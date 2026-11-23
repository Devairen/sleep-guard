using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace SleepGuard
{
    /// <summary>
    /// A handful of preferences in a plain key=value file under AppData.
    /// The chosen wake mode is deliberately NOT remembered: every launch
    /// starts in Auto, so a reboot can never leave the PC silently pinned awake.
    /// </summary>
    internal sealed class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "SleepGuard";

        public int DurationSeconds = 2 * 3600;   // 0 means no limit
        public EndAction End = EndAction.Hibernate;
        // Off since 18/11/2026: a desktop widget sits under other windows and
        // Win+D reveals it (DesktopLayer.cs). The tray menu can still pin it.
        public bool Topmost = false;
        // On since 23/11/2026 (one monitor): a chip in the taskbar opens the
        // card above it (TaskbarChip.cs). Off = the desktop widget above.
        public bool Taskbar = true;
        public double Left = double.NaN;
        public double Top = double.NaN;

        private static string Path
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SleepGuard");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(Path)) return s;
                foreach (string line in File.ReadAllLines(Path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (key == "duration") s.DurationSeconds = ParseInt(val, s.DurationSeconds);
                    else if (key == "end") s.End = ParseEnd(val);
                    else if (key == "topmost") s.Topmost = (val == "1" || val == "true");
                    else if (key == "taskbar") s.Taskbar = (val == "1" || val == "true");
                    else if (key == "left") s.Left = ParseDouble(val);
                    else if (key == "top") s.Top = ParseDouble(val);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("duration=" + DurationSeconds.ToString(CultureInfo.InvariantCulture));
                lines.Add("end=" + End.ToString().ToLowerInvariant());
                lines.Add("topmost=" + (Topmost ? "1" : "0"));
                lines.Add("taskbar=" + (Taskbar ? "1" : "0"));
                if (!double.IsNaN(Left))
                    lines.Add("left=" + Left.ToString("F0", CultureInfo.InvariantCulture));
                if (!double.IsNaN(Top))
                    lines.Add("top=" + Top.ToString("F0", CultureInfo.InvariantCulture));
                // Write beside the real file and swap it in. WriteAllLines
                // truncates first, so a crash in that window would leave an
                // empty settings.ini and lose the card's position.
                string path = Path;
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch { }
        }

        private static int ParseInt(string v, int fallback)
        {
            int n;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static double ParseDouble(string v)
        {
            double n;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : double.NaN;
        }

        private static EndAction ParseEnd(string v)
        {
            if (v == "sleep") return EndAction.Sleep;
            if (v == "release") return EndAction.Release;
            return EndAction.Hibernate;
        }

        // ---- Start with Windows ---------------------------------------

        public static bool StartsWithWindows()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(RunValue, "\"" + exe + "\"");
                    }
                    else if (k.GetValue(RunValue) != null)
                    {
                        k.DeleteValue(RunValue, false);
                    }
                }
            }
            catch { }
        }
    }
}
