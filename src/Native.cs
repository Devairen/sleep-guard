using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SleepGuard
{
    /// <summary>Which sleep behaviour the user has chosen.</summary>
    internal enum WakeMode
    {
        /// Windows decides: screen blanks and the PC sleeps on their normal timers.
        Auto = 0,
        /// The PC stays awake. The screen is still allowed to blank.
        Awake = 1,
        /// Screen and PC both stay on.
        Screen = 2
    }

    /// <summary>What happens when the chosen window of time runs out.</summary>
    internal enum EndAction
    {
        Hibernate = 0,
        Sleep = 1,
        Release = 2
    }

    internal static class Native
    {
        // ---- Wake lock -------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        private static WakeMode _current = WakeMode.Auto;

        /// <summary>
        /// Tell Windows what may sleep. This never edits the power plan: the
        /// hold lives only as long as this process, so a crash or a quit
        /// restores the normal timeouts immediately.
        /// Windows tracks the state per thread and drops it when that thread
        /// exits, so every call must come from the same long-lived thread.
        /// We always call it from the WPF UI thread.
        /// </summary>
        public static bool Apply(WakeMode mode)
        {
            _current = mode;
            return SetThreadExecutionState(FlagsFor(mode)) != 0;
        }

        private static uint FlagsFor(WakeMode mode)
        {
            uint flags = ES_CONTINUOUS;
            if (mode == WakeMode.Awake || mode == WakeMode.Screen) flags |= ES_SYSTEM_REQUIRED;
            if (mode == WakeMode.Screen) flags |= ES_DISPLAY_REQUIRED;
            return flags;
        }

        /// <summary>
        /// Re-state the current mode and hand back the flags the thread was
        /// already holding, which is how we prove the hold really registered.
        /// </summary>
        public static uint PeekState()
        {
            return SetThreadExecutionState(FlagsFor(_current));
        }

        // ---- Hibernate / sleep ----------------------------------------

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.Bool)] bool hibernate,
            [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
            [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;

        /// Hibernating or sleeping needs SeShutdownPrivilege switched on in
        /// our own token. Every standard user holds it; it just starts off.
        private static void EnableShutdownPrivilege()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(),
                        TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return;

                LUID luid;
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out luid)) return;

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        /// <summary>Drop the wake lock, then hibernate. Falls back to shutdown.exe.</summary>
        public static void Hibernate()
        {
            Apply(WakeMode.Auto);
            EnableShutdownPrivilege();
            try { if (SetSuspendState(true, true, false)) return; }
            catch { }
            RunHidden("shutdown.exe", "/h");
        }

        /// <summary>Drop the wake lock, then sleep.</summary>
        public static void Sleep()
        {
            Apply(WakeMode.Auto);
            EnableShutdownPrivilege();
            try { SetSuspendState(false, true, false); }
            catch { }
        }

        private static void RunHidden(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { }
        }

        // ---- Reading the real power plan -------------------------------

        /// <summary>
        /// Idle seconds before the PC sleeps on mains power, or -1 if unknown.
        /// 0 means never.
        /// </summary>
        public static int SleepAfterSeconds()
        {
            return QueryIdleSeconds("SUB_SLEEP", "STANDBYIDLE");
        }

        /// <summary>Idle seconds before the screen blanks on mains power, or -1.</summary>
        public static int DisplayOffAfterSeconds()
        {
            return QueryIdleSeconds("SUB_VIDEO", "VIDEOIDLE");
        }

        /// powercfg prints the AC value second-to-last and the DC value last.
        /// Reading them positionally keeps this working on a non-English Windows.
        private static int QueryIdleSeconds(string subgroup, string setting)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powercfg.exe",
                    "/query SCHEME_CURRENT " + subgroup + " " + setting);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;

                string output;
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                }

                MatchCollection m = Regex.Matches(output, "0x([0-9a-fA-F]{8})");
                if (m.Count < 2) return -1;
                return (int)Convert.ToUInt32(m[m.Count - 2].Groups[1].Value, 16);
            }
            catch { return -1; }
        }

        /// <summary>"10 min", "1 h 30 m", "never", or null when unknown.</summary>
        public static string FormatIdle(int seconds)
        {
            if (seconds < 0) return null;
            if (seconds == 0) return "never";
            int minutes = seconds / 60;
            if (minutes < 60) return minutes + " min";
            int hours = minutes / 60;
            int rest = minutes % 60;
            if (rest == 0) return hours + " h";
            return hours + " h " + rest + " m";
        }
    }
}
