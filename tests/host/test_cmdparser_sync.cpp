// The sync line of the SimHub plugin for the OpenFFBoard reply path, fed into the real firmware
// parser. The plugin relies on exactly this (OdriveProtocol.OffbSyncRequest): the line runs no
// handler, and the reply names the whole marker.
//
// cmd_table.cpp needs the HAL and can't be linked here, so the table below is a stand-in. What
// matters about the real one is that the class of the sync line stays unknown, main() checks that.
#include "../../Odrive-Wheel/src/cmdparser.c"

#include <fstream>
#include <sstream>
#include <string>

#include "test_util.hpp"

static int handler_calls = 0;

static int counting_handler(uint8_t, CmdType, const char *, char *reply, size_t reply_size) {
    handler_calls++;
    snprintf(reply, reply_size, "HANDLED");
    return 0;
}

const CmdEntry cmdtable[] = {
    {"axis", "range", counting_handler},
    {"sys", "save", counting_handler},
};
const size_t cmdtable_size = sizeof(cmdtable) / sizeof(cmdtable[0]);

const CmdClassMeta cmdclasses[] = {
    {"main", 1, 0, "FFB Wheel"},
    {"sys", 0, 0, "System"},
    {"axis", 2561, 0, "Axis 0"},
};
const size_t cmdclasses_size = sizeof(cmdclasses) / sizeof(cmdclasses[0]);

// The way process_line hands a line to the parser (ascii_protocol.cpp).
static std::string feed(const std::string &line) {
    char reply[512];
    size_t n = cmdparser_feed(reinterpret_cast<const uint8_t *>(line.data()), line.size(), reply, sizeof(reply));
    n += cmdparser_feed(reinterpret_cast<const uint8_t *>(";"), 1, reply + n, sizeof(reply) - n);
    return std::string(reply, n);
}

int main() {
    // The stand-in reaches its handlers, so a sync line that doesn't is no accident.
    CHECK(feed("axis.range?") == "[axis.range?|HANDLED]\n");
    CHECK(handler_calls == 1);
    handler_calls = 0;

    CHECK(feed("odwsync.m1000000?") == "[odwsync.m1000000?|NOT_FOUND]\n");
    CHECK(feed("odwsync.m1000001?") == "[odwsync.m1000001?|NOT_FOUND]\n");
    CHECK(feed("odwsync.m4294967295?") == "[odwsync.m4294967295?|NOT_FOUND]\n");
    CHECK(handler_calls == 0);

    std::ifstream table("../../Odrive-Wheel/src/cmd_table.cpp");
    CHECK(table.good());
    std::stringstream text;
    text << table.rdbuf();
    CHECK(text.str().find("cmdclasses[]") != std::string::npos);
    CHECK(text.str().find("odwsync") == std::string::npos);

    return TEST_RESULT();
}
