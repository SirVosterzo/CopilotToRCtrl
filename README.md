# CopilotToRCtrl

Turn the **Copilot key** on Windows laptops back into a **Right Ctrl** key.

Many recent laptops (e.g. ASUS ROG Zephyrus G14) replaced Right Ctrl with a Copilot key. Windows only lets you
point that key at an app — it can't make it behave like a modifier. CopilotToRCtrl fixes that: hold the Copilot
key and it acts exactly like Right Ctrl (Ctrl+C, Ctrl+Click, Ctrl+Shift+Esc, auto-repeat, …).

- Tiny, single-file C# program (~250 lines) you can read in a few minutes
- Built with the C# compiler that ships with Windows — no SDK, no downloads, no dependencies
- No network access, no registry changes, no telemetry, no child processes
- Runs at logon with elevated rights so it also works in admin windows

## Why not a registry Scancode Map / PowerToys?

The Copilot key doesn't send a single key. It sends a chord:

```
press:    LWin ↓   LShift ↓   F23 ↓
release:  F23 ↑    LShift ↑   LWin ↑
```

Remapping F23 at the scancode level still leaves Win+Shift held, so you get Win+Shift+Ctrl instead of Ctrl.
Shortcut-remapping tools generally can't hold the result as a modifier either.

## How it works

A low-level keyboard hook (`WH_KEYBOARD_LL`) watches for that chord:

1. **LWin ↓** is held back for up to 100 ms.
2. If **LShift ↓** and then **F23 ↓** follow, the chord is swallowed and **Right Ctrl ↓** is injected.
3. **F23 ↑** injects **Right Ctrl ↑**; the trailing LShift ↑ / LWin ↑ are swallowed.
4. If anything else happens (you pressed the real Win key, Win+E, Win+Shift+S, …), the held keys are replayed
   in their original order, so normal Windows shortcuts keep working.

Extra details:

- Your real Win/Shift keys still work while the Copilot key is held (Ctrl+Shift+Esc, Ctrl+Win+←).
- Input is injected from the message loop, never inside the hook callback, to avoid hook timeouts.
- The hook is re-installed every 5 seconds, because Windows silently drops low-level hooks that ever time out.

## Install

Requirements: Windows 10/11 x64 (the .NET Framework 4.x it uses is built into Windows).

### Option A — build from source (recommended)

```powershell
git clone https://github.com/<you>/CopilotToRCtrl.git
cd CopilotToRCtrl
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

### Option B — release download

Download the zip from [Releases](../../releases), check its SHA-256 against the value in the release notes,
extract it, and run `install.ps1`. Release binaries are built by GitHub Actions directly from the tagged
source — see [`.github/workflows/build.yml`](.github/workflows/build.yml).

### What `install.ps1` does

It asks for admin once (UAC), then:

- copies `CopilotToRCtrl.exe` to `C:\Program Files\CopilotToRCtrl\` (admin-only writable, so the elevated task
  can't be hijacked by swapping the exe);
- registers a scheduled task **CopilotToRCtrl** that runs at your logon with highest privileges, keeps running on
  battery, has no time limit and restarts on failure;
- starts it right away.

Running elevated is what makes the key work inside elevated windows (admin terminals, Task Manager, …); Windows
blocks unelevated input hooks from affecting those.

Re-run `install.ps1` after rebuilding to update.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Removes the scheduled task and `C:\Program Files\CopilotToRCtrl\`. Nothing else is left behind.

## Troubleshooting

Stop the installed copy (only one instance can run), then run it with logging:

```powershell
Stop-Process -Name CopilotToRCtrl
.\CopilotToRCtrl.exe --log
```

Every key event and decision is appended to `%TEMP%\CopilotToRCtrl.log`. Pressing the Copilot key should log:

```
vk=5B sc=5B fl=01 down Idle->GotWin BLOCK
vk=A0 sc=2A fl=00 down GotWin->GotWinShift BLOCK
vk=86 sc=6E fl=00 down GotWinShift->Active BLOCK
vk=86 sc=6E fl=80 up   Active->Releasing BLOCK
vk=A0 sc=2A fl=80 up   Releasing->Releasing BLOCK
vk=5B sc=5B fl=81 up   Releasing->Idle BLOCK
```

If your keyboard sends something different, please open an issue with your log and laptop model.
Afterwards, start the normal instance again with `Start-ScheduledTask CopilotToRCtrl`.

## Known limitations

- **Copilot+Alt+Del** won't open the security screen — Windows ignores injected Ctrl+Alt+Del by design.
- Doesn't act on the secure desktop (UAC prompts, lock screen).
- Real Win key presses are delayed by up to 100 ms before being passed through (not noticeable in practice).
- Tested on an ASUS ROG Zephyrus G14. Other laptops that use the standard Copilot chord should work too.

## Credits

Inspired by [Dwedit/NoCopilotKey](https://github.com/Dwedit/NoCopilotKey), which pioneered the hook-based approach.
CopilotToRCtrl is an independent, minimal reimplementation.

## License

[MIT](LICENSE)
