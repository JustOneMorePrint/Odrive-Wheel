// [pkgS] Decides whether the MCU flash may be written or erased.
//
// The CPU fetches its code from the same flash, so it stalls during a flash operation and the
// control interrupt stalls with it. The PWM outputs then keep their last duty cycle while nothing
// regulates the current. So no motor may be active during a flash operation, and none may start
// during one.
//
// This part holds the decision only and has no HAL dependency, tests/host/test_flash_guard.cpp
// runs it on the PC. odrive_bridge.cpp feeds it the real axis state, atomically.

#ifndef FLASH_GUARD_H_
#define FLASH_GUARD_H_

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    FLASH_GUARD_ALLOWED = 0,
    FLASH_GUARD_BUSY,      // another flash operation is still running
    FLASH_GUARD_ARMED,     // a motor is armed
    FLASH_GUARD_REQUESTED, // a state change waits for the axis thread and may arm a motor
    FLASH_GUARD_ACTIVE,    // an axis runs anything but idle, a calibration between two arm phases for example
} flash_guard_verdict_t;

typedef struct {
    bool armed;
    bool state_requested;
    bool state_active;
} flash_guard_axis_t;

typedef struct {
    uint32_t blocked;                 // refused flash operations since boot
    uint32_t arm_refused;             // refused arm attempts since boot
    flash_guard_verdict_t last_block; // reason of the last refused flash operation
    bool busy;
} flash_guard_t;

flash_guard_verdict_t flash_guard_check(const flash_guard_axis_t *axes, size_t axis_count, bool busy);

// Claims the flash if the check allows it. If this returns true, flash_guard_finish() must follow.
// A refusal claims nothing.
bool flash_guard_try_begin(flash_guard_t *guard, const flash_guard_axis_t *axes, size_t axis_count);
void flash_guard_finish(flash_guard_t *guard);

// False while the flash is claimed.
bool flash_guard_arm_allowed(flash_guard_t *guard);

const char *flash_guard_verdict_name(flash_guard_verdict_t verdict);

// The reply of safe.status?, see docs/ROADMAP.md.
void flash_guard_format_status(const flash_guard_t *guard, flash_guard_verdict_t now, char *buf, size_t buf_size);

#ifdef __cplusplus
}
#endif

#endif
