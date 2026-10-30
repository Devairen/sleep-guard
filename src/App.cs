using System;
using System.Globalization;
using System.Threading;
using System.Windows;

namespace SleepGuard
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] argv)
        {
            if (argv != null && argv.Length > 0 && argv[0] == "--selftest")
                return SelfTest();

            if (argv != null && argv.Length > 0 && argv[0] == "--geotest")
                return GeometryTest();

            // It starts with Windows, so a second copy must be impossible:
            // two processes would each hold their own wake lock.
            bool fresh;
            using (Mutex single = new Mutex(true, "SleepGuard.SingleInstance.Joris", out fresh))
            {
                if (!fresh) return 0;

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                MainWindow window = new MainWindow();
                window.Show();

                return app.Run();
            }
        }

        /// <summary>
        /// Opens the card at its saved spot, arms it from code, and writes the
        /// window's position before and after. The two tops must match: that
        /// is the whole point of anchoring the top edge.
        /// Result goes to %TEMP%\sleepguard-geotest.txt.
        /// </summary>
        private static int GeometryTest()
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindow window = new MainWindow();
            System.Text.StringBuilder log = new System.Text.StringBuilder();

            window.Loaded += delegate
            {
                log.AppendLine(window.GeometryLine("collapsed"));

                Step(500, delegate
                {
                    window.ArmForTest();
                    Step(900, delegate
                    {
                        log.AppendLine(window.GeometryLine("armed   "));
                        System.IO.File.WriteAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                "sleepguard-geotest.txt"), log.ToString());
                        Native.Apply(WakeMode.Auto);
                        app.Shutdown();
                    });
                });
            };

            window.Show();
            return app.Run();
        }

        /// Run an action after a delay without blocking the dispatcher, so
        /// the height animation and the layout pass actually happen.
        private static void Step(int ms, Action action)
        {
            System.Windows.Threading.DispatcherTimer t =
                new System.Windows.Threading.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate
            {
                t.Stop();
                action();
            };
            t.Start();
        }

        /// <summary>
        /// Proves the wake lock really registers. SetThreadExecutionState
        /// returns the flags the calling thread held BEFORE the call, so a
        /// second call reports back whatever the first one stored.
        /// Prints to stdout and exits; no window, no lock left behind.
        /// </summary>
        private static int SelfTest()
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            log.AppendLine("mode=Auto    stored=" + Probe(WakeMode.Auto));
            log.AppendLine("mode=Awake   stored=" + Probe(WakeMode.Awake));
            log.AppendLine("mode=Screen  stored=" + Probe(WakeMode.Screen));
            log.AppendLine("released     stored=" + Probe(WakeMode.Auto));

            int display = Native.DisplayOffAfterSeconds();
            int sleep = Native.SleepAfterSeconds();
            log.AppendLine("plan: display off after " + display + "s (" +
                Native.FormatIdle(display) + "), sleep after " + sleep + "s (" +
                Native.FormatIdle(sleep) + ")");

            // A windowed app has no console, so the result goes to a file.
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sleepguard-selftest.txt");
            System.IO.File.WriteAllText(path, log.ToString());
            return 0;
        }

        private static string Probe(WakeMode mode)
        {
            Native.Apply(mode);
            uint prev = Native.PeekState();   // re-apply, read back what was held
            return Describe(prev);
        }

        private static string Describe(uint flags)
        {
            if (flags == 0) return "call failed";
            string s = "0x" + flags.ToString("X8", CultureInfo.InvariantCulture);
            if ((flags & 0x80000000u) != 0) s += " CONTINUOUS";
            if ((flags & 0x00000001u) != 0) s += " SYSTEM_REQUIRED";
            if ((flags & 0x00000002u) != 0) s += " DISPLAY_REQUIRED";
            return s;
        }
    }
}
