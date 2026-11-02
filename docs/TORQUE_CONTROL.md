# Torque & Current Control in TORQUE_CONTROL Mode

How the FFB pipeline turns a "torque demand" (in Nm) into actual current
flowing in the motor windings — and which parameters affect each stage.

This is the mode the wheel uses (`axis0.controller.config.control_mode = 1`).
In this mode the position/velocity PID loops are bypassed: `input_torque_`
goes straight through to the current controller. **PID gains
(`pos_gain`, `vel_gain`, `vel_integrator_*`) are ignored in this mode** —
see the warning in the Controller tab.

## Pipeline overview

```
[FFB stack computes]              pending_torque_ (Nm)
    ↓
[axis.maxtorque cap]              totalTorque clamped to ±maxtorque
[axis.fxratio multiplier]         × fxratio
[axis.maxtorquerate slew]         delta limit per ms
    ↓
input_torque_ ─────►  Controller  ─────►  torque_setpoint_
                       (PASSTHROUGH:                ↓
                        no PID)        [Tlim = effective_current_lim × torque_constant]
                                       clamped to ±Tlim
                                                    ↓
                              ┌──────  Motor.update()  ──────┐
                              │  iq = torque / Kt             │
                              │  2-norm clamp (Id² + Iq²)     │
                              │  + feed-forward (R, L, bEMF)  │
                              └──────────┬────────────────────┘
                                         ↓
                              [PI current loop, bandwidth = current_control_bandwidth]
                                         ↓
                              [Vdq → SVPWM → gate driver → MOSFETs]
                                         ↓
                              [shunt sense → Iq feedback to PI loop]
```

The FFB layer above `input_torque_` runs at 1 kHz. From the controller down it runs at the FOC ISR rate,
8 kHz, synchronous with PWM updates, so one setpoint is held for about 8 control loops. See `docs/MEASUREMENT.md`.

## Stage-by-stage parameters

### 🟢 FFB layer (`axis.*`) — before the controller

These knobs live in `Odrive-Wheel/src/ffb_task.cpp` (OpenFFBoard cmdparser
namespace, persisted in S1+S2 EEPROM). Tweaks via the **FFB Wheel** tab in
the configurator.

| Parameter | Effect |
|---|---|
| `axis.maxtorque` | Hard cap in Nm. Limits the top of FFB output regardless of game request. |
| `axis.fxratio` | 0..1 — final multiplier. Reduces everything proportionally. Default 0.80. |
| `axis.maxtorquerate` | Slew rate (counts/ms). 0 = off. Smooths transients. Use carefully — kills detail. |
| `axis.expo` + `axis.exposcale` | Exponential response curve. 0 = linear, positive = dead center, negative = sensitive center. |

### 🟡 Torque cap (motor side)

The Controller computes `Tlim = effective_current_lim × torque_constant`
and clamps `torque_setpoint_` to ±Tlim. The cap is updated continuously
based on thermal state.

```cpp
// motor.cpp::max_available_torque()
float max_torque = effective_current_lim_ * config_.torque_constant;
return std::clamp(max_torque, 0.0f, config_.torque_lim);
```

`effective_current_lim` is the **minimum** of:

1. `motor.config.current_lim`
2. `max_allowed_current_` (derived from `requested_current_range` and shunt amp gain)
3. Motor thermistor current limit (derates near `inverter_temp_limit_*`)
4. FET thermistor current limit

| Parameter | Effect |
|---|---|
| `motor.config.current_lim` | **Primary current cap** in A. Defines the main torque ceiling. Start low (10 A) and increase. |
| `motor.config.torque_lim` | Hard torque cap in Nm. `inf` = only constrained by `current_lim × Kt`. |
| `motor.config.current_lim_margin` | Safety margin in A between `current_lim` and the hardware over-current trip. Typical 8–10 A. |
| `motor.config.requested_current_range` | Range used to set the shunt amplifier gain. Must be ≥ `current_lim + current_lim_margin`. Higher → less precision but more headroom. |

### 🔵 Torque ↔ Current conversion

Single line, single parameter:

```cpp
// motor.cpp:591
iq = torque / config_.torque_constant;
```

| Parameter | Effect |
|---|---|
| `motor.config.torque_constant` | **Most critical conversion factor**, in Nm/A. Wrong value means actual torque ≠ requested torque. Compute as `8.27 / motor_kv`. Typical: 0.87 for 18 Nm BLDC servo, ~0.55 for hoverboard motors. |

### 🔴 FOC current loop — where the "feel" comes from

Gains are auto-derived from three physical parameters:

```cpp
// motor.cpp::update_current_controller_gains()
p_gain     = current_control_bandwidth × phase_inductance
plant_pole = phase_resistance / phase_inductance
i_gain     = plant_pole × p_gain
```

| Parameter | Effect |
|---|---|
| `motor.config.current_control_bandwidth` | **Closed-loop PI bandwidth in rad/s.** This is the most important knob for FFB feel. Range 100–2000. High → fast/electric/aggressive response. Low → smooth/mechanical/filtered feel. |
| `motor.config.phase_resistance` | Ω. Measured by `MOTOR_CALIBRATION` (state 4). Feeds into `i_gain` and the R-feed-forward term. Wrong value → ringing or sluggish response. |
| `motor.config.phase_inductance` | H. Measured by `MOTOR_CALIBRATION`. Feeds into `p_gain` and the wL-feed-forward term. |

### 🟣 Feed-forward — compensates back-EMF and resistive drop

Without feed-forward the PI loop alone has to react to these disturbances,
producing perceptible lag. Feed-forward injects them into the voltage
command directly:

```cpp
// motor.cpp:616-635
if (R_wL_FF_enable) {
    vd -= phase_vel × phase_inductance × iq;
    vq += phase_vel × phase_inductance × id;
    vd += phase_resistance × id;
    vq += phase_resistance × iq;
}
if (bEMF_FF_enable) {
    vq += phase_vel × (2/3) × (torque_constant / pole_pairs);
}
```

| Parameter | Effect |
|---|---|
| `motor.config.R_wL_FF_enable` | Adds resistive + inductive feed-forward. **Recommended True** for any setup. |
| `motor.config.bEMF_FF_enable` | Adds back-EMF feed-forward. Reduces lag at high velocity. **Only enable after `torque_constant` is confirmed correct** — otherwise it injects noise. |
| `motor.config.pole_pairs` | Used in the bEMF feed-forward calculation. Wrong value distorts the term. |

### ⚫ DC bus + thermal limits — additional caps on top torque

Even after torque is converted to current, several other limits can clamp
the final output:

| Parameter | Effect |
|---|---|
| `motor.config.inverter_temp_limit_lower` | °C where current starts derating linearly. Default 100. |
| `motor.config.inverter_temp_limit_upper` | °C where ODrive disarms with `INVERTER_OVER_TEMP`. Default 120. |
| `config.dc_max_positive_current` | A — max current drawn from the PSU. If exceeded, vbus sags, modulation saturates, possible disarm. |
| `config.dc_max_negative_current` | A — max regen current returned to PSU. If exceeded, brake saturates or OVP triggers. |
| `config.dc_bus_overvoltage_trip_level` | V — vbus ceiling. Exceeded during regen → `DC_BUS_OVER_VOLTAGE` and disarm. |
| `motor.config.I_bus_hard_max` / `I_bus_hard_min` | Hard limits on motor DC bus current (consumption / regen). |

## Practical tuning order

When setting up a new motor from scratch, prioritize these parameters in
this order:

1. **`torque_constant`** — fundamental. If wrong, every downstream value
   is miscalibrated. Use `8.27 / motor_kv` from the datasheet, or measure
   with a torque sensor against known current.
2. **`phase_resistance` + `phase_inductance`** — measured automatically by
   `MOTOR_CALIBRATION` (state 4). Without these, the PI gains are wrong.
3. **`current_lim`** — start conservative (10 A) to avoid frying the motor
   or PSU. Increase gradually.
4. **`current_control_bandwidth`** — pick based on motor type:
   - Industrial servo BLDC (low inductance): 1000 Hz works well
   - Hoverboard (high inductance): 200–500 Hz
   - Gimbal motors (very low torque, high R): 100–300 Hz
5. **`R_wL_FF_enable = True`** — almost always helps, low risk.
6. **`axis.maxtorque`** (FFB layer) — converts the internal cap into the
   game's torque request. Start at 3 Nm and ramp up.
7. **`bEMF_FF_enable = True`** — only after `torque_constant` is confirmed.
8. **`axis.maxtorquerate`** — only if you feel transients are too aggressive.
   Default 0 (off).

## Common symptoms vs. likely culprit

| Symptom | Suspect |
|---|---|
| Motor pushes but torque feels weak overall | `torque_constant` too high → fewer Amps requested for same Nm. Or `current_lim` too low. |
| High-frequency vibration when torque is high | `current_control_bandwidth` too high or `phase_inductance` mismeasured |
| Torque clips at some level regardless of game request | Effective current limit (check `current_lim`, thermal limits, `requested_current_range`) |
| FFB feels laggy | Bandwidth too low, or feed-forward disabled |
| ODrive disarms with `MODULATION_MAGNITUDE` error | Vbus too low for the requested peak torque → reduce `current_lim` or use higher-voltage PSU |
| ODrive disarms with `DC_BUS_OVER_CURRENT` | `dc_max_positive_current` or `I_bus_hard_max` too tight |
| Disarm with `DC_BUS_OVER_VOLTAGE` during heavy regen | Brake resistor undersized, or `dc_bus_overvoltage_trip_level` too low |

## Live diagnostics (Debug tab)

Watch these fields while the wheel is moving / under FFB load:

| Field | What it shows |
|---|---|
| `axis0.controller.input_torque` | Torque arriving from the FFB stack (Nm) |
| `axis0.controller.torque_setpoint` | Torque after Tlim clamp (post-`max_available_torque`) |
| `axis0.motor.current_control.Iq_setpoint` | Iq commanded after `torque / Kt` (A) |
| `axis0.motor.current_control.Iq_measured` | Iq actually flowing, measured via shunts (A) |
| `axis0.motor.current_control.Id_measured` | Id (should stay near 0 for BLDC, non-zero for ACIM) |
| `motor.fet_thermistor.temperature` | FET temperature, drives thermal derating |
| `vbus_voltage` / `ibus` | Bus monitoring — drops indicate PSU saturation |

The error `Iq_setpoint - Iq_measured` is the residual of the PI loop. In
steady state it should be near zero. During transients it scales with
bandwidth — higher bandwidth, smaller transient error.

## What is NOT used in TORQUE_CONTROL mode

These exist in the SCHEMA but are bypassed by the firmware in this mode
(see Controller tab — they're greyed out with an "ignored in TORQUE" tag):

- `pos_gain` — only active in `POSITION_CONTROL` mode
- `vel_gain` — only active in `VELOCITY_CONTROL` mode (or as a vel-clamp
  factor if `enable_torque_mode_vel_limit = True`)
- `vel_integrator_gain` / `vel_integrator_limit`
- `inertia` (only used in `VEL_RAMP` and `POS_FILTER` input modes)
- `enable_gain_scheduling` / `gain_scheduling_width`
- `circular_setpoints` and related
- `homing_speed` / mirror options

For the FFB use case these knobs are inert and changing them has no
observable effect on motor behavior.

## References

- `ODrive-fw-v0.5.6/Firmware/MotorControl/controller.cpp` — torque setpoint
  clamping, control mode dispatch
- `ODrive-fw-v0.5.6/Firmware/MotorControl/motor.cpp` — torque-to-current
  conversion, current PI loop tuning, feed-forward
- `Odrive-Wheel/src/ffb_task.cpp` — `setEffectTorque()` and `update()`,
  where the FFB-side caps and `pending_torque_` are computed
