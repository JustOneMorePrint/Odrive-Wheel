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

clean:
	rm -rf $(BUILD_DIR)

-include $(TESTS:=.d)

.PHONY: check clean
