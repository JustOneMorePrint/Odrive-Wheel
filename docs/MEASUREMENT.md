# Rates, latency and measurement

What runs how often, what to measure, and how an optimization earns its place. No package claims a benefit without
a measurement before and after it.

## What really runs at which rate

The firmware is not 8 kHz throughout. Only the motor control part is.

| Part | Rate | Source |
|---|---|---|
| PWM carrier | 24 kHz | `TIM_1_8_PERIOD_CLOCKS` 3500, up and down at 168 MHz |
| Sampling, control loop, current controller, encoder update | 8 kHz, 125 µs | `TIM8_UP` handler and `ControlLoop_IRQHandler` in `board_v3.cpp`, `current_meas_period` |
| Torque setpoint `input_torque_` | Written at **1 kHz**, then held for about 8 control loops | `odrive_bridge_set_input_torque` from `ffb_task.cpp` |
| Axis effects, endstop, game effects, filters, EQ | **1 kHz** | `ffb_thread`, `osDelayUntil` with 1 ms, `calcfrequency` |
| HID in and out | 1 ms polling interval at most | `usb_descriptors.c`, sent from the same thread |
| MT6835 read | Thread kicked by the control loop, may skip a read, the PLL bridges it | `enc_spi_thread` in `odrive_bridge.cpp` |
| Thermistors, bus checks | Control loop, see `task_times` | `odrive-interface.yaml` |
| Game | Set by the game, not known here, and usually below 1 kHz | – |
| Telemetry through SimHub | About 10 Hz free, 60 Hz licensed, no part of the torque path | Backlog in `docs/ROADMAP.md` |

So a change at the game's end passes USB, a 1 kHz task and its filters before it reaches an 8 kHz current loop, whose
bandwidth is a parameter of its own.

## What to measure

| Id | Metric | From, to | How | Instrument |
|---|---|---|---|---|
| M1 | Input delay | HID output report arrives, its value in `EffectsCalculator` | Time stamps in firmware, against a known test report | To add, counters only |
| M2 | Setpoint processing | Effects done, `input_torque_` written, read by the controller | Time stamps, age of the setpoint at the control loop | To add |
| M3 | Current response | Step in `input_torque_`, `Iq_measured` at 90 % | Capture at 8 kHz, at low current, run by the user | To add, buffer in firmware. Rise time and overshoot |
| M4 | Filter delay | Per filter and for the whole chain | Calculated, then a sine through the real code on the host, then on the board | Host test first |
| M5 | Runtime and jitter | Control loop, sampling, FFB thread | `task_times` and `TaskTimer` for the control loop, period statistics for the thread | Control loop exists, thread to add |
| M6 | Missed deadlines | Control loop, FFB tick, encoder read | `CONTROL_DEADLINE_MISSED`, `TIMER_UPDATE_MISSED`, `BAD_TIMING` exist and disarm, counters for ticks and skipped reads to add | Partly exists |
| M7 | End to end, to the current | Game force command to `Iq_measured` | Test report on the PC, `Iq_measured` in the HID stream at 1 kHz, a scope on a GPIO for what the host cannot see | Plugin, package 2 |
| M8 | Measured torque, optional | Force at the wheel or shaft | A torque sensor, or a lever and a scale, run by the user | None required |

**Current is not torque.** M3 and M7 measure current. With a known Kt, `Iq_measured` × Kt estimates the motor's torque, and that estimate leaves out
friction, cogging and saturation. A statement about real torque needs M8, and is named as a measurement of torque only then. No goal
here requires an additional sensor.

**Standard load:** all axis effects on, a constant and a periodic game effect, HID at full rate, serial status polling, motor
in closed loop at low force. Every number states the load, the board, the encoder and `sys.build?`.

**Calculated today, not measured:** the low frequency delay of a second order low-pass is 1/(Q·2πf).

| Filter (`ffb_task.cpp`) | Setting | Delay |
|---|---|---|
| Damper | 60 Hz, Q 0.55 | 4.8 ms |
| Friction | 50 Hz, Q 0.20 | 15.9 ms |
| Inertia | 20 Hz, Q 0.20 | 39.8 ms |
| Acceleration, first order | 100 Hz | 1.6 ms |

## Goals

Every goal is checked by the metric named. Numbers come after the baseline, a number chosen before it would be invented.

| Goal | Check |
|---|---|
| No missed control deadline, in 30 minutes under the standard load and with each new function | M6 is zero |
| The control loop keeps a margin to its 125 µs | M5: worst case stated, margin set after the baseline |
| A function in the 8 kHz path costs what it says | M5 before and after, in the PR (rule 8 in `CLAUDE.md`) |
| The FFB tick stays on time | M5, M6: worst period and missed ticks |
| A filter change reduces delay without new noise | M4 and the noise in `Iq_measured` at rest |
| A change that claims to feel better shows it | The metrics named in its PR, plus the user's verdict, marked as such |

A change that does not show its benefit is not merged as an improvement. It may stay as a variant behind a switch if the user
wants to compare, or it is dropped.

## Three separate steps for package 3

The old package mixed these. They are checked separately, in this order.

| Step | Contents | Rate | Accepted when |
|---|---|---|---|
| Protection | Endstop priority, soft start, speed limit, out of bounds, supply limiter, I²t, watchdog, HID timeout, E-stop input | What each needs. Starts at 1 kHz | Each fault case is shown to end in a safe state, in host tests, then by the user. No switch |
| Torque processing | Expo on force, new effect filters, center damping, slew in Nm/ms, `pos_estimate` | 1 kHz | M4 and M2 before and after. Switches where a comparison makes sense |
| Higher execution rate, experimental | Parts of the torque path in the control loop, hook in `controller.cpp` | 8 kHz | See below. Behind a switch, may be dropped |

**The experiment.** A virtual spring is passive while b > KT/2 + |B|, so a shorter period T leaves room for a stiffer K. That is
the reason to try, not a result: the bound assumes T is the main delay, and the encoder, its PLL and the current loop add their own. "Up
to 8× harder" is therefore no goal. To be measured, in order: M5 budget in the control loop, the highest stable endstop stiffness at
1 kHz, the same at 8 kHz, on the same mechanics. It continues only if M6 stays at zero and the stiffness gain is real.

## Claims replaced

| Earlier claim | Status | Now |
|---|---|---|
| The whole chain runs at 8 kHz (`docs/TORQUE_CONTROL.md`) | Wrong | The table above |
| Up to about 8× harder endstop | Unproven | The experiment above |
| New filters: no filter delay | Overstated | Delay reduced, M4 |
| 15–40 ms for the old filters | Calculated, plausible | Table above, to be measured |
| 6–12 % more speed reserve from the modulation limit | Unproven | Measure: torque against speed at the limit, and current noise, with the value in the PR |
| Observer: acceleration and driver torque without delay | Overstated | Delay and noise against the present derivative, M4 and M3 |
| Hands-off reaction: wheel does not snap | Unproven | Measure on real releases, never a protection |
| Anticogging, harmonic compensation: smoother | Unproven here | Velocity ripple and torque ripple before and after, per motor |
