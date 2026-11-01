#!/bin/sh
# Checks make clean on a finished firmware build. Run it from a clean checkout
# after `make` in Odrive-Wheel, with the same environment as that build; it needs
# the ARM toolchain and calls make directly, so autogen does not run again. Set
# MAKE to use another make, e.g. mingw32-make.
# The binary is rebuilt afterwards, or the script fails:
#   1. clean leaves no object or dependency file and changes no tracked file,
#   2. the next build compiles every source again,
#   3. that build reproduces the original binary.

set -eu
export LC_ALL=C

# See test_build_info.sh: keeps the branch name intact on its way into a native make.
export MSYS2_ENV_CONV_EXCL="${MSYS2_ENV_CONV_EXCL:+$MSYS2_ENV_CONV_EXCL;}BUILD_GIT_BRANCH"

cd "$(dirname "$0")/../../Odrive-Wheel"

bin=build/odrive-wheel.bin
log=$(mktemp)
trap 'rm -f "$log"' EXIT

fail() {
    echo "FAILED: $1" >&2
    cat "$log" >&2
    exit 1
}

original_sum=$(sha256sum < "$bin")
object_count=$(find . -name '*.o' | wc -l)
[ "$object_count" -gt 0 ] || fail "no objects to clean"

"${MAKE:-make}" clean > "$log" 2>&1 || fail "make clean failed"
left=$(find . \( -name '*.o' -o -name '*.d' \) | wc -l)
[ "$left" -eq 0 ] || fail "$left objects and dependency files survived make clean"
[ ! -e build ] || fail "build folder survived make clean"
git diff --quiet HEAD -- .. || fail "make clean changed tracked files"
echo "ok: clean removed all $object_count objects and changed no tracked file"

"${MAKE:-make}" -j"$(nproc)" > "$log" 2>&1 || fail "make failed"
compiled=$(grep -c -- ' -c ' "$log" || true)
[ "$compiled" -eq "$object_count" ] || fail "compiled $compiled of $object_count sources"
echo "ok: next build compiled all $compiled sources"

[ "$(sha256sum < "$bin")" = "$original_sum" ] || fail "next build changed the binary"
echo "ok: next build reproduced the original binary"
