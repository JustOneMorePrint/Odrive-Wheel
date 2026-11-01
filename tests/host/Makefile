# Host tests for HAL-free logic modules. Every test_*.cpp is one test program.

BUILD_DIR = build
TESTS     = $(patsubst %.cpp,$(BUILD_DIR)/%,$(wildcard test_*.cpp))

CXXFLAGS += -std=gnu++17 -Wall -Wextra -Werror -g -MMD -MP -I.

check: $(TESTS)
	@set -e; for t in $(TESTS); do ./$$t; done
	@CXX="$(CXX)" MAKE="$(MAKE)" sh test_build_info.sh

$(BUILD_DIR)/%: %.cpp
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) $< -o $@

clean:
	rm -rf $(BUILD_DIR)

-include $(TESTS:=.d)

.PHONY: check clean
