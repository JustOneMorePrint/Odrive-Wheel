// [pkg1] sys.capabilities? says what the replies of this firmware guarantee, so that a client does not
// have to guess it from what a reply looks like. Get only: a set or an exec answers ERR, nothing changes.
//
// The reply is exactly these three fields, separated by one space:
//   v           Version of this schema. Another field or another meaning needs another version.
//   ascii_full  1: an "r <path>" reply holds the whole number, or an error. It is never cut.
//   float32_rt  1: a float in an "r <path>" reply reads back as the same float32.
//
// tests/host/test_ascii_read.cpp holds both flags against the real formatting. Change the formatting
// in fibre-cpp/protocol.hpp or ascii_protocol.cpp and that test decides whether they still hold.
// Clients read the contract in docs/packages/PKG1.md.

#ifndef FIRMWARE_CAPABILITIES_H_
#define FIRMWARE_CAPABILITIES_H_

#include "cmdparser.h"

#define FIRMWARE_CAPABILITIES_REPLY "v=1 ascii_full=1 float32_rt=1"

#ifdef __cplusplus
extern "C" {
#endif

// A CmdHandler. Writes nothing unless it can write the whole reply.
int firmware_capabilities_cmd(uint8_t instance, CmdType type, const char *value,
                              char *reply_buf, size_t reply_size);

#ifdef __cplusplus
}
#endif

#endif
