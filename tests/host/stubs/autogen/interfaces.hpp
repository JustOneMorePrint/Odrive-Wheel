// Stand-in for the generated ODrive interface, for test_arm_refusal.cpp only. The real file needs
// Python packages at build time and carries everything Fibre exposes. This holds the names that the
// production functions under test use. The values are just distinct bits.
#ifndef STUB_AUTOGEN_INTERFACES_HPP
#define STUB_AUTOGEN_INTERFACES_HPP

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <utility>

using float2D = std::pair<float, float>;

#define STUB_FLAG_OPERATORS(E)                                                                       \
    inline E operator|(E a, E b) { return static_cast<E>(static_cast<uint64_t>(a) | static_cast<uint64_t>(b)); } \
    inline E operator&(E a, E b) { return static_cast<E>(static_cast<uint64_t>(a) & static_cast<uint64_t>(b)); } \
    inline E operator~(E a) { return static_cast<E>(~static_cast<uint64_t>(a)); }                    \
    inline E &operator|=(E &a, E b) { return a = a | b; }                                            \
    inline E &operator&=(E &a, E b) { return a = a & b; }

class ODriveIntf {
public:
    class MotorIntf {
    public:
        enum Error : uint64_t {
            ERROR_NONE = 0,
            ERROR_PHASE_RESISTANCE_OUT_OF_RANGE = 1ULL << 0,
            ERROR_PHASE_INDUCTANCE_OUT_OF_RANGE = 1ULL << 1,
            ERROR_BRAKE_RESISTOR_DISARMED = 1ULL << 2,
            ERROR_UNKNOWN_VBUS_VOLTAGE = 1ULL << 3,
            ERROR_CONTROLLER_INITIALIZING = 1ULL << 4,
            ERROR_UNKNOWN_CURRENT_MEASUREMENT = 1ULL << 5,
            ERROR_MODULATION_IS_NAN = 1ULL << 6,
            ERROR_MODULATION_MAGNITUDE = 1ULL << 7,
            ERROR_UNKNOWN_VOLTAGE_COMMAND = 1ULL << 8,
            ERROR_UNKNOWN_PHASE_ESTIMATE = 1ULL << 9,
            ERROR_UNKNOWN_GAINS = 1ULL << 10,
            ERROR_UNKNOWN_CURRENT_COMMAND = 1ULL << 11,
            ERROR_BAD_TIMING = 1ULL << 12,
        };
        enum MotorType {
            MOTOR_TYPE_HIGH_CURRENT = 0,
            MOTOR_TYPE_GIMBAL = 2,
            MOTOR_TYPE_ACIM = 3,
        };
    };

    class AxisIntf {
    public:
        enum Error : uint32_t {
            ERROR_NONE = 0,
            ERROR_MOTOR_FAILED = 1u << 0,
            ERROR_MIN_ENDSTOP_PRESSED = 1u << 1,
            ERROR_HOMING_WITHOUT_ENDSTOP = 1u << 2,
            ERROR_UNKNOWN_POSITION = 1u << 3,
        };
        enum AxisState {
            AXIS_STATE_UNDEFINED = 0,
            AXIS_STATE_IDLE = 1,
            AXIS_STATE_MOTOR_CALIBRATION = 4,
            AXIS_STATE_ENCODER_INDEX_SEARCH = 6,
            AXIS_STATE_ENCODER_OFFSET_CALIBRATION = 7,
            AXIS_STATE_CLOSED_LOOP_CONTROL = 8,
        };
    };

    class EncoderIntf {
    public:
        enum Error : uint32_t {
            ERROR_NONE = 0,
            ERROR_NO_RESPONSE = 1u << 0,
            ERROR_CPR_POLEPAIRS_MISMATCH = 1u << 1,
            ERROR_INDEX_NOT_FOUND_YET = 1u << 2,
            ERROR_HALL_NOT_CALIBRATED_YET = 1u << 3,
            ERROR_ILLEGAL_HALL_STATE = 1u << 4,
        };
        enum Mode {
            MODE_INCREMENTAL = 0,
            MODE_HALL = 1,
        };
    };

    class ControllerIntf {
    public:
        enum Error : uint32_t {
            ERROR_NONE = 0,
            ERROR_INVALID_LOAD_ENCODER = 1u << 0,
        };
        enum ControlMode {
            CONTROL_MODE_TORQUE_CONTROL = 1,
            CONTROL_MODE_VELOCITY_CONTROL = 2,
            CONTROL_MODE_POSITION_CONTROL = 3,
        };
        enum InputMode {
            INPUT_MODE_PASSTHROUGH = 1,
            INPUT_MODE_VEL_RAMP = 2,
            INPUT_MODE_TRAP_TRAJ = 5,
        };
    };
};

STUB_FLAG_OPERATORS(ODriveIntf::MotorIntf::Error)
STUB_FLAG_OPERATORS(ODriveIntf::AxisIntf::Error)
STUB_FLAG_OPERATORS(ODriveIntf::EncoderIntf::Error)
STUB_FLAG_OPERATORS(ODriveIntf::ControllerIntf::Error)

#endif
