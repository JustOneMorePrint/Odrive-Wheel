// Stand-ins for everything around the production functions in test_arm_refusal.cpp: the hardware, the
// RTOS and the classes those functions belong to. The member names are the real ones, so the
// unchanged function text compiles against them. Whether they fit the real classes shows the
// firmware build. component.hpp, phase_control_law.hpp, foc.hpp, open_loop_controller.hpp and
// utils.hpp are the real headers.
#ifndef STUB_BOARD_H
#define STUB_BOARD_H

#include <autogen/interfaces.hpp>

#include <algorithm>
#include <array>
#include <bitset>
#include <cmath>
#include <cstdint>
#include <functional>
#include <limits>
#include <optional>
#include <tuple>

#include "utils.hpp"
#include "component.hpp"
#include "phase_control_law.hpp"
#include "foc.hpp"
#include "open_loop_controller.hpp"

// ---- Timing, same values as Board/v3/Inc/board.h ------------------------------------------------

#define TIM_1_8_CLOCK_HZ 168000000
#define TIM_1_8_PERIOD_CLOCKS 3500
#define TIM_1_8_RCR 2
#define AXIS_COUNT (2)
#define MAX_CONTROL_LOOP_UPDATE_TO_CURRENT_UPDATE_DELTA (TIM_1_8_PERIOD_CLOCKS / 2 + 1 * 128)
static const float current_meas_period = (float)2 * TIM_1_8_PERIOD_CLOCKS * (TIM_1_8_RCR + 1) / (float)TIM_1_8_CLOCK_HZ;

// ---- RTOS and MCU ----------------------------------------------------------------------------------

// Interrupts don't exist here, the simulation in the test runs inside osDelay.
#define CRITICAL_SECTION() for (int critical_section_once = 1; critical_section_once; critical_section_once = 0)
#define __DMB() ((void)0)

void osDelay(uint32_t ms);

struct TIM_TypeDef { uint32_t BDTR = 0; };
struct TIM_HandleTypeDef { TIM_TypeDef *Instance; };
#define TIM_BDTR_AOE 0u
#define __HAL_TIM_MOE_DISABLE_UNCONDITIONALLY(handle) ((void)(handle))

extern "C" {
inline void odrive_drv_trace(uint8_t) {}
inline volatile uint32_t g_arm_call_cnt, g_arm_success_cnt, g_arm_last_cl_ptr, g_disarm_call_cnt,
    g_disarm_caller, g_disarm_idx, g_disarm_callers[8], g_last_disarm_error,
    g_brake_resistor_armed_snapshot, g_enable_brake_snapshot;
}

inline float vbus_voltage = 24.0f;
inline bool brake_resistor_armed = true;

// Called by Motor::disarm after a motor was armed, the tests hook in there.
inline std::function<void()> on_brake_current_update;
inline void update_brake_current() {
    if (on_brake_current_update) on_brake_current_update();
}

struct ODrive {
    struct { bool enable_brake_resistor = false; } config_;
    bool misconfigured_ = false;
    uint32_t n_evt_control_loop_ = 0;
};
inline ODrive odrv;

// ---- The classes around the functions under test -----------------------------------------------------

class Axis;

class Motor : public ODriveIntf::MotorIntf {
public:
    struct Config_t {
        float phase_inductance = 0.0f;
        float phase_resistance = 0.0f;
        float current_control_bandwidth = 1000.0f;
        float current_control_deadband = 0.0f;
        float calibration_current = 10.0f;
        float resistance_calib_max_voltage = 2.0f;
        int32_t pole_pairs = 7;
        MotorType motor_type = MOTOR_TYPE_HIGH_CURRENT;
    };

    struct GateDriver { void set_enabled(bool) {} };

    bool arm(PhaseControlLaw<3> *control_law);
    bool disarm(bool *was_armed = nullptr);
    void update_current_controller_gains();
    void disarm_with_error(Error error);
    bool measure_phase_resistance(float test_current, float max_voltage);
    bool measure_phase_inductance(float test_voltage);
    bool run_calibration();

    TIM_TypeDef timer_instance_;
    TIM_HandleTypeDef timer_storage_{&timer_instance_};
    TIM_HandleTypeDef *const timer_ = &timer_storage_;
    GateDriver gate_driver_;
    Config_t config_;
    Axis *axis_ = nullptr;
    Error error_ = ERROR_NONE;
    float last_error_time_ = 0.0f;
    bool is_armed_ = false;
    uint8_t armed_state_ = 0;
    bool is_calibrated_ = false;
    bool arm_refused_ = false;
    FieldOrientedController current_control_;
    InputPort<float> torque_setpoint_src_;
    InputPort<float> phase_vel_src_;
    float direction_ = 0.0f;
    OutputPort<float2D> Vdq_setpoint_ = {{0.0f, 0.0f}};
    OutputPort<float2D> Idq_setpoint_ = {{0.0f, 0.0f}};
    PhaseControlLaw<3> *control_law_ = nullptr;
};

class Encoder : public ODriveIntf::EncoderIntf {
public:
    static constexpr std::array<float, 6> hall_edge_defaults = {0.0f, 1.0f, 2.0f, 3.0f, 4.0f, 5.0f};

    struct Config_t {
        Mode mode = MODE_INCREMENTAL;
        bool use_index = false;
        bool find_idx_on_lockin_only = false;
        int32_t cpr = 8192;
        int32_t phase_offset = 0;
        float phase_offset_float = 0.0f;
        int32_t direction = 0;
        float calib_range = 0.02f;
        float calib_scan_distance = 16.0f * 3.14159265f;
        float calib_scan_omega = 4.0f * 3.14159265f;
        uint8_t hall_polarity = 0;
        bool hall_polarity_calibrated = false;
        std::array<float, 6> hall_edge_phcnt = hall_edge_defaults;
    };

    void set_error(Error error) { error_ |= error; }
    void set_idx_subscribe(bool = false) { idx_subscribe_calls_++; }
    void set_linear_count(int32_t) { set_linear_count_calls_++; }

    bool run_index_search();
    bool run_direction_find();
    bool run_hall_polarity_calibration();
    bool run_hall_phase_calibration();
    bool run_offset_calibration();

    Axis *axis_ = nullptr;
    Config_t config_;
    Error error_ = ERROR_NONE;
    bool index_found_ = false;
    bool is_ready_ = false;
    int32_t shadow_count_ = 0;
    int32_t count_in_cpr_ = 0;
    float calib_scan_response_ = 0.0f;
    OutputPort<float> phase_ = 0.0f;
    OutputPort<float> phase_vel_ = 0.0f;
    OutputPort<float> pos_estimate_ = 0.0f;
    OutputPort<float> vel_estimate_ = 0.0f;
    OutputPort<float> pos_circular_ = 0.0f;
    uint8_t hall_state_ = 0;
    bool calibrate_hall_phase_ = false;
    bool sample_hall_states_ = false;
    bool sample_hall_phase_ = false;
    std::array<int, 8> states_seen_count_{};
    std::array<int, 6> hall_phase_calib_seen_count_{};

    int idx_subscribe_calls_ = 0;
    int set_linear_count_calls_ = 0;
};

class Controller : public ODriveIntf::ControllerIntf {
public:
    struct Config_t {
        ControlMode control_mode = CONTROL_MODE_TORQUE_CONTROL;
        InputMode input_mode = INPUT_MODE_PASSTHROUGH;
        float homing_speed = 0.25f;
        float circular_setpoint_range = 1.0f;
        uint8_t load_encoder_axis = 0;
    };

    void reset() {}
    bool control_mode_updated() { return true; }
    void input_pos_updated() {}
    void set_error(Error error) { error_ |= error; }

    Config_t config_;
    Error error_ = ERROR_NONE;
    InputPort<float> pos_estimate_linear_src_;
    InputPort<float> pos_estimate_circular_src_;
    InputPort<float> pos_wrap_src_;
    InputPort<float> vel_estimate_src_;
    OutputPort<float> torque_output_ = 0.0f;
    float input_pos_ = 0.0f;
    float input_vel_ = 0.0f;
    float input_torque_ = 0.0f;
    float pos_setpoint_ = 0.0f;
    float vel_setpoint_ = 0.0f;
    float vel_integrator_torque_ = 0.0f;
    bool trajectory_done_ = false;
};

struct AcimEstimator {
    InputPort<float> rotor_phase_src_;
    InputPort<float> rotor_phase_vel_src_;
    OutputPort<float> stator_phase_ = 0.0f;
    OutputPort<float> stator_phase_vel_ = 0.0f;
    float rotor_flux_ = 0.0f;
};

struct SensorlessEstimator {
    OutputPort<float> phase_ = 0.0f;
    OutputPort<float> phase_vel_ = 0.0f;
    OutputPort<float> vel_estimate_ = 0.0f;
};

// The tests set on_release to step in between the two checks of Motor::arm.
struct MechanicalBrake {
    void release() {
        releases++;
        if (on_release) on_release();
    }
    int releases = 0;
    std::function<void()> on_release;
};

struct Endstop {
    struct { bool enabled = true; float offset = 0.0f; } config_;
    bool get_state() { return pressed; }
    bool pressed = false;
};

class Axis : public ODriveIntf::AxisIntf {
public:
    struct LockinConfig_t {
        float current = 10.0f;
        float ramp_time = 0.4f;
        float accel = 20.0f;
        float vel = 40.0f;
        float finish_distance = 100.0f;
        bool finish_on_vel = false;
        bool finish_on_distance = true;
        bool finish_on_enc_idx = true;
    };

    struct Config_t {
        bool enable_sensorless_mode = false;
        bool enable_step_dir = false;
        bool step_dir_always_on = false;
        LockinConfig_t calibration_lockin;
        LockinConfig_t sensorless_ramp;
    };

    bool wait_for_control_iteration() { return true; }
    void set_step_dir_active(bool) {}
    bool check_for_errors() { return error_ == ERROR_NONE; }

    bool start_closed_loop_control();
    bool stop_closed_loop_control();
    bool run_lockin_spin(const LockinConfig_t &lockin_config, bool remain_armed,
                std::function<bool(bool)> loop_cb = {}, std::function<void()> on_armed = {} );
    bool run_closed_loop_control_loop();
    bool run_homing();

    int axis_num_ = 0;
    Config_t config_;
    Encoder encoder_;
    AcimEstimator acim_estimator_;
    SensorlessEstimator sensorless_estimator_;
    Controller controller_;
    OpenLoopController open_loop_controller_;
    Motor motor_;
    Endstop min_endstop_;
    MechanicalBrake mechanical_brake_;
    Error error_ = ERROR_NONE;
    AxisState requested_state_ = AXIS_STATE_UNDEFINED;
    std::array<AxisState, 10> task_chain_ = {AXIS_STATE_IDLE};
    AxisState &current_state_ = task_chain_.front();
    struct { bool is_homed = false; } homing_;
};

extern Axis axes[AXIS_COUNT];

#endif
