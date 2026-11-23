using System;
using System.Globalization;
using System.Threading;
using System.Windows;

namespace SleepGuard
{
    internal static class Program
    {
        /// True during --geotest/--statetest: they measure a visible card, so taskbar mode stays off.
        internal static bool TestRun;

        [STAThread]
        public static int Main(string[] argv)
        {
            if (argv != null && argv.Length > 0 && argv[0] == "--selftest")
                return SelfTest();

            if (argv != null && argv.Length > 0 && argv[0] == "--geotest")
            {
                TestRun = true;
                return GeometryTest();
            }

            if (argv != null && argv.Length > 0 && argv[0] == "--statetest")
            {
                TestRun = true;
                return StateTest();
            }

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

        /// <summary>
        /// Proves the pill, the timer row and the real state agree after the
        /// timer ends, and that a deadline missed while the PC was asleep does
        /// not fire the end action. The test sets the end to "then nothing",
        /// so it can never hibernate the PC. Result: %TEMP%\sleepguard-statetest.txt.
        /// </summary>
        private static int StateTest()
        {
            // The card saves its settings when it resizes, which would store the
            // test's "then nothing". Keep the real file and put it back at the end.
            string iniPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SleepGuard", "settings.ini");
            string iniBefore = System.IO.File.Exists(iniPath) ? System.IO.File.ReadAllText(iniPath) : null;

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MainWindow w = new MainWindow();
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            Func<string, string> line = delegate(string label)
            {
                string drawn = new[] { "Auto", "Awake", "Screen" }[w.DrawnIndex];
                return label + ": state=" + w.Mode + " drawn=" + drawn +
                    " timerRow=" + (w.ArmedRowOpen ? "open" : "closed") + " finish=" + w.LastFinish;
            };

            w.Loaded += delegate
            {
                Step(400, delegate
                {
                    w.SelectForTest(WakeMode.Awake);
                    Step(700, delegate
                    {
                        log.AppendLine(line("1 armed Awake          "));
                        w.DeadlineForTest(-1);
                        w.TickForTest();
                        Step(700, delegate
                        {
                            log.AppendLine(line("2 timer just ran out   "));
                            w.SelectForTest(WakeMode.Awake);
                            Step(700, delegate
                            {
                                w.DeadlineForTest(-600);
                                w.TickForTest();
                                Step(700, delegate
                                {
                                    log.AppendLine(line("3 ran out while asleep "));
                                    w.SelectForTest(WakeMode.Screen);
                                    Step(700, delegate
                                    {
                                        w.SelectForTest(WakeMode.Auto);
                                        Step(700, delegate
                                        {
                                            log.AppendLine(line("4 Screen then Auto     "));
                                            System.IO.File.WriteAllText(
                                                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                    "sleepguard-statetest.txt"), log.ToString());
                                            Native.Apply(WakeMode.Auto);
                                            app.Shutdown();
                                        });
                                    });
                                });
                            });
                        });
                    });
                });
            };

            w.Show();
            int rc = app.Run();
            // After Run: closing the window saves the settings one last time.
            if (iniBefore != null) System.IO.File.WriteAllText(iniPath, iniBefore);
            return rc;
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
