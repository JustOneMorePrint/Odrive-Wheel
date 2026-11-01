// Example of a pure, HAL-free function. Real modules live in Odrive-Wheel/src.
#pragma once

inline float clamp_symmetric(float value, float limit) {
    if (value > limit) return limit;
    if (value < -limit) return -limit;
    return value;
}
