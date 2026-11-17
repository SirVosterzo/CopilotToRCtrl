# CopilotToRCtrl

Make the Copilot key on Windows laptops work as Right Ctrl again.

A lot of recent laptops, my ASUS ROG Zephyrus G14 included, swapped Right Ctrl for a Copilot key. Windows lets you
point that key at an app, but it won't let you turn it into a modifier. With CopilotToRCtrl running, holding the
Copilot key is the same as holding Right Ctrl, so Ctrl+C, Ctrl+Click and Ctrl+Shift+Esc all work, and it repeats
when held.

It's one C# file of about 250 lines, short enough to read before you run it. It compiles with the C# compiler that
ships with Windows, so there's nothing to install first. It doesn't touch the network or the registry, doesn't
collect anything, and doesn't start other programs. It runs elevated at logon, which is what lets it work in admin
windows too.

## Why not a registry Scancode Map or PowerToys?

The Copilot key doesn't send one key. It sends a chord:

```
press:    LWin ↓   LShift ↓   F23 ↓
release:  F23 ↑    LShift ↑   LWin ↑
```

If you remap F23 with a Scancode Map, Win and Shift are still held, so you get Win+Shift+Ctrl instead of Ctrl.
Shortcut remappers like PowerToys usually can't keep the result held down as a modifier either.

## How it works

CopilotToRCtrl installs a low-level keyboard hook (`WH_KEYBOARD_LL`) and watches for that chord:

1. When LWin goes down, it holds the key back for up to 100 ms.
2. If LShift and then F23 follow, it swallows all three and sends Right Ctrl down instead.
3. When F23 comes back up, it sends Right Ctrl up and swallows the LShift and LWin releases that follow.
4. If something else arrives first (you pressed the real Win key, or Win+E, or Win+Shift+S), it replays the held
   keys in their original order and your shortcuts work as usual.

Your real Win and Shift keys still work while you hold the Copilot key, so Ctrl+Shift+Esc and Ctrl+Win+← are fine.

Ctrl is sent from the message loop rather than from inside the hook callback. That keeps the callback fast enough
that Windows doesn't time it out. Windows quietly removes any low-level hook that has timed out even once, though,
so the program re-installs its hook every 5 seconds to be safe.

## Install

You need 64-bit Windows 10 or 11. The .NET Framework 4.x it runs on is already part of Windows.

### Option A: build from source (recommended)

```powershell
git clone https://github.com/SirVosterzo/CopilotToRCtrl.git
cd CopilotToRCtrl
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

### Option B: download a release

Download the zip from [Releases](../../releases) and compare its SHA-256 with the one in the release notes. Then
extract it and run `install.ps1`. GitHub Actions builds each release straight from the tagged source. The workflow
is in [`.github/workflows/build.yml`](.github/workflows/build.yml) if you want to check how.

### What `install.ps1` does

It asks for admin rights once, through UAC. Then it copies `CopilotToRCtrl.exe` to
`C:\Program Files\CopilotToRCtrl\`. Only admins can write there, so another program can't swap in its own exe and
get it run elevated at your next logon.

Next it registers a scheduled task called `CopilotToRCtrl`. The task starts at your logon with highest privileges,
has no time limit, keeps running on battery and restarts if it crashes. The script starts it right away, so you
don't have to log out.

It needs to run elevated to work inside admin windows like an admin terminal or Task Manager. Windows doesn't let
an unelevated keyboard hook affect those.

After a rebuild, run `install.ps1` again to update.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

This deletes the scheduled task and `C:\Program Files\CopilotToRCtrl\`. The program doesn't create anything else,
so that's all of it.

## Troubleshooting

Only one copy can run at a time, so stop the installed one before starting it with logging:

```powershell
Stop-Process -Name CopilotToRCtrl
.\CopilotToRCtrl.exe --log
```

It writes every key event, and what it did with it, to `%TEMP%\CopilotToRCtrl.log`. One press of the Copilot key
should look like this:

```
vk=5B sc=5B fl=01 down Idle->GotWin BLOCK
vk=A0 sc=2A fl=00 down GotWin->GotWinShift BLOCK
vk=86 sc=6E fl=00 down GotWinShift->Active BLOCK
vk=86 sc=6E fl=80 up   Active->Releasing BLOCK
vk=A0 sc=2A fl=80 up   Releasing->Releasing BLOCK
vk=5B sc=5B fl=81 up   Releasing->Idle BLOCK
```

If your keyboard sends something different, open an issue with the log and your laptop model. When you're done,
stop the logging copy and start the normal one again:

```powershell
Stop-Process -Name CopilotToRCtrl
Start-ScheduledTask CopilotToRCtrl
```

## Known limitations

- Copilot+Alt+Del won't open the security screen. Windows deliberately ignores Ctrl+Alt+Del when software sends it.
- It doesn't work on the secure desktop, meaning UAC prompts and the lock screen.
- A real Win key press can wait up to 100 ms before it gets through. I haven't noticed it.
- I've only tested it on an ASUS ROG Zephyrus G14. Other laptops whose Copilot key sends the same chord should work.

## Credits

The idea comes from [Dwedit/NoCopilotKey](https://github.com/Dwedit/NoCopilotKey), which does the same job with a
keyboard hook. CopilotToRCtrl is a separate, much smaller program and doesn't share any of its code.

## License

[MIT](LICENSE)
