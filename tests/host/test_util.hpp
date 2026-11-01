// Minimal test helpers: a failed check is reported and the program's exit code
// becomes non-zero through TEST_RESULT().
#pragma once

#include <cmath>
#include <cstdio>

static int test_failures = 0;
static int test_checks = 0;

#define CHECK(cond)                                                       \
    do {                                                                  \
        test_checks++;                                                    \
        if (!(cond)) {                                                    \
            test_failures++;                                              \
            std::printf("%s:%d: FAILED: %s\n", __FILE__, __LINE__, #cond); \
        }                                                                 \
    } while (0)

#define CHECK_NEAR(actual, expected, tolerance) \
    CHECK(std::fabs((actual) - (expected)) <= (tolerance))

#define TEST_RESULT()                                                            \
    (std::printf("%s: %d checks, %d failed\n", __FILE__, test_checks, test_failures), \
     test_failures == 0 ? 0 : 1)
