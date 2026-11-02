// [pkgS] See flash_guard.h.

#include "flash_guard.h"

#include <stdio.h>

flash_guard_verdict_t flash_guard_check(const flash_guard_axis_t *axes, size_t axis_count, bool busy) {
    if (busy) return FLASH_GUARD_BUSY;

    flash_guard_verdict_t verdict = FLASH_GUARD_ALLOWED;
    for (size_t i = 0; i < axis_count; i++) {
        // An armed motor on any axis is the strongest reason, so it wins over the others.
        if (axes[i].armed) return FLASH_GUARD_ARMED;
        if (axes[i].state_requested) {
            verdict = FLASH_GUARD_REQUESTED;
        } else if (axes[i].state_active && verdict == FLASH_GUARD_ALLOWED) {
            verdict = FLASH_GUARD_ACTIVE;
        }
    }
    return verdict;
}

bool flash_guard_try_begin(flash_guard_t *guard, const flash_guard_axis_t *axes, size_t axis_count) {
    flash_guard_verdict_t verdict = flash_guard_check(axes, axis_count, guard->busy);
    if (verdict != FLASH_GUARD_ALLOWED) {
        guard->blocked++;
        guard->last_block = verdict;
        return false;
    }
    guard->busy = true;
    return true;
}

void flash_guard_finish(flash_guard_t *guard) {
    guard->busy = false;
}

bool flash_guard_arm_allowed(flash_guard_t *guard) {
    if (guard->busy) {
        guard->arm_refused++;
        return false;
    }
    return true;
}

const char *flash_guard_verdict_name(flash_guard_verdict_t verdict) {
    switch (verdict) {
        case FLASH_GUARD_ALLOWED:   return "allowed";
        case FLASH_GUARD_BUSY:      return "busy";
        case FLASH_GUARD_ARMED:     return "armed";
        case FLASH_GUARD_REQUESTED: return "requested";
        case FLASH_GUARD_ACTIVE:    return "active";
    }
    return "unknown";
}

void flash_guard_format_status(const flash_guard_t *guard, flash_guard_verdict_t now, char *buf, size_t buf_size) {
    snprintf(buf, buf_size, "now=%s blocked=%lu last=%s armrefused=%lu",
             flash_guard_verdict_name(now),
             (unsigned long)guard->blocked,
             guard->blocked == 0 ? "none" : flash_guard_verdict_name(guard->last_block),
             (unsigned long)guard->arm_refused);
}
