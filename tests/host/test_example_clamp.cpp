#include "example_clamp.hpp"
#include "test_util.hpp"

int main() {
    CHECK_NEAR(clamp_symmetric(1.5f, 3.0f), 1.5f, 1e-6f);
    CHECK_NEAR(clamp_symmetric(-1.5f, 3.0f), -1.5f, 1e-6f);
    CHECK_NEAR(clamp_symmetric(5.0f, 3.0f), 3.0f, 1e-6f);
    CHECK_NEAR(clamp_symmetric(-5.0f, 3.0f), -3.0f, 1e-6f);
    CHECK_NEAR(clamp_symmetric(5.0f, 0.0f), 0.0f, 1e-6f);
    return TEST_RESULT();
}
