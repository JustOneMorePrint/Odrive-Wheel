// Prints BUILD_ID unchanged so test_build_info.sh can compare it byte for byte.
#include <cstdio>

#include "build_info.h"

int main() {
    std::fputs(BUILD_ID, stdout);
    return 0;
}
