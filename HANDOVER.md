# Entanglo Windows — Handover

## Published state

**v0.1.61** — commit `main` (before this branch). Released on the website.

## Branch: sync/linux-0.1.16

Closes the gap with **Linux 0.1.16** and **macOS 0.1.61**.
All changes are on branch `sync/linux-0.1.16`; not yet merged to `main` or released.

### What changed

| Item | Change |
|---|---|
| **2.1 Bonjour TXT id** | `DiscoveryService` advertises `id=<UUID>` in mDNS TXT record. Parses TXT id from peers; re-emits `DiscoveredPeer` when id arrives. `DiscoveredPeer` gains `DeviceId?`. Auto-dial prefers UUID match. Trust migration: name-keyed → UUID on first Hello. |
| **2.2 Hello screen size** | `HelloPayload` gains optional `ScreenWidth/ScreenHeight`. Primary screen dimensions included in sent Hello (physical pixels, PerMonitorV2 DPI aware). Peer screen size stored in `PeerSession` and `ActivePeer`. |
| **2.3 Receiver mouse** | `InjectMouseMove` handles `deltaX/deltaY`-only events with fractional remainder accumulation. Fixed absolute mapping: removed `MOUSEEVENTF_VIRTUALDESK`; uses `(v * 65535) / (size-1)` with clamp. |
| **2.4 Media keys receiver** | `InjectMediaKey` maps protocol `mediaKey` enum to `VK_VOLUME_*` / `VK_MEDIA_*` virtual keys. Brightness/eject logged once and ignored. |
| **2.5 Controller** | `InputCaptureService` creates hidden HWND_MESSAGE window; registers Raw Input with RIDEV_INPUTSINK; handles WM_INPUT; fires `RawMouseCaptured` with raw deltaX/deltaY. `EnterCapture()` clips cursor to 1×1 centre rect and hides cursor. Media keys forwarded as `mediaKey` events. `EdgeSwitcherService` tracks virtual cursor in peer pixel space; calculates entry position at matching edge; detects return across entry edge and restores local cursor. Applies `PointerSpeed` multiplier (default 1.0, clamped [0.25, 4.0]). `AppSettings.PointerSpeed` added. |
| **2.6 File transfer** | `fileAck` sent on the same transport that delivered chunks (not by senderDeviceId). 15-second ack wait after all chunks sent; falls back to "Sent (receipt not confirmed)" on timeout. `OutgoingFileTerminal` fires at most once per transfer. |
| **2.8 Key mapping tests** | Digit row 5/6/7, full numpad, numpad Enter, all modifier bits and VK codes. 93 tests total, all passing. |

### Verified by tests only (not yet live-tested)

- Relative mouse injection math
- Absolute coordinate mapping
- Media key wire format
- Pointer speed clamping
- Hello round-trip with/without screen size
- Key mapping regression suite
- File transfer ack routing (unit logic only — large-file live test not yet run)

### Waiting for user live tests (Step 3)

Before release, test against Linux 0.1.16 on iMac1/iMac2:
1. Linux → Windows: mouse (relative + absolute), keyboard, keypad, media keys, clipboard, edge return
2. Windows → Linux: same + confinement (local cursor must not move), emergency stop (Escape)
3. Files both directions: small files + ≥3 GB file (sender must show Sent, not Failed)
4. Devices page: one row per device after upgrading existing install with saved trust
5. Ethernet vs Wi-Fi path failover (if applicable)

---

## Open issues (priority order)

1. **2.7 Network path selection** — not implemented. Ethernet-over-WiFi path ranking, per-interface discovery caching, path upgrade without dropping in-flight transfers. Needs `NetworkInterface.NetworkInterfaceType` ranking.
2. **Pointer speed UI** — `AppSettings.PointerSpeed` exists and is persisted, but there is no settings page control yet. Users must edit `settings.json` manually.
3. **Per-device pointer speed** — SYNC-PROMPT asks for per-device; current implementation is global. Architecture note: would need a dictionary keyed by DeviceId in AppSettings.
4. **Brightness key injection** — `brightnessUp`/`brightnessDown` have no Win32 VK. WMI `WmiMonitorBrightnessMethods` path not yet implemented; events are logged and ignored.
5. **Large-file heartbeat timeout** — added `await Task.Delay(5)` between chunks. Not live-tested with files >1 GB yet.

---

## Pitfalls

- **DPI**: `app.manifest` already sets `PerMonitorV2`. `GetSystemMetrics(SM_CXSCREEN/CYSCREEN)` returns physical pixels. If this ever changes, the Hello screen size and injection math will break.
- **UIPI**: Low-level hooks and `SendInput` may fail to inject into elevated windows (UAC). The app runs at normal integrity level. If a game or admin tool is in the foreground, injection is silently dropped.
- **Hook timeout**: Windows kills LL hooks after ~5 s if the message pump stalls. The dedicated hook thread + `AboveNormal` priority mitigates this but does not eliminate it on heavily loaded machines.
- **ClipCursor interaction**: `ClipCursor` confines the cursor process-wide. `EnterCapture`/`ExitCapture` must be balanced. `InputCaptureService.Dispose` always calls `ExitCapture` as a safety net.
- **Raw Input window**: The hidden HWND_MESSAGE window uses the "STATIC" class. If `CreateWindowExW` fails (unusual sandbox), Raw Input falls back gracefully with a warning log; relative motion is unavailable but absolute and keyboard input still work.
- **ShowCursor balance**: `ShowCursor` modifies a per-process internal counter. `_cursorHidden` bool ensures we always call `ShowCursor(true)` exactly once for each `ShowCursor(false)`.

## Rollback

To roll back to v0.1.61:
```
git checkout main
```
No database or trust-store migrations are destructive. If a trust.json.enc was written by this branch, it remains readable by 0.1.61 (UUID keys were always used on Windows).

## Release (Step 4)

After user confirms live tests:
1. Bump `<Version>` to `0.1.62` in `Entanglo.Windows.csproj` and `Installer/Entanglo.iss`
2. Build installer: `iscc Installer/Entanglo.iss`
3. Create GitHub release `v0.1.62` with installer asset
4. Update `public/updates/latest-win.json` and `src/data/release-notes.json` in `entanglo-website`
5. Ask user for explicit approval before pushing the release or the website
