# Hardware, sensors and calibration

The binding hardware targets and how setup treats them. What is planned is kept apart from what has been checked.
Nothing here sets a motor limit: those come from the motor's data, not from this project.

## Target boards

| Board | Axes | Firmware of this fork | Support level | Sources |
|---|---|---|---|---|
| MKS XDrive Mini | 1 | The only build: `Odrive-Wheel/Makefile` says "MKS XDrive Mini style: v3.6-56V" | **Build exists.** Test hardware of the user, hardware tests open | [Makerbase: firmware and schematic](https://github.com/makerbase-motor/MKS-ODrive/tree/master/Firmware/MKS%20ODrive%20MINI) |
| MKS XDrive S | 1 | None | **Planned** | `docs/MKS ODrive_S schematic_v3.6.pdf`, [Makerbase: "MKS ODrive-S"](https://github.com/makerbase-motor/MKS-ODrive/tree/master/Firmware/MKS%20ODrive-S) |
| MKS XDrive | 2 | None. FFB drives `axes[0]` only, M1 stays unused | **Planned** | [Makerbase: "ODrive_V3.6"](https://github.com/makerbase-motor/MKS-ODrive/tree/master/Firmware/ODrive_V3.6) |

Axis counts are from [the FFBeast wiring page](https://ffbeast.github.io/docs/en/connection_odrive.html), which also lists
these boards. Makerbase's own names differ, and it ships a separate firmware for each, which is why one image cannot be
assumed to fit all. That the folders above are these retail boards is a reading of the names, to be confirmed on real
boards. Makerbase's firmware is v0.5.1, this fork is on 0.5.6.

The levels, each needing the one before:

| Level | Means | Shown by |
|---|---|---|
| Planned | Intended, no statement about function | – |
| Build exists | A firmware for it is built in CI | CI artifact |
| Reads | Connects, reads values, voltage reading correct against a meter | User's test, recorded here |
| Calibrates | Motor and encoder calibration work | User's test, recorded here |
| Verified | FFB in a game, no faults, backup and restore | User's test, recorded here |

Only the last three are claims of compatibility. Upstream also names ODESC V4.2. It is no target of this fork, its
code (MT6835) stays and is not tested here.

**Selection in the plugin.** The user selects the board. The selection must fit the firmware, so the firmware has to say
which board it was built for: `hw_version_*` says 3.6-56 on all of them and cannot tell. Planned:

1. A board identity in the firmware, as a new capability field.
2. The plugin compares selection and firmware, and offers no write function on a mismatch.
3. The flasher reads the identity from the image and refuses a mismatch before erasing anything.

**To compare for each board before a build exists:** pin map, gate driver handling, bus voltage divider, shunt values,
FET thermistor channel, default encoder, brake output. The divider is known to vary: `sys.vbusdiv` defaults to 19 for the
Mini, and the catalog names 11 for an original v3.6. Whether one image with a runtime selection or one build per board
is right follows from this comparison.

## Power supply and brake resistor

**Nominal voltage 24–48 V.** The range this project plans for. The input range in the plugin follows in the setup stage, today
it only asks for a positive value.

**Voltage limits in the firmware**, as `odrive_main.h` has them. All are configurable:

| Value | Default | What it does |
|---|---|---|
| `dc_bus_overvoltage_trip_level` | 30 V | Above it the firmware stops with `DC_BUS_OVER_VOLTAGE` (`do_fast_checks`) |
| `dc_bus_undervoltage_trip_level` | 4 V | Below it the firmware stops with `DC_BUS_UNDER_VOLTAGE` |
| `dc_bus_overvoltage_ramp_start`, `_end` | 59.92 V, from `1.07 × HW_VERSION_VOLTAGE` | Only the overvoltage ramp, which is off by default |

So a board with the default configuration on a supply above 30 V reports the error until setup has set the trip level. These are
settings. **No electrical rating of a board follows from any of them**, that takes the maker's data.

**Power supply data** stays as it is: DC Output Voltage, Continuous Output Current, Current Headroom, Short-time overload
supported, Short-Time Current, Maximum Duration in ms. Details in `docs/packages/PKG1.md`.

**Three currents, kept apart.** One does not follow from another, and no field is filled from another.

| Quantity | Limited by | Set through |
|---|---|---|
| Power supply current, DC | The supply's data | `dc_max_positive_current`, later the limiter of package 3 |
| Motor phase current | The motor's data and the board | `motor.config.current_lim` |
| Regeneration, into the bus | The supply's tolerance, brake resistor | `dc_max_negative_current`, `max_regen_current`, the brake resistor |

**Repeated peaks need more data.** Short-Time Current and Maximum Duration describe one event. Whether it can repeat takes
the maker's statement on recovery time or duty cycle, and on how the limit behaves when warm. Without that the plugin
offers no repeating peak budget and does not assume one.

**Brake resistor.** A decimal value above 0 up to and including 20 Ω. Disabled is a state of its own (`enable_brake_resistor`
= 0), never a value of 0 Ω. The range check follows in the setup stage, the editor of PR #7 only requires above 0. Taking effect
needs a reboot, according to the firmware interface.

## Temperature sensors

As implemented in the build of eagabriel/Odrive-Wheel, and kept by this fork. The NTC calculator exists in the web client and
moves to the plugin in step 1d. The plugin's catalog already lists these values, read-only.

| | FET sensor | Motor sensor |
|---|---|---|
| Hardware | On the board | External, on a GPIO in `ANALOG_IN` |
| Firmware | `fet_thermistors[]`, fixed polynomial (`board_v3.cpp`) | `motor_thermistors[]`, polynomial from the config |
| Config | `enabled`, `temp_limit_lower`, `temp_limit_upper` | The same, plus `gpio_pin`, `poly_coefficient_0..3` |
| Effect | Current limit falls linearly from lower and is zero at upper. The error `FET_THERMISTOR_OVER_TEMP` follows at upper + 5 °C (`thermistor.cpp`) | The same, `MOTOR_THERMISTOR_OVER_TEMP` |
| Reported | `sys.temp?`, HID `FetTempC` | `sys.motortemp?`, HID `MotorTempC` |
| Setup | – | NTC calculator: β, R at 25 °C, pull-up, reference voltage, range to a third-order fit, with a wiring note |

To carry over knowingly:

- The pull-up in the described motor-sensor circuit is **external**: the STM32 has none in analog mode. Verify the actual board circuit and
  keep the ADC input within its rated voltage in normal operation and fault conditions. A larger pull-up alone does not make a 5 V sensor
  circuit safe, particularly with an open sensor.
- Without a pull-up the reading looks like a valid temperature. The firmware cannot tell a missing sensor, so setup has to ask
  for a plausibility check at a known temperature.
- `0.0` from `sys.temp?` or `sys.motortemp?` is ambiguous: these commands answer it for NaN, and a real 0 °C looks the same. It shows
  neither a valid reading nor a missing sensor. Setup and diagnosis treat it as unclear, and use the plausibility check above.
- The HID report uses `-128` for a value that is not finite. That is not a sensor check either: the firmware's filter goes back to 0 after a NaN
  (`ThermistorCurrentLimiter::update`), and a pin without an ADC channel evaluates the polynomial at -1, which gives a number.
- The KTY84 preset of step 1d adds a positive coefficient sensor to the same polynomial. Threshold PTCs cannot be mapped.
- Limits are the user's from the motor's data. The firmware's defaults are 100 and 120 °C, with the FET sensor on and the motor sensor off. Setup does
  not present them as this motor's values.

## Calibration: what exists and what to take over

**Odrive-Wheel** ("Quick Start" in `Odrive-Wheel/tools/odrive-wheel.html`, `docs/GETTING_STARTED.md`): 13 steps. Flash,
connect, erase, enter power values, enter motor values, enter encoder values, measure R and L (state 4, see below),
find the encoder offset (state 7, about one turn, wheel free), mark as calibrated and save, optional index search with Z
(about 30 s of rotation), enter range and torque, test with a spring. Stored values are used on later starts. Separately: anticogging
(3–10 min, motor free), performance test and coastdown (J, b, breakaway), encoder diagnostics, capture of the mechanical center.

**FFBeast** ([controller setup](https://ffbeast.github.io/docs/en/ffbeast_setup_controller.html)): enter pole pairs and encoder
CPR, then a calibration **at every start**, before the device appears on USB. "Start as the center" makes a small movement to find
the direction, "Automatic center of range" moves to the hard stops. It has settings for calibration magnitude and speed, invert joystick and
invert force, P and I gain with working defaults, a power limit in steps, and a braking resistor limit raised until the
supply stays on during rapid rotation. Its documentation names no R or L measurement and no anticogging. [Its motor
page](https://ffbeast.github.io/docs/en/hardware_motor.html) estimates torque from kV.

**What follows for this project:**

| From | Take over |
|---|---|
| Odrive-Wheel | Measured R and L with gains derived from them, stored and traceable calibration, anticogging as an option, J and b from a test |
| FFBeast | Few inputs first, then a guided order, low calibration power as a named setting, an explicit check of force direction, a guided regeneration test |
| Not taken over | Moving the motor at every power-up, with a wheel attached. Stored values are checked, and motion needs a request in the window |

**Each quantity, and where it comes from:**

| Quantity | Source | State |
|---|---|---|
| Phase resistance, inductance | **Measured**, state 4. Needs `calibration_current` and `resistance_calib_max_voltage`, chosen from the motor's data. No rotation is intended, but the motor is energized and can align or move, whatever the web client's "does NOT spin" suggests | Exists |
| Current controller gains | **Derived** from R, L and the chosen bandwidth | Exists |
| Encoder offset, direction | **Measured**, state 7, wheel free | Exists |
| Index position | **Measured**, state 6, only with Z | Exists |
| Pole pairs | **Entered**, a wrong value fails the offset calibration. Finding it from a low-current lock-in and the encoder is possible | Check exists, finding it is planned |
| Encoder `cpr`, mode | **Entered**, from the encoder's data | Exists |
| Force and position direction | **Checked** at low force in a guided step, the user confirms | Planned, from FFBeast |
| Mechanical center | **Measured** at the position the user sets (`axis.zeroenc`) | Exists |
| Range | **Entered**. Finding hard stops only as an option at low force, for mechanics that have them | Entered exists, finding is planned |
| Magnet and encoder quality | **Measured**, `sys.magnet?`, `sys.encraw?`, MT6835 commands | Exists |
| Cogging map | **Measured**, motor free. Kept only if it helps, see `docs/MEASUREMENT.md` | Exists, package 6 extends it |
| Inertia J, friction b, breakaway | **Measured**, with test forces | In the web client, package 2 |
| Regeneration behaviour | **Observed** in a guided test by the user: does the bus stay in range during rapid rotation | Planned, from FFBeast |
| Torque constant Kt | **Entered**, from Kt, Ke or Kv through the Kt wizard. Not measured by any of these flows, a check with a lever and scale is the user's | Wizard planned, 1d |
| Current limit, peak current and duration | **Needs the motor's data**, not determined | – |
| Thermal limits and time constants, I²t | **Needs the motor's data**, not determined | – |
| Holding brake polarity and timing | **Needs the brake's data** | Planned, 1d |

Where the data is unknown the field stays empty and the function that needs it stays off. There are no invented defaults for a motor.
Each calibration result gets a record, see `docs/FOUNDATIONS.md`.
