#!/bin/sh
# Regression tests for tools/gen_build_info.sh, run through the real Makefile rule
# in a throwaway git repository. Hostile but valid branch names must reach the
# compiled BUILD_ID byte for byte, without make, a shell or the compiler evaluating
# them. Needs git, make and a C++ compiler; no ARM toolchain.

set -u
export LC_ALL=C

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
CXX=${CXX:-g++}
MAKE=${MAKE:-make}
unset BUILD_GIT_BRANCH BUILD_GIT_PR_HEAD

# MSYS rewrites path-like values on their way into a native make (x=/y becomes
# x=Y:/). Exempt only the branch name and keep what the caller has listed.
export MSYS2_ENV_CONV_EXCL="${MSYS2_ENV_CONV_EXCL:+$MSYS2_ENV_CONV_EXCL;}BUILD_GIT_BRANCH"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

repo=$work/repo
fw=$repo/Odrive-Wheel
header=$fw/build/build_info.h
pr_head=0123456789abcdef0123456789abcdef01234567

checks=0
failures=0
skipped=0

# MSYS rewrites path-like arguments of native programs (x=/y becomes x=Y:/), which
# would corrupt the branch names this script hands to git.
repo_git() {
    (cd "$repo" && MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' git "$@")
}

mkdir -p "$fw/tools"
cp "$root/Odrive-Wheel/Makefile" "$fw/"
cp "$root/Odrive-Wheel/tools/gen_build_info.sh" "$fw/tools/"
echo tracked > "$repo/tracked.txt"
echo build/ > "$repo/.gitignore"
repo_git init -q
repo_git config user.name test
repo_git config user.email test@example.invalid
repo_git config commit.gpgsign false
repo_git config core.autocrlf false
repo_git add -A
repo_git commit -q -m init

head_hash() {
    repo_git rev-parse --short=12 HEAD
}

# Prints the id as a compiled program sees it.
compile_and_print() {
    "$CXX" -std=gnu++17 -Wall -Wextra -Werror -I"$1" "$here/print_build_id.cpp" -o "$work/print_id" &&
        "$work/print_id"
}

build_id() {
    "$MAKE" -s -C "$fw" build/build_info.h > /dev/null && compile_and_print "$fw/build"
}

check_equal() {
    checks=$((checks + 1))
    if [ "$3" != "$2" ]; then
        failures=$((failures + 1))
        printf 'FAILED: %s\n  expected: %s\n  actual:   %s\n' "$1" "$2" "$3"
    fi
}

check_true() {
    checks=$((checks + 1))
    if ! eval "$2"; then
        failures=$((failures + 1))
        printf 'FAILED: %s\n' "$1"
    fi
}

header_rewritten_since_marker() {
    [ -n "$(find "$header" -newer "$work/marker")" ]
}

# Valid git branch names, read literally. Windows can't store some of them as
# loose refs, so a name git refuses to create is skipped there, not failed.
while IFS= read -r name; do
    check_true "test data is a valid branch name: $name" 'repo_git check-ref-format --branch "$name" > /dev/null'
    if ! repo_git checkout -q -b "$name" 2> /dev/null; then
        skipped=$((skipped + 1))
        printf 'SKIPPED: branch cannot be created here: %s\n' "$name"
    else
        check_equal "git branch: $name" "$(head_hash) branch=$name" "$(build_id)"
    fi
    check_equal "env branch: $name" "$(head_hash) head=0123456789ab branch=$name" \
        "$(export BUILD_GIT_BRANCH="$name" BUILD_GIT_PR_HEAD="$pr_head"; build_id)"
done <<'EOF'
pkg/0-dev-setup
it's
say"hi"
"
'
$(touch${IFS}PWNED)
`touch${IFS}PWNED`
$HOME
$$x
$(x)
${x}
a;touch${IFS}PWNED
a&touch${IFS}PWNED
a|b
>PWNED
<x
#comment
a#b
%d%s%n
x=/y
x=/usr/bin
a=b=/c
=/x
#/x
(/x
a"=/y
a]b
(x)
{x}
!x
umlaut-äöü
EOF

# Not valid in git, but the environment route must keep them intact as well.
while IFS= read -r name; do
    check_equal "env only: $name" "$(head_hash) branch=$name" \
        "$(export BUILD_GIT_BRANCH="$name"; build_id)"
done <<'EOF'
a\b
trailing\
a"b\"
??=
*/
$(shell touch PWNED)
a b
EOF

check_true "no hostile name was executed" \
    '[ -z "$(find "$work" "$here" -name "PWNED*")" ] && [ ! -e PWNED ]'

# The header only changes when the id does.
repo_git checkout -q -b rebuild-check
build_id > /dev/null
sleep 1
touch "$work/marker"
build_id > /dev/null
check_true "unchanged id leaves the header untouched" '! header_rewritten_since_marker'

repo_git commit -q --allow-empty -m next
check_equal "new commit updates the id" "$(head_hash) branch=rebuild-check" "$(build_id)"
check_true "new commit rewrites the header" 'header_rewritten_since_marker'

sleep 1
touch "$work/marker"
check_equal "other branch updates the id" "$(head_hash) branch=other" \
    "$(export BUILD_GIT_BRANCH=other; build_id)"
check_true "other branch rewrites the header" 'header_rewritten_since_marker'

# Checkout states.
echo changed >> "$repo/tracked.txt"
check_equal "uncommitted change is marked" "$(head_hash)-dirty branch=rebuild-check" "$(build_id)"
repo_git checkout -q -- tracked.txt

repo_git checkout -q --detach
check_equal "detached HEAD" "$(head_hash) branch=detached" "$(build_id)"
repo_git checkout -q rebuild-check

mkdir "$work/no-git"
actual=$(cd "$work/no-git" && GIT_CEILING_DIRECTORIES=$work sh "$fw/tools/gen_build_info.sh" build_info.h > /dev/null &&
    compile_and_print "$work/no-git")
check_equal "outside a repository" "unknown branch=unknown" "$actual"

# An id longer than the reply buffer is cut visibly, the checkout hash survives.
long=$(printf '%0200d' 0)
actual=$(export BUILD_GIT_BRANCH="$long"; build_id)
check_equal "long id fits the reply buffer" 127 "${#actual}"
check_true "long id starts with the checkout" 'case $actual in "$(head_hash) branch=000"*) true ;; *) false ;; esac'
check_true "long id is marked as cut" 'case $actual in *...) true ;; *) false ;; esac'

# Invalid metadata stops the build instead of producing a wrong id.
before=$(cat "$header")
tab=$(printf 'a\tb')
check_true "control character is rejected" \
    '! (cd "$fw" && BUILD_GIT_BRANCH="$tab" sh tools/gen_build_info.sh build/build_info.h > /dev/null 2>&1)'
check_true "non-hash PR head is rejected" \
    '! (cd "$fw" && BUILD_GIT_PR_HEAD="abc;touch PWNED" sh tools/gen_build_info.sh build/build_info.h > /dev/null 2>&1)'
check_true "colon, as left by an MSYS rewrite, is rejected" \
    '! (cd "$fw" && BUILD_GIT_BRANCH="x=Y:/" sh tools/gen_build_info.sh build/build_info.h > /dev/null 2>&1)'
check_equal "rejected metadata leaves the header alone" "$before" "$(cat "$header")"

# Conversion between an MSYS shell and a native make.
if "$MAKE" --version | grep -q w64-mingw32; then
    native_make=yes
else
    native_make=no
fi

# Without the exclusion a native make sees a rewritten value: the build must stop
# instead of reporting a wrong branch. Any other make passes the value through.
actual=$(export BUILD_GIT_BRANCH='x=/y' MSYS2_ENV_CONV_EXCL=; build_id 2> /dev/null) || actual=build-stopped
if [ "$native_make" = yes ]; then
    check_equal "rewritten branch stops the build" build-stopped "$actual"
else
    check_equal "branch passes through without exclusion" "$(head_hash) branch=x=/y" "$actual"
fi

# The exclusion covers the branch name only, conversion stays on for the rest.
printf 'all:\n\t@printf "%%s" "$$PROBE_OTHER"\n' > "$work/probe.mk"
actual=$(export PROBE_OTHER='x=/y'; "$MAKE" --no-print-directory -s -f "$work/probe.mk")
if [ "$native_make" = yes ]; then
    check_true "other variables are still converted" '[ "$actual" != "x=/y" ]'
else
    check_equal "other variables pass through" 'x=/y' "$actual"
fi

printf 'test_build_info.sh: %d checks, %d failed, %d skipped\n' "$checks" "$failures" "$skipped"
[ "$failures" -eq 0 ]
