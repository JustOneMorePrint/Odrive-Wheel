# Host tests for HAL-free logic modules. Every test_*.cpp is one test program.

BUILD_DIR = build
TESTS     = $(patsubst %.cpp,$(BUILD_DIR)/%,$(wildcard test_*.cpp))

CXXFLAGS += -std=gnu++17 -Wall -Wextra -Werror -g -MMD -MP -I. -I../../Odrive-Wheel/inc

check: $(TESTS)
	@set -e; for t in $(TESTS); do ./$$t; done
	@CXX="$(CXX)" MAKE="$(MAKE)" sh test_build_info.sh
	@MAKE="$(MAKE)" sh test_make_clean.sh
	@sh test_flash_paths.sh

$(BUILD_DIR)/%: %.cpp
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) $< -o $@

# [pkgS] Compiles the text of real production functions against the stand-ins in stubs/. That text is
# written for a 32-bit target (uint32_t casts of pointers), hence -fpermissive and no -Werror here.
MOTOR_CONTROL = ../../ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl

$(BUILD_DIR)/test_arm_refusal: CXXFLAGS += -fpermissive -Wno-error -I$(BUILD_DIR) -Istubs -I$(MOTOR_CONTROL)
$(BUILD_DIR)/test_arm_refusal: $(BUILD_DIR)/prod_arm_paths.inc

$(BUILD_DIR)/prod_arm_paths.inc: extract_prod_functions.sh ../../Odrive-Wheel/src/odrive_bridge.cpp \
		$(MOTOR_CONTROL)/motor.cpp $(MOTOR_CONTROL)/axis.cpp $(MOTOR_CONTROL)/encoder.cpp $(MOTOR_CONTROL)/utils.cpp
	@mkdir -p $(dir $@)
	@sh extract_prod_functions.sh > $@.tmp
	@mv $@.tmp $@

# Real formatters, real sink, real read path. With the math flags of the firmware (Odrive-Wheel/Makefile),
# because -ffast-math changes what the usual ways of asking for the sign of a zero return.
FIRMWARE = ../../ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware

$(BUILD_DIR)/test_ascii_read: CXXFLAGS += -ffast-math -fno-finite-math-only -fno-math-errno -I$(BUILD_DIR) \
	-isystem $(FIRMWARE)/fibre-cpp/include -isystem $(FIRMWARE)/fibre-cpp -isystem $(FIRMWARE)/communication
$(BUILD_DIR)/test_ascii_read: $(BUILD_DIR)/prod_ascii_read.inc

$(BUILD_DIR)/prod_ascii_read.inc: extract_prod_functions.sh $(FIRMWARE)/communication/ascii_protocol.cpp
	@mkdir -p $(dir $@)
	@sh extract_prod_functions.sh ascii-read > $@.tmp
	@mv $@.tmp $@

clean:
	rm -rf $(BUILD_DIR)

-include $(TESTS:=.d)

.PHONY: check clean
