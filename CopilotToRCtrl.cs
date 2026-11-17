// CopilotToRCtrl - turns the Copilot key back into Right Ctrl.
//
// The Copilot key sends the chord  LWin down, LShift down, F23 down ... F23 up, LShift up, LWin up.
// A low-level keyboard hook swallows that chord and injects Right Ctrl instead.
// Real Win / Win+Shift presses are held for at most 100 ms and then replayed unchanged.
//
// No network, no registry, no child processes. Build with build.ps1.
// Flags:  --log   write every key event and decision to %TEMP%\CopilotToRCtrl.log

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class CopilotToRCtrl
{
    // ---- Win32 -----------------------------------------------------------------
    const int WH_KEYBOARD_LL = 13;
    const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
    const uint WM_TIMER = 0x0113, WM_APP = 0x8000;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x01, KEYEVENTF_KEYUP = 0x02;

    const ushort VK_LWIN = 0x5B, VK_LSHIFT = 0xA0, VK_RCONTROL = 0xA3, VK_F23 = 0x86;
    const ushort SC_LWIN = 0x5B, SC_LSHIFT = 0x2A, SC_RCTRL = 0x1D;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    // ---- State -----------------------------------------------------------------
    enum State { Idle, GotWin, GotWinShift, Active, Releasing }

    static State state = State.Idle;
    static bool winSuppressed, shiftSuppressed;      // partial chord: held downs to replay
    static bool chordWinUpPending, chordShiftUpPending; // chord releases still to swallow
    static bool realWinDown, realShiftDown;          // user's own LWin/LShift pressed while active

    static LowLevelKeyboardProc hookProc = HookCallback; // keep delegate alive
    static IntPtr hook = IntPtr.Zero;
    static uint mainThread;
    static UIntPtr chordTimer = UIntPtr.Zero, watchdogTimer = UIntPtr.Zero;
    static readonly List<INPUT> pending = new List<INPUT>();
    static StreamWriter log;

    const uint ChordTimeoutMs = 100, ReleaseTimeoutMs = 250, WatchdogMs = 5000;

    // ---- Entry -----------------------------------------------------------------
    [STAThread]
    static int Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\CopilotToRCtrl", out created))
        {
            if (!created) return 1;

            if (Array.IndexOf(args, "--log") >= 0)
            {
                log = new StreamWriter(Path.Combine(Path.GetTempPath(), "CopilotToRCtrl.log"), true);
                log.AutoFlush = true;
                Log("---- started ----");
            }

            mainThread = GetCurrentThreadId();
            InstallHook();
            if (hook == IntPtr.Zero) return 2;
            watchdogTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, WatchdogMs, IntPtr.Zero);

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP) { FlushPending(); continue; }
                if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero)
                {
                    UIntPtr id = (UIntPtr)(ulong)msg.wParam.ToInt64();
                    if (id == watchdogTimer) { InstallHook(); continue; }
                    if (id == chordTimer) { OnChordTimeout(); continue; }
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }
        return 0;
    }

    // Windows silently drops an LL hook that ever times out; re-installing periodically
    // keeps us alive. Install the new one before removing the old so no event is missed.
    static void InstallHook()
    {
        IntPtr h = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        if (h == IntPtr.Zero) { Log("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error()); return; }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = h;
    }

    // ---- Hook ------------------------------------------------------------------
    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                State before = state;
                bool block = Process(k);
                if (log != null)
                    Log(string.Format("vk={0:X2} sc={1:X2} fl={2:X2} {3,-4} {4}->{5} {6}",
                        k.vkCode, k.scanCode, k.flags, (k.flags & LLKHF_UP) != 0 ? "up" : "down",
                        before, state, block ? "BLOCK" : "pass"));
                if (pending.Count > 0) PostThreadMessage(mainThread, WM_APP, IntPtr.Zero, IntPtr.Zero);
                if (block) return (IntPtr)1;
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // Returns true to swallow the physical event.
    static bool Process(KBDLLHOOKSTRUCT k)
    {
        bool up = (k.flags & LLKHF_UP) != 0;
        uint vk = k.vkCode;

        switch (state)
        {
            case State.Idle:
                if (vk == VK_LWIN && !up)
                {
                    winSuppressed = true; shiftSuppressed = false;
                    state = State.GotWin;
                    StartChordTimer(ChordTimeoutMs);
                    return true;
                }
                return false;

            case State.GotWin:
            case State.GotWinShift:
                if (vk == VK_LSHIFT && !up && state == State.GotWin)
                {
                    shiftSuppressed = true;
                    state = State.GotWinShift;
                    return true;
                }
                if (vk == VK_F23 && !up)
                {
                    StopChordTimer();
                    state = State.Active;
                    chordWinUpPending = winSuppressed;
                    chordShiftUpPending = shiftSuppressed;
                    winSuppressed = shiftSuppressed = false;
                    realWinDown = realShiftDown = false;
                    QueueKey(VK_RCONTROL, SC_RCTRL, true, false);
                    return true;
                }
                // Not the Copilot chord: replay what we held, then this key, in order.
                StopChordTimer();
                ReplaySuppressed();
                QueueKey((ushort)vk, (ushort)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, up);
                state = State.Idle;
                return true;

            case State.Active:
                if (vk == VK_F23)
                {
                    QueueKey(VK_RCONTROL, SC_RCTRL, true, up); // down = typematic repeat
                    if (up)
                    {
                        if (chordWinUpPending || chordShiftUpPending)
                        {
                            state = State.Releasing;
                            StartChordTimer(ReleaseTimeoutMs);
                        }
                        else state = State.Idle;
                    }
                    return true;
                }
                if (vk == VK_LWIN) return ActiveModifier(up, ref realWinDown, ref chordWinUpPending);
                if (vk == VK_LSHIFT) return ActiveModifier(up, ref realShiftDown, ref chordShiftUpPending);
                return false; // e.g. C while "Ctrl" is held -> Ctrl+C

            case State.Releasing:
                if (up && vk == VK_LWIN && chordWinUpPending) { chordWinUpPending = false; FinishRelease(); return true; }
                if (up && vk == VK_LSHIFT && chordShiftUpPending) { chordShiftUpPending = false; FinishRelease(); return true; }
                // Something else: stop waiting and treat it fresh.
                StopChordTimer();
                state = State.Idle;
                return Process(k);
        }
        return false;
    }

    // While the chord is held, the user may press their real LWin/LShift (Ctrl+Shift+Esc,
    // Ctrl+Win+Arrow). Those pass through; an up we did not see a down for is the chord's.
    static bool ActiveModifier(bool up, ref bool realDown, ref bool chordUpPending)
    {
        if (!up) { realDown = true; return false; }
        if (realDown) { realDown = false; return false; }
        if (chordUpPending) { chordUpPending = false; return true; }
        return false;
    }

    static void FinishRelease()
    {
        if (!chordWinUpPending && !chordShiftUpPending) { StopChordTimer(); state = State.Idle; }
    }

    static void OnChordTimeout()
    {
        StopChordTimer();
        if (state == State.GotWin || state == State.GotWinShift)
        {
            // Real Win (or Win+Shift) held longer than the chord takes: hand it back.
            ReplaySuppressed();
            state = State.Idle;
            FlushPending();
            Log("timeout: replayed held keys");
        }
        else if (state == State.Releasing)
        {
            chordWinUpPending = chordShiftUpPending = false;
            state = State.Idle;
            Log("timeout: release wait ended");
        }
    }

    static void ReplaySuppressed()
    {
        if (winSuppressed) QueueKey(VK_LWIN, SC_LWIN, true, false);
        if (shiftSuppressed) QueueKey(VK_LSHIFT, SC_LSHIFT, false, false);
        winSuppressed = shiftSuppressed = false;
    }

    static void StartChordTimer(uint ms)
    {
        StopChordTimer();
        chordTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, ms, IntPtr.Zero);
    }

    static void StopChordTimer()
    {
        if (chordTimer != UIntPtr.Zero) { KillTimer(IntPtr.Zero, chordTimer); chordTimer = UIntPtr.Zero; }
    }

    // ---- Injection (done from the message loop, never inside the hook) --------------
    static void QueueKey(ushort vk, ushort scan, bool extended, bool up)
    {
        var i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = scan;
        i.u.ki.dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        pending.Add(i);
    }

    static void FlushPending()
    {
        if (pending.Count == 0) return;
        INPUT[] batch = pending.ToArray();
        pending.Clear();
        uint sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf(typeof(INPUT)));
        if (sent != batch.Length) Log("SendInput sent " + sent + "/" + batch.Length + " err=" + Marshal.GetLastWin32Error());
    }

    static void Log(string s)
    {
        if (log != null) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
    }
}
