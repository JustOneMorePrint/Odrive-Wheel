// What happens when the flash guard refuses Motor::arm. Runs the real production functions, their
// text comes from motor.cpp, axis.cpp, encoder.cpp and odrive_bridge.cpp (extract_prod_functions.sh),
// on top of the real flash_guard.c, foc.cpp and open_loop_controller.cpp. Only the hardware and the
// classes around them are stand-ins (stubs/). A simulated control loop runs inside osDelay, so the
// normal way through a calibration runs as well.
//
// A refused start must leave every calibration value alone. A calibration that did start and was
// aborted later keeps its old behaviour, that is checked here too.
#include <board.h>

#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>

#include "flash_guard.h"
#include "odrive_bridge.h"
#include "test_util.hpp"

// The production text isn't written for -Wextra or for 64-bit pointers (uint32_t casts of pointers).
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wunused-parameter"
#pragma GCC diagnostic ignored "-Wsign-compare"
#pragma GCC diagnostic ignored "-Wunused-variable"
#include "../../Odrive-Wheel/src/flash_guard.c"
#include "foc.cpp"
#include "open_loop_controller.cpp"
extern "C" float our_arm_sin_f32(float x) { return sinf(x); }
extern "C" float our_arm_cos_f32(float x) { return cosf(x); }
static flash_guard_t s_flash_guard{};
#include "prod_arm_paths.inc"
#pragma GCC diagnostic pop

Axis axes[AXIS_COUNT];

// ---- Simulation -------------------------------------------------------------------------------------

static const float kRealResistance = 0.1f;   // [Ohm]
static const float kRealInductance = 100e-6f;  // [H]
static const float kPi = 3.14159265f;

struct Simulation {
    uint32_t timestamp = 0;
    int ms = 0;
    float current = 0.0f;
    float voltage_now = 0.0f;
    float voltage_next = 0.0f;
    float encoder_counts = 0.0f;
    float last_distance = 0.0f;
    int hall_step = 0;
    std::function<void(int)> on_ms;
};
static Simulation sim;

static void measurement_law_cycle(Motor &motor) {
    if (auto *law = dynamic_cast<ResistanceMeasurementControlLaw *>(motor.control_law_)) {
        sim.current = law->test_voltage_ / kRealResistance;
    } else if (dynamic_cast<InductanceMeasurementControlLaw *>(motor.control_law_)) {
        // The output of one cycle takes effect one cycle later (see InductanceMeasurementControlLaw).
        sim.current += sim.voltage_now * current_meas_period / kRealInductance;
    } else {
        return;
    }

    std::array<float, 3> currents = {sim.current, -sim.current / 2.0f, -sim.current / 2.0f};
    Motor::Error error = motor.control_law_->on_measurement(vbus_voltage, currents, sim.timestamp);
    float timings[3];
    std::optional<float> ibus;
    if (error == Motor::ERROR_NONE) error = motor.control_law_->get_output(sim.timestamp, timings, &ibus);
    if (error != Motor::ERROR_NONE && error != Motor::ERROR_CONTROLLER_INITIALIZING) {
        motor.disarm_with_error(error);
        return;
    }
    if (auto *law = dynamic_cast<InductanceMeasurementControlLaw *>(motor.control_law_)) {
        sim.voltage_now = sim.voltage_next;
        sim.voltage_next = law->test_voltage_;
    }
}

static void open_loop_cycle(Axis &axis) {
    OpenLoopController &open_loop = axis.open_loop_controller_;
    open_loop.Idq_setpoint_.reset();
    open_loop.Vdq_setpoint_.reset();
    open_loop.phase_.reset();
    open_loop.phase_vel_.reset();
    open_loop.total_distance_.reset();
    open_loop.update(sim.timestamp);

    Encoder &encoder = axis.encoder_;
    const float distance = open_loop.total_distance_.any().value_or(0.0f);
    const float elec_rad_per_count = axis.motor_.config_.pole_pairs * 2.0f * kPi / (float)encoder.config_.cpr;
    sim.encoder_counts += (distance - sim.last_distance) / elec_rad_per_count;
    sim.last_distance = distance;
    const int32_t whole_counts = (int32_t)sim.encoder_counts;
    sim.encoder_counts -= (float)whole_counts;
    encoder.shadow_count_ += whole_counts;
    encoder.count_in_cpr_ += whole_counts;
}

// The parts of Encoder::update that the hall calibrations rely on, in simplified form.
static void hall_cycle(Encoder &encoder, int step) {
    static const uint8_t kHallSequence[6] = {0b001, 0b011, 0b010, 0b110, 0b100, 0b101};
    encoder.hall_state_ = kHallSequence[step];
    if (encoder.sample_hall_states_) encoder.states_seen_count_[encoder.hall_state_]++;
    if (encoder.calibrate_hall_phase_ && encoder.sample_hall_phase_) {
        encoder.hall_phase_calib_seen_count_[step]++;
        encoder.config_.hall_edge_phcnt[step] = wrap_pm_pi((float)step * 2.0f * kPi / 6.0f);
    }
}

// All blocking in the functions under test goes through here, so this is where the control loop runs.
void osDelay(uint32_t ms) {
    for (uint32_t i = 0; i < ms; i++) {
        if (++sim.ms > 120000) {
            std::printf("FAILED: the function under test does not return\n");
            std::exit(1);
        }
        if (sim.on_ms) sim.on_ms(sim.ms);
        for (int cycle = 0; cycle < 8; cycle++) {
            sim.timestamp += 2 * TIM_1_8_PERIOD_CLOCKS * (TIM_1_8_RCR + 1);
            const int hall_step = sim.hall_step++ % 6;
            for (Axis &axis : axes) {
                hall_cycle(axis.encoder_, hall_step);
                if (!axis.motor_.is_armed_) continue;
                measurement_law_cycle(axis.motor_);
                if (axis.motor_.is_armed_) open_loop_cycle(axis);
            }
        }
    }
}

static Axis &fresh_axis() {
    for (Axis &axis : axes) {
        axis.~Axis();
        new (&axis) Axis();
        axis.motor_.axis_ = &axis;
        axis.encoder_.axis_ = &axis;
    }
    axes[1].axis_num_ = 1;
    sim = Simulation();
    s_flash_guard = flash_guard_t{};
    on_brake_current_update = nullptr;
    odrv = ODrive();
    brake_resistor_armed = true;
    return axes[0];
}

// A flash operation is running, then a request arrives over another interface such as CAN or UART.
static void flash_operation_running() {
    CHECK(odrive_bridge_flash_begin());
}

static std::string guard_status() {
    char text[128];
    odrive_bridge_flash_status(text, sizeof(text));
    return text;
}

static const std::array<float, 6> kOwnHallEdges = {0.25f, 1.25f, 2.25f, 3.25f, 4.25f, 5.25f};

// ---- Motor calibration ------------------------------------------------------------------------------

static void set_old_motor_calibration(Motor &motor) {
    motor.config_.phase_resistance = 0.123f;
    motor.config_.phase_inductance = 456e-6f;
    motor.is_calibrated_ = true;
    motor.update_current_controller_gains();
}

static void check_old_motor_calibration(Motor &motor) {
    CHECK(motor.config_.phase_resistance == 0.123f);
    CHECK(motor.config_.phase_inductance == 456e-6f);
    CHECK(motor.is_calibrated_);
    CHECK(motor.error_ == Motor::ERROR_NONE);
    CHECK(!motor.is_armed_);
}

static void test_refused_resistance_measurement() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    flash_operation_running();

    CHECK(!axis.motor_.measure_phase_resistance(10.0f, 2.0f));
    check_old_motor_calibration(axis.motor_);
    CHECK(axis.motor_.arm_refused_);
    CHECK(axis.mechanical_brake_.releases == 0);
    CHECK(axis.motor_.control_law_ == nullptr);
    CHECK(guard_status() == "now=busy blocked=0 last=none armrefused=1");

    // Without a test current the old code divided 0 by 0 and reported an error.
    CHECK(!axis.motor_.measure_phase_resistance(0.0f, 2.0f));
    check_old_motor_calibration(axis.motor_);
}

static void test_refused_inductance_measurement() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    flash_operation_running();

    CHECK(!axis.motor_.measure_phase_inductance(2.0f));
    check_old_motor_calibration(axis.motor_);
    CHECK(axis.motor_.arm_refused_);
}

static void test_refused_motor_calibration() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    const auto gains = axis.motor_.current_control_.pi_gains_;
    flash_operation_running();

    CHECK(!axis.motor_.run_calibration());
    check_old_motor_calibration(axis.motor_);
    CHECK(axis.motor_.current_control_.pi_gains_ == gains);
    odrive_bridge_flash_end();

    // A motor without a calibration doesn't get one either.
    axis.motor_.is_calibrated_ = false;
    flash_operation_running();
    CHECK(!axis.motor_.run_calibration());
    CHECK(!axis.motor_.is_calibrated_);
}

// By the logic of the guard this can't happen, a flash operation doesn't start during a calibration.
// The resistance really was measured, so it stays. The inductance was not.
static void test_motor_calibration_refused_after_the_resistance() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    axis.motor_.is_calibrated_ = false;
    on_brake_current_update = [] { CHECK(odrive_bridge_flash_begin()); };

    CHECK(!axis.motor_.run_calibration());
    CHECK_NEAR(axis.motor_.config_.phase_resistance, kRealResistance, 0.002f);
    CHECK(axis.motor_.config_.phase_inductance == 456e-6f);
    CHECK(!axis.motor_.is_calibrated_);
    CHECK(axis.motor_.error_ == Motor::ERROR_NONE);
}

static void test_motor_calibration_normal_way() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    axis.motor_.is_calibrated_ = false;

    CHECK(axis.motor_.run_calibration());
    CHECK_NEAR(axis.motor_.config_.phase_resistance, kRealResistance, 0.002f);
    CHECK_NEAR(axis.motor_.config_.phase_inductance, kRealInductance, 2e-6f);
    CHECK(axis.motor_.is_calibrated_);
    CHECK(axis.motor_.error_ == Motor::ERROR_NONE);
    CHECK(!axis.motor_.is_armed_);
    CHECK(!axis.motor_.arm_refused_);
    CHECK_NEAR(axis.motor_.current_control_.pi_gains_->first, 1000.0f * kRealInductance, 0.003f);
    CHECK(guard_status() == "now=allowed blocked=0 last=none armrefused=0");
}

// An aborted measurement did start, so as before it writes what it has and counts as a success.
static void test_aborted_resistance_measurement_keeps_its_behaviour() {
    Axis &axis = fresh_axis();
    set_old_motor_calibration(axis.motor_);
    sim.on_ms = [&axis](int ms) {
        if (ms == 50) axis.requested_state_ = Axis::AXIS_STATE_IDLE;
    };

    CHECK(axis.motor_.measure_phase_resistance(10.0f, 2.0f));
    CHECK(axis.motor_.config_.phase_resistance != 0.123f);
    CHECK(!axis.motor_.arm_refused_);
}

// ---- Motor::arm and the guard ------------------------------------------------------------------------

static void test_arm_race_with_a_flash_operation() {
    // The flash operation slips in between the first check and the critical section.
    Axis &axis = fresh_axis();
    axis.mechanical_brake_.on_release = [] { CHECK(odrive_bridge_flash_begin()); };
    CHECK(!axis.motor_.arm(&axis.motor_.current_control_));
    CHECK(!axis.motor_.is_armed_);
    CHECK(axis.motor_.arm_refused_);
    CHECK(axis.motor_.control_law_ == nullptr);
    CHECK(guard_status() == "now=busy blocked=0 last=none armrefused=1");

    axis.mechanical_brake_.on_release = nullptr;
    odrive_bridge_flash_end();
    CHECK(axis.motor_.arm(&axis.motor_.current_control_));
    CHECK(axis.motor_.is_armed_);
    CHECK(!axis.motor_.arm_refused_);
    CHECK(axis.motor_.control_law_ == &axis.motor_.current_control_);
}

// The other direction, through the real bridge: both axes count, and all three states.
static void test_flash_operation_refused_with_an_active_axis() {
    for (Axis *target : {&axes[0], &axes[1]}) {
        fresh_axis();
        CHECK(target->motor_.arm(&target->motor_.current_control_));
        CHECK(!odrive_bridge_flash_begin());
        CHECK(guard_status() == "now=armed blocked=1 last=armed armrefused=0");
        target->motor_.disarm();

        target->requested_state_ = Axis::AXIS_STATE_CLOSED_LOOP_CONTROL;
        CHECK(!odrive_bridge_flash_begin());
        CHECK(guard_status() == "now=requested blocked=2 last=requested armrefused=0");
        target->requested_state_ = Axis::AXIS_STATE_UNDEFINED;

        target->current_state_ = Axis::AXIS_STATE_MOTOR_CALIBRATION;
        CHECK(!odrive_bridge_flash_begin());
        CHECK(guard_status() == "now=active blocked=3 last=active armrefused=0");
        target->current_state_ = Axis::AXIS_STATE_IDLE;

        CHECK(odrive_bridge_flash_begin());
        CHECK(!odrive_bridge_flash_begin());
        CHECK(guard_status() == "now=busy blocked=4 last=busy armrefused=0");
        odrive_bridge_flash_end();
        CHECK(guard_status() == "now=allowed blocked=4 last=busy armrefused=0");
    }
}

// ---- Hall calibrations --------------------------------------------------------------------------------

static void test_refused_hall_polarity_calibration() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_polarity = 0b010;
    encoder.config_.hall_polarity_calibrated = true;
    encoder.states_seen_count_.fill(7);
    flash_operation_running();

    CHECK(!encoder.run_hall_polarity_calibration());
    CHECK(encoder.config_.hall_polarity_calibrated);
    CHECK(encoder.config_.hall_polarity == 0b010);
    CHECK(encoder.states_seen_count_[3] == 7);
    CHECK(!encoder.sample_hall_states_);
    CHECK(encoder.error_ == Encoder::ERROR_NONE);
    CHECK(!axis.motor_.is_armed_);
    CHECK(axis.motor_.arm_refused_);
}

static void test_hall_polarity_calibration_normal_way() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_polarity = 0b010;
    encoder.config_.hall_polarity_calibrated = false;

    CHECK(encoder.run_hall_polarity_calibration());
    CHECK(encoder.config_.hall_polarity_calibrated);
    CHECK(encoder.config_.hall_polarity == 0b000);
    CHECK(encoder.states_seen_count_[0] == 0);
    CHECK(encoder.states_seen_count_[1] > 50);
    CHECK(!axis.motor_.is_armed_);
}

static void test_aborted_hall_polarity_calibration_keeps_its_behaviour() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_polarity_calibrated = true;
    sim.on_ms = [&axis](int ms) {
        if (ms == 50) axis.requested_state_ = Axis::AXIS_STATE_IDLE;
    };

    CHECK(!encoder.run_hall_polarity_calibration());
    CHECK(!encoder.config_.hall_polarity_calibrated);
    CHECK(!axis.motor_.arm_refused_);
}

static void test_refused_hall_phase_calibration() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_edge_phcnt = kOwnHallEdges;
    encoder.config_.direction = -1;
    encoder.hall_phase_calib_seen_count_.fill(9);
    flash_operation_running();

    CHECK(!encoder.run_hall_phase_calibration());
    CHECK(encoder.config_.hall_edge_phcnt == kOwnHallEdges);
    CHECK(encoder.config_.direction == -1);
    CHECK(encoder.hall_phase_calib_seen_count_[2] == 9);
    CHECK(!encoder.calibrate_hall_phase_);
    CHECK(encoder.error_ == Encoder::ERROR_NONE);
    CHECK(!axis.motor_.is_armed_);
    CHECK(axis.motor_.arm_refused_);
}

static void test_hall_phase_calibration_normal_way() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_edge_phcnt = kOwnHallEdges;

    CHECK(encoder.run_hall_phase_calibration());
    for (int i = 0; i < 6; i++) CHECK_NEAR(encoder.config_.hall_edge_phcnt[i], (float)i, 0.001f);
    CHECK(encoder.config_.direction == 1);
    CHECK(!encoder.calibrate_hall_phase_);
    CHECK(!axis.motor_.is_armed_);
}

static void test_aborted_hall_phase_calibration_keeps_its_behaviour() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.hall_edge_phcnt = kOwnHallEdges;
    sim.on_ms = [&axis](int ms) {
        if (ms == 50) axis.requested_state_ = Axis::AXIS_STATE_IDLE;
    };

    CHECK(!encoder.run_hall_phase_calibration());
    CHECK(encoder.config_.hall_edge_phcnt == Encoder::hall_edge_defaults);
    CHECK(!encoder.calibrate_hall_phase_);
}

// ---- Index search, direction, encoder offset ---------------------------------------------------------

static void test_refused_index_search() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.use_index = false;
    encoder.index_found_ = true;
    flash_operation_running();

    CHECK(!encoder.run_index_search());
    CHECK(!encoder.config_.use_index);
    CHECK(encoder.index_found_);
    CHECK(encoder.idx_subscribe_calls_ == 0);
    CHECK(!axis.motor_.is_armed_);
}

static void test_index_search_normal_way() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.index_found_ = true;  // from an earlier search, must not end this one at once
    axis.config_.calibration_lockin.finish_on_distance = false;
    int ms_when_found = 0;
    sim.on_ms = [&encoder, &ms_when_found](int ms) {
        if (ms == 100) {
            encoder.index_found_ = true;
            ms_when_found = ms;
        }
    };

    CHECK(encoder.run_index_search());
    CHECK(ms_when_found == 100);
    CHECK(sim.ms >= 100);
    CHECK(encoder.config_.use_index);
    CHECK(encoder.idx_subscribe_calls_ == 1);
    CHECK(!axis.motor_.is_armed_);
}

static void test_refused_direction_find() {
    Axis &axis = fresh_axis();
    axis.encoder_.config_.direction = -1;
    flash_operation_running();

    CHECK(!axis.encoder_.run_direction_find());
    CHECK(axis.encoder_.config_.direction == -1);
    CHECK(!axis.motor_.is_armed_);
}

static void test_direction_find_normal_way() {
    Axis &axis = fresh_axis();
    axis.encoder_.config_.direction = -1;

    CHECK(axis.encoder_.run_direction_find());
    CHECK(axis.encoder_.config_.direction == 1);
}

static void test_refused_offset_calibration() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.phase_offset = 111;
    encoder.config_.phase_offset_float = 0.25f;
    encoder.config_.direction = -1;
    encoder.is_ready_ = true;
    // Several turns from zero: the position of the FFB layer comes from this counter.
    encoder.shadow_count_ = 30000;
    encoder.count_in_cpr_ = 5424;
    flash_operation_running();

    CHECK(!encoder.run_offset_calibration());
    CHECK(encoder.shadow_count_ == 30000);
    CHECK(encoder.count_in_cpr_ == 5424);
    CHECK(encoder.config_.phase_offset == 111);
    CHECK(encoder.config_.phase_offset_float == 0.25f);
    CHECK(encoder.config_.direction == -1);
    CHECK(encoder.is_ready_);
    CHECK(encoder.error_ == Encoder::ERROR_NONE);
    CHECK(!axis.motor_.is_armed_);
}

static void test_offset_calibration_normal_way() {
    Axis &axis = fresh_axis();
    Encoder &encoder = axis.encoder_;
    encoder.config_.phase_offset = 111;
    encoder.config_.direction = -1;
    encoder.shadow_count_ = 30000;
    encoder.count_in_cpr_ = 5424;

    CHECK(encoder.run_offset_calibration());
    CHECK(encoder.error_ == Encoder::ERROR_NONE);
    CHECK(encoder.config_.direction == 1);
    CHECK(encoder.config_.phase_offset != 111);
    CHECK(encoder.is_ready_);
    // Synced to the counter within one turn before the scan, which ends where it started.
    CHECK(std::abs(encoder.shadow_count_ - encoder.count_in_cpr_) < encoder.config_.cpr);
    CHECK(!axis.motor_.is_armed_);
}

// ---- Lock-in, closed loop, homing ----------------------------------------------------------------------

static void test_refused_lockin_spin() {
    Axis &axis = fresh_axis();
    flash_operation_running();

    int loop_calls = 0;
    CHECK(!axis.run_lockin_spin(axis.config_.calibration_lockin, false, [&loop_calls](bool) {
        loop_calls++;
        return true;
    }));
    CHECK(loop_calls == 0);
    CHECK(sim.ms == 0);
    CHECK(!axis.motor_.is_armed_);
}

// What a caller changes for the spin happens only on a real start, and before the spin looks at it.
static void test_lockin_on_armed() {
    Axis &axis = fresh_axis();
    flash_operation_running();
    int on_armed_calls = 0;
    auto on_armed = [&axis, &on_armed_calls] {
        CHECK(axis.motor_.is_armed_);
        on_armed_calls++;
    };
    CHECK(!axis.run_lockin_spin(axis.config_.calibration_lockin, false, {}, on_armed));
    CHECK(on_armed_calls == 0);

    odrive_bridge_flash_end();
    bool loop_ran_after_on_armed = false;
    auto loop_cb = [&on_armed_calls, &loop_ran_after_on_armed](bool) {
        loop_ran_after_on_armed = on_armed_calls == 1;
        return true;
    };
    CHECK(axis.run_lockin_spin(axis.config_.calibration_lockin, false, loop_cb, on_armed));
    CHECK(on_armed_calls == 1);
    CHECK(loop_ran_after_on_armed);
}

static void test_refused_closed_loop() {
    Axis &axis = fresh_axis();
    flash_operation_running();

    CHECK(!axis.start_closed_loop_control());
    CHECK(!axis.motor_.is_armed_);
    CHECK(axis.motor_.arm_refused_);
    // The loop ends at once and the axis goes back to idle without a new error.
    CHECK(axis.run_closed_loop_control_loop());
    CHECK(axis.error_ == Axis::ERROR_NONE);
    CHECK(axis.motor_.error_ == Motor::ERROR_NONE);
    CHECK(sim.ms == 0);
}

static void test_refused_closed_loop_in_sensorless_mode() {
    Axis &axis = fresh_axis();
    axis.config_.enable_sensorless_mode = true;
    flash_operation_running();

    CHECK(!axis.start_closed_loop_control());
    CHECK(!axis.motor_.is_armed_);
    CHECK(sim.ms == 0);
}

static void test_closed_loop_normal_way() {
    Axis &axis = fresh_axis();
    sim.on_ms = [&axis](int ms) {
        CHECK(axis.motor_.is_armed_);
        if (ms == 20) axis.requested_state_ = Axis::AXIS_STATE_IDLE;
    };

    CHECK(axis.run_closed_loop_control_loop());
    CHECK(sim.ms == 20);
    CHECK(axis.mechanical_brake_.releases == 1);
    CHECK(!axis.motor_.is_armed_);
}

static void test_refused_homing() {
    Axis &axis = fresh_axis();
    axis.homing_.is_homed = true;
    axis.error_ = Axis::ERROR_MIN_ENDSTOP_PRESSED;
    axis.encoder_.set_linear_count_calls_ = 0;
    flash_operation_running();

    CHECK(!axis.run_homing());
    CHECK(axis.homing_.is_homed);
    CHECK(axis.error_ == Axis::ERROR_MIN_ENDSTOP_PRESSED);
    CHECK(axis.encoder_.set_linear_count_calls_ == 0);
    CHECK(!axis.motor_.is_armed_);
    CHECK(sim.ms == 0);
}

// A start that fails before the arm is not a refusal, however old the last refusal is. As before, the
// old homing result is gone then.
static void test_homing_with_an_older_refusal() {
    Axis &axis = fresh_axis();
    axis.homing_.is_homed = true;
    axis.motor_.arm_refused_ = true;
    axis.controller_.config_.load_encoder_axis = AXIS_COUNT;

    CHECK(!axis.run_homing());
    CHECK(!axis.homing_.is_homed);
    CHECK(!axis.motor_.arm_refused_);
}

// The wheel already moved, so the old result no longer holds, but the position must not be zeroed:
// without the arm the second phase never ran. As in the motor calibration, the logic of the guard
// should keep this from happening.
static void test_homing_refused_in_the_second_phase() {
    Axis &axis = fresh_axis();
    axis.homing_.is_homed = true;
    sim.on_ms = [&axis](int ms) {
        if (ms == 10) axis.min_endstop_.pressed = true;
    };
    on_brake_current_update = [] { CHECK(odrive_bridge_flash_begin()); };

    CHECK(!axis.run_homing());
    CHECK(!axis.homing_.is_homed);
    CHECK(axis.encoder_.set_linear_count_calls_ == 0);
    CHECK(!axis.motor_.is_armed_);
}

static void test_homing_normal_way() {
    Axis &axis = fresh_axis();
    axis.homing_.is_homed = false;
    sim.on_ms = [&axis](int ms) {
        if (ms == 10) axis.min_endstop_.pressed = true;
        if (ms == 20) axis.controller_.trajectory_done_ = true;
    };

    CHECK(axis.run_homing());
    CHECK(axis.homing_.is_homed);
    CHECK(axis.encoder_.set_linear_count_calls_ == 1);
    CHECK(!axis.motor_.is_armed_);
}

int main() {
    test_refused_resistance_measurement();
    test_refused_inductance_measurement();
    test_refused_motor_calibration();
    test_motor_calibration_refused_after_the_resistance();
    test_motor_calibration_normal_way();
    test_aborted_resistance_measurement_keeps_its_behaviour();
    test_arm_race_with_a_flash_operation();
    test_flash_operation_refused_with_an_active_axis();
    test_refused_hall_polarity_calibration();
    test_hall_polarity_calibration_normal_way();
    test_aborted_hall_polarity_calibration_keeps_its_behaviour();
    test_refused_hall_phase_calibration();
    test_hall_phase_calibration_normal_way();
    test_aborted_hall_phase_calibration_keeps_its_behaviour();
    test_refused_index_search();
    test_index_search_normal_way();
    test_refused_direction_find();
    test_direction_find_normal_way();
    test_refused_offset_calibration();
    test_offset_calibration_normal_way();
    test_refused_lockin_spin();
    test_lockin_on_armed();
    test_refused_closed_loop();
    test_refused_closed_loop_in_sensorless_mode();
    test_closed_loop_normal_way();
    test_refused_homing();
    test_homing_with_an_older_refusal();
    test_homing_refused_in_the_second_phase();
    test_homing_normal_way();
    return TEST_RESULT();
}
