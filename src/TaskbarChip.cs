using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SleepGuard
{
    /// <summary>
    /// Taskbar mode (added 23/11/2026, when Joris went to one monitor): a small
    /// chip inside the taskbar, right next to the tray icons. It shows the mode
    /// as a Windows icon (moon = Auto, bolt = Awake, monitor = Screen), a ring
    /// that empties as the time limit runs out, and the time left as h:mm.
    /// Clicking it springs the card up above the taskbar (MainWindow).
    ///
    /// Windows never covers the taskbar, so the chip is always visible without
    /// sitting over any window. It is a topmost tool window that never takes
    /// focus, placed left of TrayNotifyWnd. The taskbar lifts itself above it
    /// when clicked, so the chip re-pins on every foreground change and checks
    /// every 250 ms. A full-screen game or video hides the taskbar, and the chip
    /// hides with it. The Today card's chip (todo-app/desktop-card/taskbar.js)
    /// finds this one by its title, "Sleep Guard chip", and sits to its left.
    /// </summary>
    internal sealed class TaskbarChip : Window
    {
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_PIN = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
        private const uint GW_HWNDPREV = 3;
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        private const double ChipHeight = 40;   // Windows 11 taskbar buttons
        private const double Gap = 4;

        public const string ChipTitle = "Sleep Guard chip";

        public event Action Clicked;
        public event Action RightClicked;

        private readonly Border _face;
        private readonly TextBlock _glyph;
        private readonly TextBlock _text;
        private readonly Ellipse _track;
        private readonly Path _arc;
        private readonly ScaleTransform _press = new ScaleTransform(1, 1);
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _soon;
        private readonly WinEventProc _proc;
        private IntPtr _hwnd;
        private IntPtr _hook;
        private bool _running;
        private bool _light;

        public TaskbarChip()
        {
            Title = ChipTitle;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = true;      // hidden by WS_EX_TOOLWINDOW instead (see DesktopLayer.cs)
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            SizeToContent = SizeToContent.Width;
            Height = ChipHeight;
            UseLayoutRounding = true;
            Left = -2000; Top = -2000;

            _glyph = new TextBlock();
            _glyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            _glyph.FontSize = 15;
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            _glyph.RenderTransformOrigin = new Point(0.5, 0.5);
            _glyph.RenderTransform = _press;

            _track = new Ellipse();
            _track.Width = 26; _track.Height = 26;
            _track.StrokeThickness = 2;

            _arc = new Path();
            _arc.StrokeThickness = 2;
            _arc.StrokeStartLineCap = PenLineCap.Round;
            _arc.StrokeEndLineCap = PenLineCap.Round;

            Grid icon = new Grid();
            icon.Width = 26; icon.Height = 26;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Children.Add(_track);
            icon.Children.Add(_arc);
            icon.Children.Add(_glyph);

            _text = new TextBlock();
            _text.FontFamily = Theme.Face;
            _text.FontSize = 13;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _text.Margin = new Thickness(6, 0, 0, 1);
            Theme.UseTabularFigures(_text);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
            row.Children.Add(_text);

            _face = new Border();
            _face.Height = ChipHeight;
            _face.Padding = new Thickness(7, 0, 9, 0);
            _face.CornerRadius = new CornerRadius(4);
            _face.Child = row;
            Content = _face;

            _face.MouseEnter += delegate { Paint(); };
            _face.MouseLeave += delegate { _press.ScaleX = _press.ScaleY = 1; Paint(); };
            _face.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _press.ScaleX = _press.ScaleY = 0.86;
                Paint();
            };
            _face.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Bounce();
                Paint();
                if (Clicked != null) Clicked();
            };
            _face.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (RightClicked != null) RightClicked();
            };

            // The hover text opens above the chip, never over it: on the Today
            // chip a tooltip drawn over the chip swallowed the next click.
            ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetVerticalOffset(this, -8);
            ToolTipService.SetInitialShowDelay(this, 450);

            _proc = OnForeground;
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += delegate { Tick(); };
            _soon = new DispatcherTimer();
            _soon.Interval = TimeSpan.FromMilliseconds(30);
            _soon.Tick += delegate { _soon.Stop(); Tick(); };

            SourceInitialized += delegate
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW);
            };
            SizeChanged += delegate { Tick(); };

            Update(WakeMode.Auto, 1.0, false, 0);
        }

        // ---- public surface ------------------------------------------------

        public bool Running { get { return _running; } }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _light = TaskbarIsLight();
            Show();
            if (_hook == IntPtr.Zero)
                _hook = SetWinEventHook(3, 3, IntPtr.Zero, _proc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            _timer.Start();
            Tick();
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
            Hide();
        }

        /// <summary>The chip's rectangle in WPF units, for placing the card above it.</summary>
        public Rect Bounds { get { return new Rect(Left, Top, ActualWidth, ActualHeight); } }

        private WakeMode _mode = WakeMode.Auto;
        private double _fraction = 1.0;
        private bool _limited;

        /// <param name="secondsLeft">time left on the limit; only shown when limited</param>
        public void Update(WakeMode mode, double fraction, bool limited, double secondsLeft)
        {
            _mode = mode;
            _fraction = Math.Max(0, Math.Min(1, fraction));
            _limited = limited && mode != WakeMode.Auto;

            // The same three icons as the card's own Auto / Awake / Screen pill.
            if (mode == WakeMode.Awake) _glyph.Text = "";        // lightning bolt
            else if (mode == WakeMode.Screen) _glyph.Text = "";  // monitor
            else _glyph.Text = "";                               // moon

            if (_limited)
            {
                int minutes = (int)Math.Ceiling(Math.Max(0, secondsLeft) / 60.0);
                _text.Text = (minutes / 60) + ":" + (minutes % 60).ToString("00");
                _text.Visibility = Visibility.Visible;
            }
            else
            {
                _text.Text = "";
                _text.Visibility = Visibility.Collapsed;
            }
            DrawArc();
            Paint();
        }

        /// <summary>Windows' own "light taskbar" switch (Settings, Personalisation, Colours).</summary>
        private static bool TaskbarIsLight()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        // ---- drawing -------------------------------------------------------

        private void Paint()
        {
            Color fg = _light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xFF, 0xFF, 0xFF);
            Color live = _mode == WakeMode.Screen ? Theme.AccentDeep : Theme.Accent;
            _text.Foreground = new SolidColorBrush(fg);
            _glyph.Foreground = new SolidColorBrush(_mode == WakeMode.Auto ? Color.FromArgb(0xC8, fg.R, fg.G, fg.B) : live);
            _track.Stroke = new SolidColorBrush(_mode == WakeMode.Auto ? Colors.Transparent
                : (_light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));
            _arc.Stroke = new SolidColorBrush(live);

            // Almost clear, never fully: a fully clear pixel lets clicks fall through.
            byte a = 0x02;
            if (_face.IsMouseOver) a = (byte)(_press.ScaleX < 1 ? 0x0C : 0x14);
            _face.Background = new SolidColorBrush(_light ? Color.FromArgb(a, 0, 0, 0) : Color.FromArgb(a, 0xFF, 0xFF, 0xFF));
        }

        private void DrawArc()
        {
            if (_mode == WakeMode.Auto) { _arc.Data = null; return; }
            double sweep = _limited ? 360.0 * _fraction : 360.0;
            const double c = 13, r = 12;
            if (sweep >= 359.5) { _arc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
            if (sweep < 0.5) { _arc.Data = null; return; }
            double rad = (sweep - 90) * Math.PI / 180.0;
            Point start = new Point(c, c - r);
            Point end = new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
            PathFigure fig = new PathFigure();
            fig.StartPoint = start;
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
            PathGeometry g = new PathGeometry();
            g.Figures.Add(fig);
            _arc.Data = g;
        }

        /// <summary>The icon springs back after a press, slightly past full size.</summary>
        private void Bounce()
        {
            System.Windows.Media.Animation.DoubleAnimation a = new System.Windows.Media.Animation.DoubleAnimation(
                0.86, 1.0, TimeSpan.FromMilliseconds(380));
            System.Windows.Media.Animation.ElasticEase e = new System.Windows.Media.Animation.ElasticEase();
            e.Oscillations = 1; e.Springiness = 5; e.EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut;
            a.EasingFunction = e;
            a.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
            _press.ScaleX = _press.ScaleY = 1;
            _press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ---- staying in the taskbar ---------------------------------------

        private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (!_soon.IsEnabled) _soon.Start();
        }

        private static string ClassOf(IntPtr h)
        {
            StringBuilder s = new StringBuilder(256);
            GetClassName(h, s, 256);
            return s.ToString();
        }

        private bool FullscreenApp(System.Drawing.Rectangle screen)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd) return false;
            string cls = ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW") return false;
            int st;
            if (SHQueryUserNotificationState(out st) == 0 && (st == 2 || st == 3 || st == 4)) return true;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }

        private bool CoveredBy(IntPtr bar)
        {
            IntPtr h = GetWindow(_hwnd, GW_HWNDPREV);
            for (int i = 0; h != IntPtr.Zero && i < 400; i++)
            {
                if (h == bar) return true;
                h = GetWindow(h, GW_HWNDPREV);
            }
            return false;
        }

        private void Tick()
        {
            if (!_running || _hwnd == IntPtr.Zero) return;

            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            IntPtr tray = bar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT b = new RECT(), t = new RECT();
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            bool ok = bar != IntPtr.Zero && tray != IntPtr.Zero && GetWindowRect(bar, out b) && GetWindowRect(tray, out t);
            // Auto-hidden or vertical taskbar, or a full-screen app: step aside.
            if (!ok || b.Top >= screen.Bottom - 4 || (b.Bottom - b.Top) > 120 || FullscreenApp(screen))
            {
                if (IsVisible) Hide();
                return;
            }
            if (!IsVisible) Show();

            double scale = 1.0;
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M11;

            double w = ActualWidth > 0 ? ActualWidth : 40;
            double left = (t.Left / scale) - Gap - w;
            double top = (b.Top / scale) + ((b.Bottom - b.Top) / scale - ChipHeight) / 2;
            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;

            if (CoveredBy(bar))
            {
                SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_PIN);
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_PIN);
            }
        }
    }
}
