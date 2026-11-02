#include "firmware_capabilities.h"

#include <string.h>

int firmware_capabilities_cmd(uint8_t instance, CmdType type, const char *value,
                              char *reply_buf, size_t reply_size) {
    (void)instance;
    (void)value;
    if (type != CMD_TYPE_GET) return -1;
    if (reply_size < sizeof(FIRMWARE_CAPABILITIES_REPLY)) return -1;
    memcpy(reply_buf, FIRMWARE_CAPABILITIES_REPLY, sizeof(FIRMWARE_CAPABILITIES_REPLY));
    return 0;
}
