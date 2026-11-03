# Shared foundations

Every function that talks to the board builds on this, instead of bringing its own rules. A package may
add to it, and then everyone gets the addition. Planning is in `docs/ROADMAP.md`, standing rules in `CLAUDE.md`.

## What exists

| Piece | File | What it gives |
|---|---|---|
| One owner of the port | `Core/OdriveSerialService.cs` | One worker thread, one command on the wire, priorities, resync after a timeout, the software stop ahead of everything |
| Reading and writing | `Core/OdriveParameterService.cs` | Both dialects, results that say how far a line got |
| Operation binding | `SerialOperationContext` in `Core/OdriveSerialTypes.cs` | A command goes out only while its connection and stop state still hold, checked by the worker right before the line. `ForReading` also accepts a finished stop with its latch set, and lets nothing but reads through |
| Firmware capabilities | `Core/OdriveFirmwareCapabilities.cs`, `sys.capabilities?` | What the firmware of this connection declares, contract in `docs/packages/PKG1.md` |
| First operation built on it | `Core/OdriveRamApply.cs` | Review, fresh check, write, readback, for Power / Brake |
| A reading operation on it | `Core/OdriveCatalogSnapshot.cs` | The catalog snapshot: every catalog path once, bound to one connection, stop state and capability state, `docs/packages/PKG1.md` |
| Flash guard | `src/flash_guard.c`, `safe.status?` | The firmware refuses flash operations with an active motor, and arming during one |

`OdriveRamApply` and the snapshot capture each check the contract themselves, and are not yet a general runner. When the second
writing function arrives, the steps they share move into something all use. No framework before that.

## The contract of an operation

An operation is anything of more than one step, or anything that changes the board.

**Connection.** Everything is bound to a `ConnectGeneration`. After a disconnect, a released port or a reconnect, what
was read, reviewed or declared on the old connection counts for nothing and is never upgraded. A request of an old
connection does not reach a new one, and a late reply does not count for it.

**Stop.** The software stop goes ahead of everything, and never depends on an operation finishing. It sets the latch.
What the latch blocks stays blocked until the user clears it. An operation that began before a stop stays ended after
the latch is cleared. It is no hardware E-stop: it needs a working USB link and the motor coasts.

**Firmware capabilities.** Declared by the firmware, once per connection, in one object that display, review and
readback share. An operation rests on one state of that check. A new state ends it, also a new check without an answer yet.

**Start.** An operation begins bound to the connection, the stop state and the capability state it was prepared on. It
reads again what it rests on, and refuses if that changed.

**Abort.** Stop, connection, capability state, firmware refusal, a closing window, or a result it cannot confirm. Each
ends the operation, and the worker holds back any line still queued. A stop or state change in the instant between the
worker's last check and the line cannot call that line back. It is then named as sent.

**Partial changes.** What already went out stays and is named, value by value. There is no automatic rollback and no
automatic retry. Going back is a new operation the user starts, with its own review.

**Unclear results.** `sent, not verified` is a result of its own. It ends the operation, because the next step would
rest on something only assumed. The plugin never guesses, never fills in the expected value, and never shows an unclear
value as current.

## States of a value

The window always shows which of these a value is in. There is no single "saved" that covers them.

| State | Meaning | How it is known |
|---|---|---|
| Typed | Only in the input | Local |
| Reviewed | Checked against what was read | A review on this connection, stop state and capability state |
| Sent | The line went out | The worker, a write has no positive answer |
| In RAM, verified | The board reports that value | Readback compared by type |
| In RAM, unclear | It may or may not be there | The readback failed or was refused |
| Save reported | FFB store only: the firmware reports no counted write error | `sys.save!` answered `OK`. Some effect and filter writes are not checked, so persistence is not confirmed |
| Save sent, unconfirmed | ODrive store only: the line went out | `ss` has no answer |
| Save failed | Not saved, the store may hold a part of it | `sys.save!` answered `ERR`, with the counts in `sys.savestat?`, or the values differ after a reboot |
| Saved | In flash | Read again after a reboot, and equal to what was saved |

**Only the last state is shown as permanently saved.** A failed or unconfirmed save never confirms a change as permanent. The changed values stay
marked as in RAM, nothing is retried on its own, and after a failure what the store holds is unclear until it was read after a reboot.

**The two stores are separate**, each with its own state. The success of one says nothing about the other.

| | ODrive store | FFB store |
|---|---|---|
| Flash | S10+S11 | S1+S2 |
| Save | `ss` | `sys.save!` |
| Answer | None | `OK` or `ERR`; `sys.savestat?` counts only the writes checked by `ffb_save_flash()` |
| Refused | While a motor is armed `ss` does nothing, without an answer | With a motor active, `ERR` from S1 |
| Afterwards | A reboot, **also if the store failed**, so a reboot confirms nothing (`ODrive::save_configuration`) | No reboot |

`EffectsCalculator::saveFlash()` currently ignores its `Flash_Write` results. These effect and filter write failures can therefore leave
`sys.save!` reporting `OK` and are absent from `sys.savestat?`. A refusal by S1 leaves the counters from the previous save unchanged.

A report names both stores. Sending a save, receiving `OK` or observing a reboot does not by itself confirm persistence or clear its
uncertainty. Only a post-reboot readback that matches the intended values confirms persistence for the corresponding store.

## Who decides

**Final protection decisions belong in the firmware.** The plugin explains, refuses early and shows the reason, and it is
never the only barrier: another command source, a crash or a lost USB link must not bypass protection.

| Protection | Final decision today | Planned |
|---|---|---|
| Flash access by the running firmware with an active motor, arming during a flash operation | Firmware (S1) | – |
| Over and under voltage, over current, thermal limits, deadline misses | Firmware (ODrive) | – |
| Motor limits (`current_lim`, `torque_lim`) | Firmware | – |
| Zero torque on USB unmount | Firmware (`ffb_on_usb_unmount`) | – |
| Commands that arm or reboot while the stop latch is set | Plugin only (`OdriveCommandGuard`) | Firmware side in the protection step of package 3 |
| Writes with an active axis in the editor | Plugin only, documented as no lock | Firmware refusal for values that must not change while armed, per value, no blanket rule |
| Relations of Power / Brake values | Plugin review, the firmware stops afterwards if they are broken | Firmware refusal where a wrong value can do harm |
| Endstop priority, HID timeout, watchdog | Not yet | Package 3, protection step |
| Firmware fits the selected board | Not yet | Board identity and flasher, see `docs/HARDWARE.md` |
| Protected sectors when flashing through DFU | Not yet | The flasher of 1e excludes them itself, and is the barrier here: S1 only guards the running firmware, which is not running during DFU |

Hands-off detection is no protection. It may reduce discomfort, and nothing relies on it.

## Minimum capabilities

Older firmware is no compatibility target. A function needs these or it is not offered, with the reason shown. There
are no further heuristics to guess what a firmware can do. What the firmware does not declare, it does not have.

| Function | Needs on this connection |
|---|---|
| Read and display | A connection. Without a declaration long numbers stay marked |
| Backup and comparison | `ascii_full=1 float32_rt=1`, or the file says that values are uncertain. The catalog snapshot does the latter in its metadata, and a file without matching metadata proves no precision |
| Write to RAM, new functions | `ascii_full=1 float32_rt=1`, latch clear, axes idle, readback |
| Write to RAM, Power / Brake of PR #7 | As above, or today still its fallback for values that look exact. Decided: the fallback goes with the next extension of the writing editor, `docs/ROADMAP.md` |
| Save to flash | The above, and the firmware's flash guard |
| Calibration and anything that moves the motor | The above, the board matches, a current backup exists, explicit confirmation in the window |
| Flash firmware | Board identity on the image and on the board, protected sectors (rule 12 in `CLAUDE.md`) |

New needs become a new declared field, which means a new schema version (`docs/packages/PKG1.md`). Planned candidates are
the board identity and the flash guard.

## Calibration records

A calibration is only as good as what it was made on. Each result the plugin stores carries:

- Board, firmware build (`sys.build?`), motor label and encoder (type, `cpr`, mode).
- Pole pairs and the currents used for the measurement.
- The measured values, the date, and where they are stored.

A record that does not match the present hardware or firmware is shown as such and not used without a decision by the
user. This is plugin data. It does not touch `Config_t` (rule 3 in `CLAUDE.md`).
