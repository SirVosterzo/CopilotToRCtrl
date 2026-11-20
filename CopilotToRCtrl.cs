// CopilotToRCtrl - turns the Copilot key back into Right Ctrl.
//
// The Copilot key sends the chord  LWin down, LShift down, F23 down ... F23 up, LShift up, LWin up.
// A low-level keyboard hook swallows that chord and injects Right Ctrl instead.
// Real Win / Win+Shift presses are held for at most 100 ms and then replayed unchanged.
//
// One exe, two jobs. Build with build.ps1.
//   (no args)     setup: install, update or uninstall, with a dialog (asks for admin once)
//   --install     install without asking          --uninstall   uninstall without asking
//   --quiet       with --install/--uninstall: no dialogs, result in the exit code
//   --run         the remapper itself (what the scheduled task starts)
//   --log         run the remapper and write every key event to %TEMP%\CopilotToRCtrl.log
//
// The remapper: no network, no registry, no child processes.
// Setup: copies the exe to Program Files, registers a logon task and an entry in Settings > Apps.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("CopilotToRCtrl")]
[assembly: AssemblyDescription("Makes the Copilot key work as Right Ctrl")]
[assembly: AssemblyProduct("CopilotToRCtrl")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Claudio Lavacca. MIT License.")]
[assembly: AssemblyVersion(CopilotToRCtrl.Version + ".0")]
[assembly: AssemblyFileVersion(CopilotToRCtrl.Version + ".0")]
[assembly: AssemblyInformationalVersion(CopilotToRCtrl.Version)] // ProductVersion; release tag must be v + this

static class CopilotToRCtrl
{
    public const string Version = "1.1.0";

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
        var a = new List<string>(args);
        bool withLog = a.Contains("--log");
        if (a.Contains("--run") || withLog) return RunRemapper(withLog);
        return Setup.Run(a);
    }

    static int RunRemapper(bool withLog)
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\CopilotToRCtrl", out created))
        {
            if (!created) return 1;

            if (withLog)
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

// ---- Setup: install / update / uninstall --------------------------------------------
static class Setup
{
    const string Name = "CopilotToRCtrl";
    const string RepoUrl = "https://github.com/SirVosterzo/CopilotToRCtrl";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;

    static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    static readonly string InstalledExe = Path.Combine(InstallDir, Name + ".exe");
    static readonly string SelfExe = Assembly.GetExecutingAssembly().Location;

    static bool quiet;
    static int parentPid; // the unelevated copy that launched us; it waits for us, so don't kill it

    const uint MB_YESNOCANCEL = 0x3, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20,
               MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000;
    const int IDYES = 6, IDNO = 7, ERROR_CANCELLED = 1223;
    const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;
    const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string from, string to, uint flags);

    const string InstalledMsg =
        "CopilotToRCtrl is installed and running.\n\n" +
        "The Copilot key now works as Right Ctrl, and it starts by itself every time you sign in.\n\n" +
        "To remove it, go to Settings > Apps > Installed apps, or open this file again.";

    public static int Run(List<string> a)
    {
        SetProcessDPIAware();
        quiet = a.Contains("--quiet");
        string mode = a.Contains("--install") ? "--install" : a.Contains("--uninstall") ? "--uninstall" : null;
        string user = ArgValue(a, "--user");
        int.TryParse(ArgValue(a, "--parent"), out parentPid);

        try
        {
            if (!IsAdmin()) return Elevate(mode);
            if (user == null) user = WindowsIdentity.GetCurrent().Name;

            if (mode == "--install") { Install(user); Say(InstalledMsg); return 0; }
            if (mode == "--uninstall") { Say(UninstalledMsg(Uninstall())); return 0; }

            if (!IsInstalled())
            {
                if (Ask("Install CopilotToRCtrl?\n\n" +
                        "The Copilot key will work as Right Ctrl, starting now and every time you sign in.",
                        MB_YESNO) != IDYES) return 0;
                Install(user);
                Say(InstalledMsg);
            }
            else
            {
                int r = Ask("CopilotToRCtrl is already installed.\n\n" +
                            "Yes:  update or reinstall it\nNo:  uninstall it\nCancel:  leave it as it is",
                            MB_YESNOCANCEL);
                if (r == IDYES) { Install(user); Say(InstalledMsg); }
                else if (r == IDNO) Say(UninstalledMsg(Uninstall()));
            }
            return 0;
        }
        catch (Exception e)
        {
            if (!quiet)
                MessageBox(IntPtr.Zero, "Something went wrong:\n\n" + e.Message, Name, MB_ICONERROR | MB_SETFOREGROUND);
            return 1;
        }
    }

    // ---- Install / uninstall ------------------------------------------------------
    static void Install(string user)
    {
        dynamic folder = TaskFolder();
        StopRunning(folder);

        Directory.CreateDirectory(InstallDir); // inherits Program Files ACL: only admins can write
        if (!SamePath(SelfExe, InstalledExe)) Retry(() => File.Copy(SelfExe, InstalledExe, true));

        folder.RegisterTask(Name, TaskXml(user, InstalledExe), TASK_CREATE_OR_UPDATE, user, null,
                            TASK_LOGON_INTERACTIVE_TOKEN, null);
        WriteUninstallEntry();
        folder.GetTask(Name).Run(null);
    }

    // Returns true when the folder can only be fully removed at the next restart.
    static bool Uninstall()
    {
        dynamic folder = TaskFolder();
        StopRunning(folder);
        if (GetTask(folder) != null) folder.DeleteTask(Name, 0);
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            hklm.DeleteSubKeyTree(UninstallKey, false);
        return RemoveInstallDir();
    }

    static bool RemoveInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return false;
        if (IsUnder(SelfExe, InstallDir))
        {
            // We are the installed copy (uninstall from Settings > Apps). A running exe can't be
            // deleted, but it can be moved: park it in the Windows temp folder, delete it at restart.
            string parked = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
                                         Name + "-" + Guid.NewGuid().ToString("N") + ".exe");
            if (MoveFileEx(SelfExe, parked, 0))
                MoveFileEx(parked, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            else
            {
                foreach (string f in Directory.GetFiles(InstallDir))
                    if (!SamePath(f, SelfExe)) File.Delete(f);
                MoveFileEx(SelfExe, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                MoveFileEx(InstallDir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                return true;
            }
        }
        Retry(() => Directory.Delete(InstallDir, true));
        return false;
    }

    static void StopRunning(dynamic folder)
    {
        dynamic task = GetTask(folder);
        if (task != null) { try { task.Stop(0); } catch (Exception) { } } // not running is fine

        int me = Process.GetCurrentProcess().Id;
        foreach (Process p in Process.GetProcessesByName(Name))
            using (p)
            {
                if (p.Id == me || p.Id == parentPid) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { } // already exited
            }
    }

    static void WriteUninstallEntry()
    {
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var k = hklm.CreateSubKey(UninstallKey))
        {
            string exe = "\"" + InstalledExe + "\"";
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayVersion", CopilotToRCtrl.Version);
            k.SetValue("Publisher", "Claudio Lavacca");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("UninstallString", exe + " --uninstall");
            k.SetValue("QuietUninstallString", exe + " --uninstall --quiet");
            k.SetValue("URLInfoAbout", RepoUrl);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
        }
    }

    // ---- Task Scheduler (COM, late-bound) -------------------------------------------
    static dynamic TaskFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        return service.GetFolder("\\");
    }

    static dynamic GetTask(dynamic folder)
    {
        try { return folder.GetTask(Name); }
        catch (FileNotFoundException) { return null; }
    }

    static bool IsInstalled() { return File.Exists(InstalledExe) || GetTask(TaskFolder()) != null; }

    // At logon, elevated, no time limit, keeps running on battery, restarts if it fails.
    static string TaskXml(string user, string exe)
    {
        string u = SecurityElement.Escape(user), e = SecurityElement.Escape(exe);
        return
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
"  <RegistrationInfo><Description>Makes the Copilot key work as Right Ctrl.</Description></RegistrationInfo>" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + u + "</UserId></LogonTrigger></Triggers>" +
"  <Principals><Principal id=\"Author\"><UserId>" + u + "</UserId>" +
"    <LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
"  <Settings>" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
"    <AllowHardTerminate>true</AllowHardTerminate>" +
"    <StartWhenAvailable>false</StartWhenAvailable>" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>" +
"    <Enabled>true</Enabled>" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
"    <Priority>4</Priority>" +
"    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>" +
"  </Settings>" +
"  <Actions Context=\"Author\"><Exec><Command>" + e + "</Command><Arguments>--run</Arguments></Exec></Actions>" +
"</Task>";
    }

    // ---- Helpers ----------------------------------------------------------------------
    static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Relaunch elevated (one UAC prompt), telling the copy who the real user is, and wait for it.
    static int Elevate(string mode)
    {
        var args = new List<string>();
        if (mode != null) args.Add(mode);
        if (quiet) args.Add("--quiet");
        args.Add("--user \"" + WindowsIdentity.GetCurrent().Name + "\"");
        args.Add("--parent " + Process.GetCurrentProcess().Id);
        var psi = new ProcessStartInfo(SelfExe, string.Join(" ", args));
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        try
        {
            using (Process p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            if (e.NativeErrorCode == ERROR_CANCELLED) return ERROR_CANCELLED; // UAC declined
            throw;
        }
    }

    static string UninstalledMsg(bool needsRestart)
    {
        return "CopilotToRCtrl is uninstalled. The Copilot key is back to normal." +
               (needsRestart ? "\n\nIts folder in Program Files will be deleted the next time you restart." : "");
    }

    static int Ask(string text, uint buttons)
    {
        return MessageBox(IntPtr.Zero, text, Name, buttons | MB_ICONQUESTION | MB_SETFOREGROUND);
    }

    static void Say(string text)
    {
        if (!quiet) MessageBox(IntPtr.Zero, text, Name, MB_ICONINFORMATION | MB_SETFOREGROUND);
    }

    static string ArgValue(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    }

    static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    static bool IsUnder(string file, string dir)
    {
        return Path.GetFullPath(file).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\",
                                                 StringComparison.OrdinalIgnoreCase);
    }

    // A process we just killed can hold its exe for a moment.
    static void Retry(Action act)
    {
        for (int i = 0; ; i++)
        {
            try { act(); return; }
            catch (IOException) { if (i >= 9) throw; }
            catch (UnauthorizedAccessException) { if (i >= 9) throw; }
            Thread.Sleep(300);
        }
    }
}
