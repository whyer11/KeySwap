# Unified Windows keyboard host

The user requested a complete fix for local and Xiaomi Pad remote input, then asked to merge KeySwap and WorkBuddy. Use KeySwap as the Windows host with one hook, tray and startup task. Keep the existing Android WorkBuddy and Moonlight applications unchanged.

- Independent persistent switches: left Alt/Ctrl swapping, native Alt+Tab preservation, Pad keyboard bridging.
- Bridge translates injected Alt+F12 to logical Alt+Tab and injected Scroll Lock to left Windows down/up. Decode transport before applying the swap policy. Local F12 and Scroll Lock remain ordinary input.
- Bridge works while swapping is disabled. With swapping enabled and preservation disabled, physical Alt+Tab becomes Ctrl+Tab, including the encoded Pad path.
- One state machine owns emitted modifiers and paired releases. Cover repeats, quick release, partial SendInput failure, initial held keys, and changes during a press. Preserve boot-scoped hardware calibration.
- Existing Pad UDP protocol V2 remains exact: IPv4 port 57420, nonce echo, host version token, protocol=2, core=ready, rules=alt_tab,windows_key. Network failures must not stop input; no control commands or input logging in this service.
- Normal startup must detect legacy Host conflicts. Migration backs up current binaries/settings/tasks, retires only the validated legacy Host, keeps its installation for rollback, and leaves one active login task. Reserve its legacy mutex while the integrated bridge runs.
- Keep the existing Tailscale-scoped UDP firewall rule. Do not change Android, Moonlight, Sunshine, display or network configuration.
- Validate automated state/protocol/lifecycle tests and the user's real Pad keyboard before publishing the finished changes to the relevant repositories.
