#!/bin/sh
# Prints the unchanged text of the production functions behind test_arm_refusal.cpp, with #line
# directives so failures point into the real files. These files need the HAL as a whole, so the test
# compiles only these functions, against the stand-ins in stubs/. A name that is not found is an
# error: the test must never run against less than this list.
#
# PROD_ROOT reads another tree, the negative controls use that. Needs sh and awk.

set -eu
export LC_ALL=C

root=${PROD_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
motor_control=ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl

# A definition starts in column 0, either as `struct <name>` or with its name before the first
# parenthesis, and ends with a closing brace in column 0.
extract() {
    file=$1
    shift
    awk -v wanted="$*" -v shown="$file" '
    BEGIN {
        n = split(wanted, names, " ")
        for (i = 1; i <= n; i++) missing[names[i]] = 1
    }
    {
        sub(/\r$/, "")
        if (!copying && $0 ~ /^[A-Za-z_]/) {
            name = ""
            if ($0 ~ /^struct /) {
                split($0, parts, /[ :{]+/)
                name = parts[2]
            } else if (index($0, "(") > 0) {
                k = split(substr($0, 1, index($0, "(") - 1), parts, /[ \t*&]+/)
                name = parts[k]
            }
            if (name in missing) {
                delete missing[name]
                copying = 1
                printf "#line %d \"%s\"\n", FNR, shown
            }
        }
        if (copying) {
            print
            if ($0 ~ /^}/) copying = 0
        }
    }
    END {
        failed = 0
        for (name in missing) {
            printf "%s: %s not found\n", shown, name > "/dev/stderr"
            failed = 1
        }
        exit failed
    }' "$root/$file"
}

extract "$motor_control/utils.cpp" SVM

extract Odrive-Wheel/src/odrive_bridge.cpp \
    flash_guard_read_axes odrive_bridge_flash_begin odrive_bridge_flash_end \
    odrive_bridge_flash_arm_allowed odrive_bridge_flash_status

extract "$motor_control/motor.cpp" \
    ResistanceMeasurementControlLaw InductanceMeasurementControlLaw \
    Motor::arm Motor::disarm Motor::update_current_controller_gains Motor::disarm_with_error \
    Motor::measure_phase_resistance Motor::measure_phase_inductance Motor::run_calibration

extract "$motor_control/axis.cpp" \
    Axis::run_lockin_spin Axis::start_closed_loop_control Axis::stop_closed_loop_control \
    Axis::run_closed_loop_control_loop Axis::run_homing

extract "$motor_control/encoder.cpp" \
    Encoder::run_index_search Encoder::run_direction_find Encoder::run_hall_polarity_calibration \
    Encoder::run_hall_phase_calibration Encoder::run_offset_calibration
