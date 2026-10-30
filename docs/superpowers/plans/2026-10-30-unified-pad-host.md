# Unified Pad Host Implementation Plan

> **For agentic workers:** Use subagent-driven development for the independent state, status and lifecycle modules; the primary agent integrates and deploys.

**Goal:** One Windows process handles local Alt/Ctrl remapping and Xiaomi Pad keyboard bridging correctly.

**Architecture:** A single keyboard worker and ownership state machine decode bridge input before applying modifier policy. WinForms owns settings, tray and status service lifecycle; UDP runs on a separate thread.

**Tech Stack:** .NET Framework 4.x, C#, WinForms, Win32 low-level hook and SendInput, UDP.

**Spec:** `docs/superpowers/specs/2026-10-30-unified-pad-host.md`

## Global Constraints

- Existing Android apps and protocol V2 must remain compatible.
- One hook, one tray and one active startup task after migration.
- Preserve user files and a rollback snapshot; no network/display configuration changes.

## Review Focus

- A key press must not change from native to bridged halfway through an autorepeat sequence.
- Stopping or faulting releases every owned key, including extended Win and Alt keys.
- Turning swapping off must leave the bridge functional.
- UDP bind failure cannot remove the keyboard hook; hook failure cannot advertise readiness.
- An old Host restart must not install a second bridge.

## Tasks

- [x] State and tests: extend `KeyboardState`, `ScanEvent.Extended`, independent swap/bridge policies; run `tests/test-keyboard.ps1` red then green.
- [x] Status and tests: implement `BridgeStatusServer.Start/Stop/Dispose` with exact V2 protocol and loopback tests, including exclusive bind failure and restart.
- [x] Startup and tests: add bridge CLI/preferences and `LegacyBridgeGuard.TryAcquire/Dispose`; test distinct mutex instances and argument ordering.
- [x] Integrate `KeyboardHook` and `MainForm`: extended input serialization, independent toggles, no external Host polling, one readiness/fault lifecycle, graceful `--quit`.
- [x] Run full `test.ps1`, build staged binary, review combined behavior and perform focused desktop verification where needed.
- [x] Prepare migration snapshot; stop validated old instances; install merged executable; verify process, mutex, UDP, task and real Pad input; roll back on failure.
- [ ] Update both repositories' documentation, commit and push tested work, verify CI/releases.
