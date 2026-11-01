#!/bin/sh
# Regression test for `make clean` of the firmware, through the real Makefile rule
# on stand-in objects. Sources outside Odrive-Wheel compile to objects next to
# build/, addressed through build/../, and those must go as well. Needs make; no
# compiler and no ARM toolchain. check_firmware_clean.sh covers a real build.

set -u
export LC_ALL=C

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
MAKE=${MAKE:-make}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

repo="$work/repo with space (x) [y]"
fw=$repo/Odrive-Wheel
objects=$work/objects.txt
keep=$work/keep.txt

checks=0
failures=0

check_equal() {
    checks=$((checks + 1))
    if [ "$3" != "$2" ]; then
        failures=$((failures + 1))
        printf 'FAILED: %s\n  expected: %s\n  actual:   %s\n' "$1" "$2" "$3"
    fi
}

mkdir -p "$fw"
cp "$root/Odrive-Wheel/Makefile" "$fw/"

# $(info) needs no shell: mingw32-make silently cuts a long recipe line it hands to sh.
cat > "$work/print_objects.mk" <<'EOF'
$(foreach o,$(OBJECTS),$(info $(o)))
print-objects: ; @:
EOF
(cd "$fw" && "$MAKE" --no-print-directory -s -f Makefile -f "$work/print_objects.mk" print-objects) |
    tr -d '\r' > "$objects"

total=$(wc -l < "$objects")
mirrored=$(grep -c '^build/\.\./' "$objects")
check_equal "the Makefile has objects next to build/" yes "$([ "$mirrored" -gt 0 ] && echo yes)"
check_equal "the Makefile has objects inside build/" yes "$([ "$total" -gt "$mirrored" ] && echo yes)"

# Same names as the objects, but in places clean must not touch: the real source
# tree one level up, and files with another suffix in the mirrored tree.
cat > "$keep" <<'EOF'
Odrive-Wheel/src/keep.cpp
Odrive-Wheel/ODrive-fw-v0.5.6/keep.txt
ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl/motor.cpp
ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl/motor.o
ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl/motor.d
EOF
(
    cd "$repo" &&
        sed 's#/[^/]*$##' "$keep" | xargs mkdir -p &&
        xargs touch < "$keep"
)

create_objects() {
    (
        cd "$fw" &&
            sed 's#^build/\.\./##' "$objects" > "$work/paths.txt" &&
            sed 's#/[^/]*$##' "$work/paths.txt" | sort -u | xargs mkdir -p &&
            xargs touch < "$work/paths.txt" &&
            sed 's/\.o$/.d/' "$work/paths.txt" | xargs touch
    )
}

objects_left() {
    find "$fw" \( -name '*.o' -o -name '*.d' \) | wc -l
}

missing_keep_files() {
    (cd "$repo" && while IFS= read -r file; do [ -e "$file" ] || echo "$file"; done < "$keep")
}

# cd instead of make -C: MSYS leaves this path unconverted for a native make.
run_clean() {
    (cd "$fw" && "$MAKE" --no-print-directory -s clean) > "$work/clean.log" 2>&1
    echo $?
}

create_objects
check_equal "stand-ins exist before clean" "$((total * 2))" "$(objects_left)"
check_equal "clean succeeds" 0 "$(run_clean)"
check_equal "clean leaves no object" 0 "$(objects_left)"
check_equal "clean removes build/" no "$([ -e "$fw/build" ] && echo yes || echo no)"
check_equal "clean keeps everything else" "" "$(missing_keep_files)"

# What the old rule left behind, and what deleting build/ by hand leaves behind.
create_objects
rm -rf "$fw/build"
check_equal "clean succeeds without build/" 0 "$(run_clean)"
check_equal "clean without build/ leaves no object" 0 "$(objects_left)"
check_equal "clean without build/ keeps everything else" "" "$(missing_keep_files)"

check_equal "clean succeeds on a clean tree" 0 "$(run_clean)"

# A delete that fails must stop a build, not leave old objects behind unnoticed.
create_objects
first=$(grep -m 1 '^build/\.\./' "$objects")
blocked=$fw/${first#build/../}
rm -f "$blocked"
mkdir "$blocked"
check_equal "failed delete fails clean" yes "$([ "$(run_clean)" -ne 0 ] && echo yes || echo no)"
rmdir "$blocked"

printf 'test_make_clean.sh: %d checks, %d failed\n' "$checks" "$failures"
[ "$failures" -eq 0 ]
