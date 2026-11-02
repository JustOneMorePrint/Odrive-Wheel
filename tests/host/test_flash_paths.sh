#!/bin/sh
# Every call in the firmware that can write or erase the MCU flash, against the reviewed list in
# flash_paths.expected, and the position of the flash guard in every path reachable at runtime. A
# new flash call fails here until someone has decided about its guard (docs/ROADMAP.md, S1).
#
# By text, because this code needs the HAL and can't run on the PC. test_flash_guard.cpp tests the
# logic itself. Comments don't count. Needs sh, awk, grep, find and sort; no compiler.
#
# --print lists what was found, in the format of the list. FLASH_PATHS_ROOT checks another tree,
# the negative controls use that.

set -u
export LC_ALL=C

here=$(cd "$(dirname "$0")" && pwd)
root=${FLASH_PATHS_ROOT:-$(cd "$here/../.." && pwd)}
expected=$here/flash_paths.expected

firmware=ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware
ffb_task=Odrive-Wheel/src/ffb_task.cpp
cmd_table=Odrive-Wheel/src/cmd_table.cpp
bridge=Odrive-Wheel/src/odrive_bridge.cpp
odrive_main=$firmware/MotorControl/main.cpp
motor=$firmware/MotorControl/motor.cpp

# Primitives that write or erase, and the functions that lead to them.
symbols='HAL_FLASH_Program HAL_FLASH_Program_IT HAL_FLASHEx_Erase HAL_FLASHEx_Erase_IT
HAL_FLASH_Unlock HAL_FLASH_OB_Unlock HAL_FLASHEx_OBProgram FLASH_Erase_Sector
EE_WriteVariable EE_Format EE_Init EE_PageTransfer EE_VerifyPageFullWriteVariable
Flash_Write Flash_Init Flash_ReadWriteDefault
NVM_init NVM_erase NVM_start_write NVM_write NVM_commit
prepare_store start_store finish_store save_configuration erase_configuration
saveFlash gpio_inputs_save ffb_save_flash ffb_eetest ffb_eedump ffb_eeformat ffb_init_storage_early'

# Shared by both awk programs: removes comments and tracks the enclosing top-level function, which
# starts in column 0 and contains a parenthesis, and ends with a closing brace in column 0.
awk_common='
function strip(line, out, a, b) {
    out = ""
    while (line != "") {
        if (in_comment) {
            b = index(line, "*/")
            if (b == 0) return out
            line = substr(line, b + 2)
            in_comment = 0
        } else {
            a = index(line, "/*")
            b = index(line, "//")
            if (b > 0 && (a == 0 || b < a)) return out substr(line, 1, b - 1)
            if (a == 0) return out line
            out = out substr(line, 1, a - 1)
            line = substr(line, a + 2)
            in_comment = 1
        }
    }
    return out
}
function track(line, head, parts, k) {
    if (line ~ /^[A-Za-z_]/ && index(line, "(") > 0 && line !~ /;[ \t]*$/ &&
        line !~ /^(typedef|class|struct|enum|namespace|using|template)/) {
        head = substr(line, 1, index(line, "(") - 1)
        k = split(head, parts, /[ \t*&]+/)
        current = parts[k]
    } else if (line ~ /^}/) {
        was_function = current
        current = "-"
    }
}
FNR == 1 { current = "-"; in_comment = 0 }
'

list_found() {
    (cd "$root" && find Odrive-Wheel/src Odrive-Wheel/inc "$firmware" \
        -type d \( -name ThirdParty -o -name autogen \) -prune -o \
        -type f \( -name '*.c' -o -name '*.cpp' -o -name '*.h' -o -name '*.hpp' \) \
        -exec awk -v symbols="$symbols" "$awk_common"'
        BEGIN { n = split(symbols, names, /[ \n]+/); for (i = 1; i <= n; i++) wanted[names[i]] = 1 }
        {
            line = $0
            sub(/\r$/, "", line)
            line = strip(line)
            track(line)
            rest = line
            while (match(rest, /[A-Za-z_][A-Za-z0-9_]*/)) {
                token = substr(rest, RSTART, RLENGTH)
                rest = substr(rest, RSTART + RLENGTH)
                if (token == "FLASH" && substr(rest, 1, 2) == "->") token = "FLASH->"
                if (token in wanted || token == "FLASH->") count[FILENAME " " current " " token]++
            }
        }
        END { for (key in count) print key, count[key] }
        ' {} +) | sort
}

if [ "${1:-}" = "--print" ]; then
    list_found
    exit 0
fi

checks=0
failures=0

fail() {
    failures=$((failures + 1))
    printf 'FAILED: %s\n' "$1"
}

check() {
    checks=$((checks + 1))
    if ! eval "$2"; then
        fail "$1"
    fi
}

# The lines of one function, without comments, numbered from 1.
body_of() {
    awk -v wanted="$2" "$awk_common"'
    {
        line = $0
        sub(/\r$/, "", line)
        line = strip(line)
        track(line)
        if (current == wanted) print line
        else if (was_function == wanted && line ~ /^}/) { print line; was_function = "" }
    }' "$root/$1"
}

# First and last line of a body that contains a fixed text or a pattern. Empty if there is none.
first_fixed() { printf '%s\n' "$1" | grep -n -F -- "$2" | head -n 1 | cut -d: -f1; }
last_fixed() { printf '%s\n' "$1" | grep -n -F -- "$2" | tail -n 1 | cut -d: -f1; }
first_match() { printf '%s\n' "$1" | grep -n -E -- "$2" | head -n 1 | cut -d: -f1; }
last_match() { printf '%s\n' "$1" | grep -n -E -- "$2" | tail -n 1 | cut -d: -f1; }

# a exists, b exists, a comes first.
in_order() { [ -n "$1" ] && [ -n "$2" ] && [ "$1" -lt "$2" ]; }

flash_ops='HAL_FLASH_Unlock|HAL_FLASH_Program|HAL_FLASHEx_Erase|EE_WriteVariable|EE_Init|EE_Format|Flash_Write|saveFlash|gpio_inputs_save|NVM_erase|HAL_FLASH_Lock'

# Between begin and end nothing may return, otherwise the flash stays claimed and the motor can't
# be armed until the next reset.
check_runtime_path() {
    name=$2
    body=$(body_of "$1" "$2")
    begin=$(first_fixed "$body" 'odrive_bridge_flash_begin()')
    first_op=$(first_match "$body" "$flash_ops")
    last_op=$(last_match "$body" "$flash_ops")
    end=$(first_fixed "$body" 'odrive_bridge_flash_end()')
    check "$name: asks the guard before the first flash operation" 'in_order "$begin" "$first_op"'
    check "$name: gives up when refused" 'printf "%s\n" "$body" | grep -q -E "if \(!odrive_bridge_flash_begin\(\)\) return"'
    check "$name: releases the flash after the last operation" 'in_order "$last_op" "$end"'
    returns=$(printf '%s\n' "$body" | awk -v a="${begin:-0}" -v b="${end:-0}" 'NR > a && NR < b && /return/' | wc -l | tr -d ' ')
    check "$name: no return between begin and end" '[ "$returns" = 0 ]'
    ends=$(printf '%s\n' "$body" | grep -c -F 'odrive_bridge_flash_end()')
    check "$name: releases the flash exactly once" '[ "$ends" = 1 ]'
}

# ---- The list -------------------------------------------------------------------------------------

found=$(list_found)
listed=$(grep -v -E '^(#|[[:space:]]*$)' "$expected" | tr -d '\r' | sort)
check "the flash calls match flash_paths.expected" '[ "$found" = "$listed" ]'
if [ "$found" != "$listed" ]; then
    printf 'Only in the firmware:\n'
    printf '%s\n' "$found" | while IFS= read -r row; do
        printf '%s\n' "$listed" | grep -q -x -F -- "$row" || printf '  %s\n' "$row"
    done
    printf 'Only in the list:\n'
    printf '%s\n' "$listed" | while IFS= read -r row; do
        printf '%s\n' "$found" | grep -q -x -F -- "$row" || printf '  %s\n' "$row"
    done
fi

# ---- Runtime paths through sys.* ------------------------------------------------------------------

check_runtime_path "$ffb_task" ffb_save_flash
check_runtime_path "$ffb_task" ffb_eetest
check_runtime_path "$ffb_task" ffb_eedump
check_runtime_path "$ffb_task" ffb_eeformat

# The counters of sys.savestat? keep the last save if the guard refuses.
body=$(body_of "$ffb_task" ffb_save_flash)
check "ffb_save_flash: asks before it resets the counters" \
    'in_order "$(first_fixed "$body" odrive_bridge_flash_begin)" "$(first_fixed "$body" "s_last_save_writes = 0")"'

# A refusal has to reach the parser as an error, it turns that into the reply ERR.
body=$(body_of "$cmd_table" h_sys_save)
check "h_sys_save: a refusal is an error" \
    'printf "%s\n" "$body" | grep -q -F "ffb_save_flash()" && printf "%s\n" "$body" | grep -q -F "return ok ? 0 : -1"'
body=$(body_of "$cmd_table" h_sys_eetest)
check "h_sys_eetest: a refusal is an error" \
    'in_order "$(first_fixed "$body" "if (ok < 0) return -1")" "$(first_fixed "$body" "snprintf")"'
body=$(body_of "$cmd_table" h_sys_eedump)
check "h_sys_eedump: a refusal is an error" 'printf "%s\n" "$body" | grep -q -F "return ffb_eedump("'
body=$(body_of "$cmd_table" h_sys_eeformat)
check "h_sys_eeformat: a refusal is an error" 'printf "%s\n" "$body" | grep -q -F "return ffb_eeformat("'

# ---- Runtime paths through the ODrive ASCII protocol ----------------------------------------------

# se: the board resets right after, so nothing releases the flash here.
body=$(body_of "$odrive_main" 'ODrive::erase_configuration')
check "erase_configuration: asks the guard before NVM_erase" \
    'in_order "$(first_fixed "$body" "odrive_bridge_flash_begin()")" "$(first_fixed "$body" "NVM_erase")"'
check "erase_configuration: gives up when refused" \
    'in_order "$(first_fixed "$body" "if (!odrive_bridge_flash_begin())")" "$(first_fixed "$body" "return;")"'

# ss has its own check since stock ODrive, and resets before anything could arm.
body=$(body_of "$odrive_main" 'ODrive::save_configuration')
check "save_configuration: still refuses an armed motor" \
    'in_order "$(first_fixed "$body" "if (any_armed)")" "$(first_fixed "$body" "prepare_store")"'

# ---- The other side of the race -------------------------------------------------------------------

body=$(body_of "$motor" 'Motor::arm')
check "Motor::arm: asks before the brake opens" \
    'in_order "$(first_fixed "$body" "odrive_bridge_flash_arm_allowed()")" "$(first_fixed "$body" "mechanical_brake_.release")"'
check "Motor::arm: asks again in the critical section, before it arms" \
    'in_order "$(first_fixed "$body" "CRITICAL_SECTION")" "$(last_fixed "$body" "odrive_bridge_flash_arm_allowed()")" &&
     in_order "$(last_fixed "$body" "odrive_bridge_flash_arm_allowed()")" "$(first_fixed "$body" "is_armed_ = true")"'

body=$(body_of "$bridge" odrive_bridge_flash_begin)
check "odrive_bridge_flash_begin: checks and claims atomically" \
    'in_order "$(first_fixed "$body" "CRITICAL_SECTION")" "$(first_fixed "$body" "flash_guard_try_begin")"'
body=$(body_of "$bridge" flash_guard_read_axes)
check "the guard looks at all axes" 'printf "%s\n" "$body" | grep -q -F "i < AXIS_COUNT"'
check "the guard reads the armed flag" 'printf "%s\n" "$body" | grep -q -F "motor_.is_armed_"'
check "the guard reads pending state requests" 'printf "%s\n" "$body" | grep -q -F "requested_state_"'
check "the guard reads the axis state" 'printf "%s\n" "$body" | grep -q -F "current_state_"'

printf 'test_flash_paths.sh: %d checks, %d failed\n' "$checks" "$failures"
[ "$failures" -eq 0 ]
