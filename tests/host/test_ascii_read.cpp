// The answer to an ASCII read ("r <path>"), from the value to the bytes that leave the sink. Runs the
// real formatters and parsers of fibre-cpp/protocol.hpp, the real type infos of fibre/introspection.hpp,
// the real BufferedStreamSink, and the unchanged text of AsciiProtocol::respond and cmd_read_property
// (extract_prod_functions.sh). Not run: process_line, it needs most of the firmware.
//
// The host C library does the printf work here. So this shows the logic around it, and what a correct
// printf yields. It is no evidence for how newlib-nano on the board handles %.9g.

#include <cfloat>
#include <climits>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <limits>
#include <optional>
#include <random>
#include <string>
#include <tuple>
#include <type_traits>
#include <unordered_map>
#include <utility>
#include <variant>
#include <vector>

#if defined(__SSE__)
#include <xmmintrin.h>
#endif

#include "test_util.hpp"

// newlib-nano's printf has no %llu or %lld, it leaves "lu" or "ld" in the buffer. The host library
// can do both, which would hide a formatter that relies on them. This brings back that one gap and
// says nothing else about newlib-nano.
static int snprintf_calls = 0;

static int nano_like_snprintf(char* buffer, size_t length, const char* format, ...) {
    snprintf_calls++;
    if (std::strstr(format, "ll") != nullptr)
        return std::snprintf(buffer, length, "%s", std::strstr(format, "llu") != nullptr ? "lu" : "ld");

    va_list args;
    va_start(args, format);
    int needed = std::vsnprintf(buffer, length, format, args);
    va_end(args);
    return needed;
}
#define snprintf nano_like_snprintf

#include <protocol.hpp>
#include <fibre/introspection.hpp>

// To call the private functions of the real class.
#define private public
#include <ascii_protocol.hpp>
#undef private

// ---- A small property tree, built like autogen/type_info.hpp ---------------------------------

enum class TestMode : uint16_t { kLarge = 261 };
using Unsupported = std::pair<float, float>;

struct TestValues {
    unsigned long u32 = 2147483648ul;
    unsigned long u32_max = UINT32_MAX;
    long i32_min = INT32_MIN;
    unsigned long long u64_max = UINT64_MAX;
    long long i64_min = INT64_MIN;
    unsigned long long serial = 281474976710655ull;    // 48 bits, as many as the serial number has
    float small = 0.000012345f;
    float longest_float = -FLT_MIN;
    bool flag = true;
    TestMode mode = TestMode::kLarge;
    Unsupported unsupported{1.0f, 2.0f};
};

static TestValues test_values;

template<typename T> using ValueInfo = FibrePropertyTypeInfo<Property<T>>;

struct TestRootInfo : TypeInfo {
    using TypeInfo::TypeInfo;

    template<typename T>
    static introspectable_storage_t property(T* value) {
        introspectable_storage_t storage;
        *reinterpret_cast<Property<T>*>(&storage) = Property<T>(value);
        return storage;
    }

    introspectable_storage_t get_child(introspectable_storage_t, size_t idx) const override {
        switch (idx) {
            case 0:  return property(&test_values.u32);
            case 1:  return property(&test_values.u32_max);
            case 2:  return property(&test_values.i32_min);
            case 3:  return property(&test_values.u64_max);
            case 4:  return property(&test_values.i64_min);
            case 5:  return property(&test_values.serial);
            case 6:  return property(&test_values.small);
            case 7:  return property(&test_values.longest_float);
            case 8:  return property(&test_values.flag);
            case 9:  return property(&test_values.mode);
            case 10: return property(&test_values.unsupported);
            case 11: return property<const unsigned long>(&test_values.u32);
            default: return {};
        }
    }
};

// Has children but no value, like "axis0" in the firmware.
struct TestNodeInfo : TestRootInfo {
    using TestRootInfo::TestRootInfo;
};

static const PropertyInfo node_table[] = {
    {"u32", &ValueInfo<unsigned long>::singleton},
};
static const TestNodeInfo node_info{node_table, 1};

static const PropertyInfo root_table[] = {
    {"u32",           &ValueInfo<unsigned long>::singleton},
    {"u32_max",       &ValueInfo<unsigned long>::singleton},
    {"i32_min",       &ValueInfo<long>::singleton},
    {"u64_max",       &ValueInfo<unsigned long long>::singleton},
    {"i64_min",       &ValueInfo<long long>::singleton},
    {"serial",        &ValueInfo<unsigned long long>::singleton},
    {"small",         &ValueInfo<float>::singleton},
    {"longest_float", &ValueInfo<float>::singleton},
    {"flag",          &ValueInfo<bool>::singleton},
    {"mode",          &ValueInfo<TestMode>::singleton},
    {"unsupported",   &ValueInfo<Unsupported>::singleton},
    {"readonly",      &ValueInfo<const unsigned long>::singleton},
    {"node",          &node_info},
};
static const TestRootInfo root_info{root_table, sizeof(root_table) / sizeof(root_table[0])};

static Introspectable make_root() {
    Introspectable root;
    root.type_info_ = &root_info;
    return root;
}

static Introspectable root_obj = make_root();

using namespace fibre;

#include "prod_ascii_read.inc"

#undef snprintf

// ---- Helpers -----------------------------------------------------------------------------------

#define CHECK_TEXT(actual, expected)                                                      \
    do {                                                                                  \
        const std::string actual_text_ = (actual);                                        \
        const std::string expected_text_ = (expected);                                    \
        CHECK(actual_text_ == expected_text_);                                            \
        if (actual_text_ != expected_text_)                                               \
            std::printf("  expected \"%s\", got \"%s\"\n", expected_text_.c_str(), actual_text_.c_str()); \
    } while (0)

static const unsigned char kGuard = 0xA5;

// Formats into exactly the buffer it needs and into every smaller one, down to no buffer at all.
template<typename T>
static void check_fit(const T& value, const std::string& expected) {
    const size_t needed = expected.size() + 1;
    for (size_t length = 0; length <= needed; ++length) {
        unsigned char memory[64];
        std::memset(memory, kGuard, sizeof(memory));
        char* buffer = reinterpret_cast<char*>(memory) + 8;

        const bool ok = to_string(value, buffer, length, 0);

        if (length == needed) {
            CHECK(ok);
            CHECK_TEXT(buffer, expected);
        } else {
            CHECK(!ok);
            // What is left must not read as a shorter number.
            if (length > 0) CHECK(buffer[0] == '\0');
            if (length == 0) CHECK(static_cast<unsigned char>(buffer[0]) == kGuard);
        }

        bool outside_intact = true;
        for (size_t i = 0; i < 8; ++i) outside_intact = outside_intact && memory[i] == kGuard;
        for (size_t i = 8 + length; i < sizeof(memory); ++i) outside_intact = outside_intact && memory[i] == kGuard;
        CHECK(outside_intact);
    }
}

static uint32_t bits_of(float value) {
    uint32_t bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

static float float_of(uint32_t bits) {
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

// Back through the parser the firmware uses for a write, and through strtof.
static bool round_trips(uint32_t bits) {
    char text[TO_STRING_BUFFER_SIZE];
    if (!to_string(float_of(bits), text, sizeof(text), 0)) return false;

    float through_firmware_parser = 0.0f;
    if (!from_string(text, sizeof(text), &through_firmware_parser, 0)) return false;
    return bits_of(through_firmware_parser) == bits && bits_of(std::strtof(text, nullptr)) == bits;
}

class CapturingSink : public AsyncStreamSink {
public:
    void start_write(cbufptr_t buffer, TransferHandle*, Callback<void, WriteResult> completer) final {
        bytes.append(reinterpret_cast<const char*>(buffer.begin()), buffer.size());
        completer.invoke({kStreamOk, buffer.end()});
    }
    void cancel_write(TransferHandle) final {}

    std::string bytes;
};

// The bytes that leave the real sink after the real read command.
static std::string answer(const char* command, bool use_checksum) {
    CapturingSink sink;
    AsciiProtocol protocol(nullptr, &sink);
    char line[MAX_LINE_LENGTH];
    std::snprintf(line, sizeof(line), "%s", command);
    protocol.cmd_read_property(line, use_checksum);
    return sink.bytes;
}

static std::string with_checksum(const std::string& text) {
    unsigned checksum = 0;
    for (char c : text) checksum ^= static_cast<unsigned char>(c);
    return text + "*" + std::to_string(checksum) + "\r\n";
}

static void check_answer(const char* command, const std::string& value) {
    CHECK_TEXT(answer(command, false), value + "\r\n");
    CHECK_TEXT(answer(command, true), with_checksum(value));
}

// ---- Tests -------------------------------------------------------------------------------------

static void test_integers_are_complete_and_fail_cleanly_when_they_do_not_fit() {
    check_fit<unsigned int>(2147483648u, "2147483648");
    check_fit<unsigned int>(UINT32_MAX, "4294967295");
    check_fit<int>(INT32_MIN, "-2147483648");
    // These two are int32_t and uint32_t on the target.
    check_fit<unsigned long>(2147483648ul, "2147483648");
    check_fit<unsigned long>(UINT32_MAX, "4294967295");
    check_fit<long>(INT32_MIN, "-2147483648");
    check_fit<short>(SHRT_MIN, "-32768");
    check_fit<unsigned short>(USHRT_MAX, "65535");
    check_fit<unsigned char>(UCHAR_MAX, "255");
}

static void test_64_bit_values_do_not_rely_on_printf() {
    const int calls_before = snprintf_calls;

    check_fit<unsigned long long>(UINT64_MAX, "18446744073709551615");
    check_fit<unsigned long long>(0, "0");
    check_fit<unsigned long long>(281474976710655ull, "281474976710655");
    check_fit<long long>(INT64_MIN, "-9223372036854775808");
    check_fit<long long>(INT64_MAX, "9223372036854775807");
    check_fit<long long>(-1, "-1");
    check_fit<long long>(0, "0");

    CHECK(snprintf_calls == calls_before);
}

static void test_bool() {
    check_fit<bool>(true, "1");
    check_fit<bool>(false, "0");
}

static void test_a_float_shows_all_its_digits() {
    check_fit<float>(25.0f, "25");
    check_fit<float>(0.5f, "0.5");
    check_fit<float>(-1.5f, "-1.5");
    check_fit<float>(0.1f, "0.100000001");
    check_fit<float>(16777216.0f, "16777216");
    check_fit<float>(1e9f, "1e+09");
    check_fit<float>(FLT_MAX, "3.40282347e+38");
    check_fit<float>(-FLT_MAX, "-3.40282347e+38");
    check_fit<float>(FLT_MIN, "1.17549435e-38");
    check_fit<float>(-FLT_MIN, "-1.17549435e-38");
    check_fit<float>(float_of(1), "1.40129846e-45");
    check_fit<float>(float_of(0x80000001u), "-1.40129846e-45");
}

static void test_zero_infinity_and_nan() {
    check_fit<float>(float_of(0x00000000u), "0");
    check_fit<float>(float_of(0x80000000u), "-0");
    check_fit<float>(float_of(0x7F800000u), "inf");
    check_fit<float>(float_of(0xFF800000u), "-inf");
    check_fit<float>(float_of(0x7FC00000u), "nan");
    check_fit<float>(float_of(0xFFC00000u), "nan");
    check_fit<float>(float_of(0x7F800001u), "nan");

    CHECK(round_trips(0x00000000u));
    CHECK(round_trips(0x80000000u));
}

static void test_a_finite_float_reads_back_as_the_same_float() {
    // The values a %f with six decimals turned into 0.000000 or shortened.
    for (float value : {FLT_MIN, -FLT_MIN, FLT_MAX, -FLT_MAX, FLT_EPSILON, 1.0f / 3.0f, 0.1f, 0.000012345f,
                        0.0000005f, 1e-20f, 1e20f, 123456792.0f, 25.0f, 1.0f, -1.0f})
        CHECK(round_trips(bits_of(value)));
    CHECK(round_trips(1));
    CHECK(round_trips(0x80000001u));
    CHECK(round_trips(0x007FFFFFu));    // largest denormal
    CHECK(round_trips(0x3F800001u));    // next above 1
    CHECK(round_trips(0x3F7FFFFFu));    // next below 1

    int failed = 0;
    int tried = 0;
    const auto try_bits = [&](uint32_t bits) {
        if ((bits & 0x7F800000u) == 0x7F800000u) return;    // infinity and NaN have no such promise
        tried++;
        if (!round_trips(bits) && ++failed <= 3) std::printf("  no round trip for bits 0x%08lX\n", static_cast<unsigned long>(bits));
    };

    // Every exponent, at the ends and in the middle of the mantissa, both signs.
    for (uint32_t exponent = 0; exponent < 255; ++exponent)
        for (uint32_t mantissa : {0u, 1u, 0x400000u, 0x7FFFFEu, 0x7FFFFFu})
            for (uint32_t sign : {0u, 0x80000000u})
                try_bits(sign | (exponent << 23) | mantissa);

    std::mt19937 random(20261102u);
    for (int i = 0; i < 100000; ++i) try_bits(static_cast<uint32_t>(random()));

    CHECK(tried > 100000);
    CHECK(failed == 0);
}

static void test_the_named_size_holds_every_value() {
    char buffer[64];
    CHECK(to_string<unsigned long long>(UINT64_MAX, buffer, TO_STRING_BUFFER_SIZE, 0));
    CHECK(to_string<long long>(INT64_MIN, buffer, TO_STRING_BUFFER_SIZE, 0));
    CHECK(to_string<float>(-FLT_MIN, buffer, TO_STRING_BUFFER_SIZE, 0));
    CHECK(!to_string<unsigned long long>(UINT64_MAX, buffer, TO_STRING_BUFFER_SIZE - 1, 0));
    CHECK(!to_string<long long>(INT64_MIN, buffer, TO_STRING_BUFFER_SIZE - 1, 0));
}

static void test_a_read_answers_with_the_whole_value_and_line_end() {
    check_answer("r u32", "2147483648");
    check_answer("r u32_max", "4294967295");
    check_answer("r i32_min", "-2147483648");
    check_answer("r u64_max", "18446744073709551615");
    check_answer("r i64_min", "-9223372036854775808");
    check_answer("r serial", "281474976710655");
    check_answer("r longest_float", "-1.17549435e-38");
    check_answer("r flag", "1");
    check_answer("r mode", "261");
    check_answer("r readonly", "2147483648");
    check_answer("r node.u32", "2147483648");

    // Worked out by hand: '1' is 0x31, so the checksum of that one character is 49.
    CHECK_TEXT(answer("r flag", true), "1*49\r\n");

    // The reader gets back what the property holds, not just something close.
    float small = 0.0f;
    std::string line = answer("r small", false);
    CHECK(line.size() > 2 && line.compare(line.size() - 2, 2, "\r\n") == 0);
    std::string text = line.substr(0, line.size() - 2);
    CHECK(from_string(text.c_str(), text.size() + 1, &small, 0));
    CHECK(bits_of(small) == bits_of(test_values.small));
}

static void test_error_answers_stay_as_they_are() {
    CHECK_TEXT(answer("r", false), "invalid command format\r\n");
    CHECK_TEXT(answer("r", true), with_checksum("invalid command format"));
    CHECK_TEXT(answer("r nope", false), "invalid property\r\n");
    CHECK_TEXT(answer("r nope", true), with_checksum("invalid property"));
    CHECK_TEXT(answer("r node", false), "invalid property\r\n");
    CHECK_TEXT(answer("r u32.nope", false), "invalid property\r\n");
    CHECK_TEXT(answer("r unsupported", false), "not implemented\r\n");
    CHECK_TEXT(answer("r unsupported", true), with_checksum("not implemented"));
}

int main() {
    // Linking with -ffast-math switches this x86 process to flushing denormals to zero. The firmware
    // converts a float to double in software and is not affected.
#if defined(__SSE__)
    _mm_setcsr(_mm_getcsr() & ~(0x8000u | 0x0040u));
#endif

    test_integers_are_complete_and_fail_cleanly_when_they_do_not_fit();
    test_64_bit_values_do_not_rely_on_printf();
    test_bool();
    test_a_float_shows_all_its_digits();
    test_zero_infinity_and_nan();
    test_a_finite_float_reads_back_as_the_same_float();
    test_the_named_size_holds_every_value();
    test_a_read_answers_with_the_whole_value_and_line_end();
    test_error_answers_stay_as_they_are();
    return TEST_RESULT();
}
