// sys.capabilities?, from the line to the reply frame: the real handler in the real parser. The table is
// a stand-in because cmd_table.cpp needs the HAL, so main() reads its text for the registration. Whether
// the declared flags are true is up to test_ascii_read.cpp, which has the real formatting.
#include "../../Odrive-Wheel/src/cmdparser.c"
#include "../../Odrive-Wheel/src/firmware_capabilities.c"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>

#include "test_util.hpp"

static int other_handler_calls = 0;

static int other_handler(uint8_t, CmdType, const char *, char *reply, size_t reply_size) {
    other_handler_calls++;
    snprintf(reply, reply_size, "OTHER");
    return 0;
}

const CmdEntry cmdtable[] = {
    {"sys", "swver", other_handler},
    {"sys", "capabilities", firmware_capabilities_cmd},
    {"sys", "capabilitiesx", other_handler},
};
const size_t cmdtable_size = sizeof(cmdtable) / sizeof(cmdtable[0]);

const CmdClassMeta cmdclasses[] = {
    {"sys", 0, 0, "System"},
};
const size_t cmdclasses_size = sizeof(cmdclasses) / sizeof(cmdclasses[0]);

// The way process_line hands a line to the parser (ascii_protocol.cpp), with its buffer.
static std::string feed(const std::string &line) {
    char reply[512];
    size_t n = cmdparser_feed(reinterpret_cast<const uint8_t *>(line.data()), line.size(), reply, sizeof(reply));
    n += cmdparser_feed(reinterpret_cast<const uint8_t *>(";"), 1, reply + n, sizeof(reply) - n);
    return std::string(reply, n);
}

static std::string read_file(const std::string &path) {
    std::ifstream file(path, std::ios::binary);
    CHECK(file.good());
    std::stringstream text;
    text << file.rdbuf();
    return text.str();
}

static std::string without_space(std::string text) {
    text.erase(std::remove_if(text.begin(), text.end(), [](unsigned char c) { return std::isspace(c) != 0; }), text.end());
    return text;
}

static int count(const std::string &text, const std::string &wanted) {
    int found = 0;
    for (size_t at = text.find(wanted); at != std::string::npos; at = text.find(wanted, at + wanted.size())) found++;
    return found;
}

static const std::string kFrame = "[sys.capabilities?|v=1 ascii_full=1 float32_rt=1]\n";

static void test_the_reply_frame() {
    CHECK(feed("sys.capabilities?") == kFrame);
    // process_line ends a line by feeding ';', and a client may end it with a line end too.
    CHECK(feed("sys.capabilities?\r\n") == kFrame);
    CHECK(feed("sys.1.capabilities?") == "[sys.1.capabilities?|v=1 ascii_full=1 float32_rt=1]\n");
    CHECK(other_handler_calls == 0);

    // A name that merely starts the same is another command, and there is no other spelling.
    CHECK(feed("sys.capabilitiesx?") == "[sys.capabilitiesx?|OTHER]\n");
    CHECK(feed("sys.capabilitie?") == "[sys.capabilitie?|NOT_FOUND]\n");
    CHECK(feed("sys.Capabilities?") == "[sys.Capabilities?|NOT_FOUND]\n");
    other_handler_calls = 0;
}

// The whole frame in one USB packet, and within every buffer on the way.
static void test_the_reply_fits_the_parser() {
    CHECK(kFrame.size() <= 64);
    CHECK(sizeof(FIRMWARE_CAPABILITIES_REPLY) <= 128);    // reply[128] in parse_one
    CHECK(std::strlen("capabilities") < 32);              // cmd_buf[32] in parse_one
    CHECK(std::strlen("sys.capabilities") < 80);          // head[80] in write_reply
}

static void test_set_and_exec_are_refused_and_change_nothing() {
    CHECK(feed("sys.capabilities=1") == "[sys.capabilities=1|ERR]\n");
    CHECK(feed("sys.capabilities=v=2 ascii_full=0") == "[sys.capabilities=v=2 ascii_full=0|ERR]\n");
    CHECK(feed("sys.capabilities=") == "[sys.capabilities=|ERR]\n");
    CHECK(feed("sys.capabilities!") == "[sys.capabilities!|ERR]\n");
    CHECK(feed("sys.capabilities?") == kFrame);
    CHECK(other_handler_calls == 0);

    // The handler itself: a refusal leaves the buffer as it was.
    for (CmdType type : {CMD_TYPE_SET, CMD_TYPE_EXEC}) {
        char reply[64];
        std::memset(reply, 0x5A, sizeof(reply));
        CHECK(firmware_capabilities_cmd(0, type, "1", reply, sizeof(reply)) == -1);
        bool untouched = true;
        for (char c : reply) untouched = untouched && c == 0x5A;
        CHECK(untouched);
    }
}

// Part of a declaration is no declaration, so it never shortens one.
static void test_a_buffer_too_small_gets_nothing() {
    const size_t needed = sizeof(FIRMWARE_CAPABILITIES_REPLY);
    for (size_t size = 0; size <= needed; ++size) {
        char reply[64];
        std::memset(reply, 0x5A, sizeof(reply));
        const int rc = firmware_capabilities_cmd(0, CMD_TYPE_GET, nullptr, reply, size);
        if (size == needed) {
            CHECK(rc == 0);
            CHECK(std::strcmp(reply, FIRMWARE_CAPABILITIES_REPLY) == 0);
        } else {
            CHECK(rc == -1);
            CHECK(reply[0] == 0x5A);
        }
    }
}

static void test_the_handler_is_registered_once() {
    const std::string table = without_space(read_file("../../Odrive-Wheel/src/cmd_table.cpp"));
    CHECK(count(table, "{\"sys\",\"capabilities\",firmware_capabilities_cmd}") == 1);
    CHECK(count(table, "\"capabilities\"") == 1);
    CHECK(count(table, "#include\"firmware_capabilities.h\"") == 1);

    CHECK(count(read_file("../../Odrive-Wheel/Makefile"), "src/firmware_capabilities.c") == 1);
}

// The file a client developer reads has to say what the firmware sends.
static void test_the_contract_is_documented() {
    CHECK(count(read_file("../../docs/packages/PKG1.md"), "`" FIRMWARE_CAPABILITIES_REPLY "`") >= 1);
}

int main() {
    test_the_reply_frame();
    test_the_reply_fits_the_parser();
    test_set_and_exec_are_refused_and_change_nothing();
    test_a_buffer_too_small_gets_nothing();
    test_the_handler_is_registered_once();
    test_the_contract_is_documented();
    return TEST_RESULT();
}
