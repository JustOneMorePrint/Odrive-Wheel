#!/bin/sh
# Checks the build id on a finished firmware build. Run it from a clean checkout
# after `make` in Odrive-Wheel, with the same environment as that build; it needs
# the ARM toolchain and calls make directly, so autogen does not run again. Set
# MAKE to use another make, e.g. mingw32-make.
# The binary is unchanged afterwards, or the script fails:
#   1. the binary holds the id of the checkout,
#   2. an unchanged rebuild compiles nothing,
#   3. changed metadata recompiles cmd_table.cpp only and reaches the binary,
#   4. restoring the metadata reproduces the original binary.

set -eu
export LC_ALL=C

# See test_build_info.sh: keeps the branch name intact on its way into a native make.
export MSYS2_ENV_CONV_EXCL="${MSYS2_ENV_CONV_EXCL:+$MSYS2_ENV_CONV_EXCL;}BUILD_GIT_BRANCH"

cd "$(dirname "$0")/../../Odrive-Wheel"

bin=build/odrive-wheel.bin
object=build/src/cmd_table.o
log=build/rebuild_check.log
marker=build/rebuild_check.marker

fail() {
    echo "FAILED: $1" >&2
    [ ! -f "$log" ] || cat "$log" >&2
    exit 1
}

run_make() {
    "${MAKE:-make}" -j"$(nproc)" > "$log" 2>&1 || fail "make failed"
}

compile_count() {
    grep -c -- ' -c ' "$log" || true
}

bin_holds() {
    grep -qaF -- "$1" "$bin"
}

bin_sum() {
    sha256sum "$bin"
}

# The expected id is derived here, independently of the generator.
id=$(git rev-parse --short=12 HEAD)
if [ -n "${BUILD_GIT_PR_HEAD:-}" ]; then
    id="$id head=$(printf '%s' "$BUILD_GIT_PR_HEAD" | cut -c1-12)"
fi
branch=${BUILD_GIT_BRANCH:-$(git symbolic-ref -q HEAD | sed 's|^refs/heads/||')}
id="$id branch=$branch"

original_sum=$(bin_sum)
bin_holds "$id" || fail "binary does not hold the id: $id"
echo "ok: binary holds the id: $id"

sleep 1
touch "$marker"
run_make
[ "$(compile_count)" -eq 0 ] || fail "unchanged rebuild compiled something"
[ -z "$(find "$object" -newer "$marker")" ] || fail "unchanged rebuild touched $object"
[ "$(bin_sum)" = "$original_sum" ] || fail "unchanged rebuild changed the binary"
echo "ok: unchanged rebuild compiled nothing"

changed_branch="ci=/y'\"\$(touch\${IFS}PWNED)\`touch\${IFS}PWNED\`"
changed_id=$(printf '%s' "$id" | sed 's/ branch=.*//')" branch=$changed_branch"
(
    export BUILD_GIT_BRANCH="$changed_branch"
    run_make
)
grep -q -- ' -c .*src/cmd_table\.cpp' "$log" || fail "changed metadata did not recompile cmd_table.cpp"
[ "$(compile_count)" -eq 1 ] || fail "changed metadata compiled more than cmd_table.cpp"
bin_holds "$changed_id" || fail "binary does not hold the changed id: $changed_id"
bin_holds "$id" && fail "binary still holds the old id: $id"
[ ! -e PWNED ] || fail "branch name was executed"
echo "ok: changed metadata recompiled cmd_table.cpp only and reached the binary"

run_make
[ "$(compile_count)" -eq 1 ] || fail "restoring the metadata compiled more than cmd_table.cpp"
[ "$(bin_sum)" = "$original_sum" ] || fail "restoring the metadata did not reproduce the binary"
echo "ok: restored metadata reproduced the original binary"
