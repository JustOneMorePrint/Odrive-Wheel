// The rule behind every flash operation of the firmware: none with a motor active, no arming during
// one. Fed into the real parser as well, because the SimHub plugin and the web client recognize a
// refusal by the reply ERR.
//
// cmd_table.cpp and odrive_bridge.cpp need the HAL and can't be linked here. test_flash_paths.sh
// checks by text that they use this logic in every path.
#include "../../Odrive-Wheel/src/flash_guard.c"
#include "../../Odrive-Wheel/src/cmdparser.c"

#include <cstdint>
#include <string>

#include "test_util.hpp"

// ---- Stand-ins for the real handlers, the same shape as h_sys_save and h_sys_eetest -------------

static const size_t axis_count = 2;
static flash_guard_axis_t test_axes[axis_count];
static flash_guard_t test_guard;
static int flash_writes = 0;

static int guarded_write_handler(uint8_t, CmdType t, const char *, char *reply, size_t reply_size) {
    if (t != CMD_TYPE_EXEC && t != CMD_TYPE_GET) return -1;
    if (!flash_guard_try_begin(&test_guard, test_axes, axis_count)) return -1;
    flash_writes++;
    flash_guard_finish(&test_guard);
    snprintf(reply, reply_size, "OK");
    return 0;
}

static int status_handler(uint8_t, CmdType t, const char *, char *reply, size_t reply_size) {
    if (t != CMD_TYPE_GET) return -1;
    flash_guard_format_status(&test_guard, flash_guard_check(test_axes, axis_count, test_guard.busy), reply, reply_size);
    return 0;
}

const CmdEntry cmdtable[] = {
    {"sys", "save", guarded_write_handler},
    {"sys", "eedump", guarded_write_handler},
    {"safe", "status", status_handler},
};
const size_t cmdtable_size = sizeof(cmdtable) / sizeof(cmdtable[0]);

const CmdClassMeta cmdclasses[] = {
    {"sys", 0, 0, "System"},
};
const size_t cmdclasses_size = sizeof(cmdclasses) / sizeof(cmdclasses[0]);

static std::string feed(const std::string &line) {
    char reply[512];
    size_t n = cmdparser_feed(reinterpret_cast<const uint8_t *>(line.data()), line.size(), reply, sizeof(reply));
    n += cmdparser_feed(reinterpret_cast<const uint8_t *>(";"), 1, reply + n, sizeof(reply) - n);
    return std::string(reply, n);
}

// ---- Tests ---------------------------------------------------------------------------------------

static void test_every_axis_combination() {
    const int flag_count = 6;
    for (int bits = 0; bits < (1 << flag_count); bits++) {
        flash_guard_axis_t axes[2];
        axes[0] = {(bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0};
        axes[1] = {(bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0};

        const bool any_armed = axes[0].armed || axes[1].armed;
        const bool any_requested = axes[0].state_requested || axes[1].state_requested;
        const bool any_active = axes[0].state_active || axes[1].state_active;

        flash_guard_verdict_t expected = FLASH_GUARD_ALLOWED;
        if (any_active) expected = FLASH_GUARD_ACTIVE;
        if (any_requested) expected = FLASH_GUARD_REQUESTED;
        if (any_armed) expected = FLASH_GUARD_ARMED;

        CHECK(flash_guard_check(axes, 2, false) == expected);
        CHECK(flash_guard_check(axes, 2, true) == FLASH_GUARD_BUSY);
    }
}

static void test_each_axis_alone_blocks() {
    for (size_t axis = 0; axis < axis_count; axis++) {
        flash_guard_axis_t axes[axis_count] = {};
        axes[axis].armed = true;
        CHECK(flash_guard_check(axes, axis_count, false) == FLASH_GUARD_ARMED);

        axes[axis] = {};
        axes[axis].state_requested = true;
        CHECK(flash_guard_check(axes, axis_count, false) == FLASH_GUARD_REQUESTED);

        axes[axis] = {};
        axes[axis].state_active = true;
        CHECK(flash_guard_check(axes, axis_count, false) == FLASH_GUARD_ACTIVE);
    }
}

static void test_no_axes_and_all_idle_are_allowed() {
    CHECK(flash_guard_check(nullptr, 0, false) == FLASH_GUARD_ALLOWED);
    const flash_guard_axis_t idle[axis_count] = {};
    CHECK(flash_guard_check(idle, axis_count, false) == FLASH_GUARD_ALLOWED);
}

static void test_begin_claims_and_finish_releases() {
    flash_guard_t guard = {};
    const flash_guard_axis_t idle[axis_count] = {};

    CHECK(flash_guard_try_begin(&guard, idle, axis_count));
    CHECK(guard.busy);
    CHECK(guard.blocked == 0);

    // A second operation has to wait for the first, from another thread for example.
    CHECK(!flash_guard_try_begin(&guard, idle, axis_count));
    CHECK(guard.blocked == 1);
    CHECK(guard.last_block == FLASH_GUARD_BUSY);
    CHECK(guard.busy);

    flash_guard_finish(&guard);
    CHECK(!guard.busy);
    CHECK(flash_guard_try_begin(&guard, idle, axis_count));
    CHECK(guard.blocked == 1);
}

static void test_a_refusal_claims_nothing() {
    flash_guard_t guard = {};
    flash_guard_axis_t axes[axis_count] = {};
    axes[1].armed = true;

    CHECK(!flash_guard_try_begin(&guard, axes, axis_count));
    CHECK(!guard.busy);
    CHECK(guard.blocked == 1);
    CHECK(guard.last_block == FLASH_GUARD_ARMED);
    CHECK(flash_guard_arm_allowed(&guard));

    axes[1] = {};
    axes[0].state_requested = true;
    CHECK(!flash_guard_try_begin(&guard, axes, axis_count));
    CHECK(guard.blocked == 2);
    CHECK(guard.last_block == FLASH_GUARD_REQUESTED);

    axes[0] = {};
    CHECK(flash_guard_try_begin(&guard, axes, axis_count));
    CHECK(guard.blocked == 2);
}

// Both orders of the race between a flash operation and an arm attempt. Whoever comes first wins,
// and the motor never ends up armed during a flash operation.
static void test_arm_and_flash_exclude_each_other() {
    flash_guard_t guard = {};
    flash_guard_axis_t axes[axis_count] = {};

    CHECK(flash_guard_arm_allowed(&guard));
    CHECK(guard.arm_refused == 0);

    CHECK(flash_guard_try_begin(&guard, axes, axis_count));
    CHECK(!flash_guard_arm_allowed(&guard));
    CHECK(!flash_guard_arm_allowed(&guard));
    CHECK(guard.arm_refused == 2);
    flash_guard_finish(&guard);
    CHECK(flash_guard_arm_allowed(&guard));
    CHECK(guard.arm_refused == 2);

    axes[0].armed = true;
    CHECK(!flash_guard_try_begin(&guard, axes, axis_count));
    CHECK(flash_guard_arm_allowed(&guard));
}

static void test_verdict_names() {
    CHECK(std::string(flash_guard_verdict_name(FLASH_GUARD_ALLOWED)) == "allowed");
    CHECK(std::string(flash_guard_verdict_name(FLASH_GUARD_BUSY)) == "busy");
    CHECK(std::string(flash_guard_verdict_name(FLASH_GUARD_ARMED)) == "armed");
    CHECK(std::string(flash_guard_verdict_name(FLASH_GUARD_REQUESTED)) == "requested");
    CHECK(std::string(flash_guard_verdict_name(FLASH_GUARD_ACTIVE)) == "active");
    CHECK(std::string(flash_guard_verdict_name(static_cast<flash_guard_verdict_t>(99))) == "unknown");
}

static void test_status_text() {
    char buf[128];
    flash_guard_t guard = {};
    flash_guard_format_status(&guard, FLASH_GUARD_ALLOWED, buf, sizeof(buf));
    CHECK(std::string(buf) == "now=allowed blocked=0 last=none armrefused=0");

    guard.blocked = 3;
    guard.last_block = FLASH_GUARD_ARMED;
    guard.arm_refused = 2;
    flash_guard_format_status(&guard, FLASH_GUARD_ACTIVE, buf, sizeof(buf));
    CHECK(std::string(buf) == "now=active blocked=3 last=armed armrefused=2");

    // Fits the reply buffer of the parser (128 bytes) even at its longest.
    guard.blocked = UINT32_MAX;
    guard.arm_refused = UINT32_MAX;
    guard.last_block = FLASH_GUARD_REQUESTED;
    flash_guard_format_status(&guard, FLASH_GUARD_REQUESTED, buf, sizeof(buf));
    CHECK(std::string(buf) == "now=requested blocked=4294967295 last=requested armrefused=4294967295");
    CHECK(std::string(buf).size() < sizeof(buf));

    // A short buffer stays terminated.
    char small[8];
    flash_guard_format_status(&guard, FLASH_GUARD_ALLOWED, small, sizeof(small));
    CHECK(std::string(small) == "now=all");
}

static void test_replies_through_the_real_parser() {
    test_guard = {};
    flash_writes = 0;
    for (auto &axis : test_axes) axis = {};

    CHECK(feed("sys.save!") == "[sys.save!|OK]\n");
    CHECK(flash_writes == 1);

    // The forms a client can send, none of them may reach the flash, and all of them read as a
    // failure: OdriveProtocol.IsOffbFailure and the web client look for exactly this.
    test_axes[0].armed = true;
    CHECK(feed("sys.save!") == "[sys.save!|ERR]\n");
    CHECK(feed("sys.save?") == "[sys.save?|ERR]\n");
    CHECK(feed("sys.eedump?") == "[sys.eedump?|ERR]\n");
    CHECK(flash_writes == 1);
    CHECK(test_guard.blocked == 3);
    CHECK(!test_guard.busy);

    CHECK(feed("safe.status?") == "[safe.status?|now=armed blocked=3 last=armed armrefused=0]\n");

    test_axes[0].armed = false;
    CHECK(feed("safe.status?") == "[safe.status?|now=allowed blocked=3 last=armed armrefused=0]\n");
    CHECK(feed("sys.save!") == "[sys.save!|OK]\n");
    CHECK(flash_writes == 2);
}

int main() {
    test_every_axis_combination();
    test_each_axis_alone_blocks();
    test_no_axes_and_all_idle_are_allowed();
    test_begin_claims_and_finish_releases();
    test_a_refusal_claims_nothing();
    test_arm_and_flash_exclude_each_other();
    test_verdict_names();
    test_status_text();
    test_replies_through_the_real_parser();
    return TEST_RESULT();
}
