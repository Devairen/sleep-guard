using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SleepGuard
{
    /// <summary>
    /// The tray icon. This is the surface that is always visible, so it carries
    /// the state: a ring that empties as the time runs out, plus the menu.
    /// </summary>
    internal sealed class Tray : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static readonly Color Dim = Color.FromArgb(105, 255, 255, 255);
        private static readonly Color Accent = Color.FromArgb(255, 0xFF, 0x88, 0x39);
        private static readonly Color AccentDeep = Color.FromArgb(255, 0xFF, 0x6A, 0x1A);

        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _topmostItem;
        private readonly ToolStripMenuItem _startupItem;

        private IntPtr _lastHandle = IntPtr.Zero;

        public event Action ShowRequested;
        /// A left click on the icon itself (the menu's Show/Hide item stays ShowRequested).
        public event Action IconClicked;
        public event Action QuitRequested;
        public event Action<bool> TopmostToggled;
        public event Action<bool> TaskbarToggled;
        public event Action ExtendRequested;
        private readonly ToolStripMenuItem _taskbarItem;

        public Tray()
        {
            _showItem = new ToolStripMenuItem("Show widget");
            _showItem.Click += delegate { if (ShowRequested != null) ShowRequested(); };

            _topmostItem = new ToolStripMenuItem("Keep above other windows");
            _topmostItem.CheckOnClick = true;
            _topmostItem.Click += delegate
            {
                if (TopmostToggled != null) TopmostToggled(_topmostItem.Checked);
            };

            _taskbarItem = new ToolStripMenuItem("Show in the taskbar");
            _taskbarItem.CheckOnClick = true;
            _taskbarItem.Click += delegate
            {
                if (TaskbarToggled != null) TaskbarToggled(_taskbarItem.Checked);
            };

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.CheckOnClick = true;
            _startupItem.Checked = Settings.StartsWithWindows();
            _startupItem.Click += delegate
            {
                Settings.SetStartWithWindows(_startupItem.Checked);
            };

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit (restores normal sleep)");
            quit.Click += delegate { if (QuitRequested != null) QuitRequested(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenu());
            menu.BackColor = Color.FromArgb(20, 20, 21);
            menu.ForeColor = Color.FromArgb(244, 244, 245);
            menu.ShowImageMargin = false;
            // Without this the ticks on "Show in the taskbar" and "Start with
            // Windows" were never drawn (found 23/11/2026).
            menu.ShowCheckMargin = true;
            menu.Items.Add(_showItem);
            menu.Items.Add(_taskbarItem);
            menu.Items.Add(_topmostItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            _icon = new NotifyIcon();
            _icon.ContextMenuStrip = menu;
            _icon.Text = "Sleep Guard";
            _icon.Visible = true;
            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && IconClicked != null) IconClicked();
            };
            _icon.BalloonTipClicked += delegate
            {
                if (ExtendRequested != null) ExtendRequested();
            };

            Draw(WakeMode.Auto, 1.0, false);
        }

        public void SetTopmostChecked(bool on)
        {
            _topmostItem.Checked = on;
        }

        /// <summary>Taskbar mode hides the items that only make sense for the desktop card.</summary>
        public void SetTaskbarChecked(bool on)
        {
            _taskbarItem.Checked = on;
            _topmostItem.Visible = !on;
            _showItem.Visible = !on;
        }

        /// <summary>The same menu, opened from a right-click on the taskbar chip.</summary>
        public void ShowMenuAtCursor()
        {
            try { _icon.ContextMenuStrip.Show(Cursor.Position); }
            catch { }
        }

        public void SetShowLabel(bool windowVisible)
        {
            _showItem.Text = windowVisible ? "Hide widget" : "Show widget";
        }

        public void SetTooltip(string text)
        {
            // Windows truncates past 63 characters and throws past 127.
            if (text == null) text = "Sleep Guard";
            if (text.Length > 60) text = text.Substring(0, 60);
            _icon.Text = text;
        }

        public void Warn(string title, string body)
        {
            try { _icon.ShowBalloonTip(12000, title, body, ToolTipIcon.None); }
            catch { }
        }

        /// <summary>
        /// Redraw the icon. <paramref name="fraction"/> is the share of the
        /// chosen time still left, so the ring reads like a fuel gauge.
        /// </summary>
        public void Draw(WakeMode mode, double fraction, bool limited)
        {
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;

            Color live = mode == WakeMode.Screen ? AccentDeep : Accent;

            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    Rectangle ring = new Rectangle(5, 5, 22, 22);

                    using (Pen track = new Pen(Dim, 4f))
                        g.DrawEllipse(track, ring);

                    if (mode != WakeMode.Auto)
                    {
                        float sweep = limited ? (float)(360.0 * fraction) : 360f;
                        using (Pen pen = new Pen(live, 4f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            if (sweep >= 359.5f) g.DrawEllipse(pen, ring);
                            else if (sweep > 0.5f) g.DrawArc(pen, ring, -90f, sweep);
                        }

                        float dot = mode == WakeMode.Screen ? 6f : 4f;
                        using (SolidBrush fill = new SolidBrush(live))
                            g.FillEllipse(fill, 16f - dot, 16f - dot, dot * 2f, dot * 2f);
                    }
                }

                IntPtr handle = bmp.GetHicon();
                try
                {
                    _icon.Icon = Icon.FromHandle(handle);
                    if (_lastHandle != IntPtr.Zero) DestroyIcon(_lastHandle);
                    _lastHandle = handle;
                }
                catch
                {
                    DestroyIcon(handle);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
            if (_lastHandle != IntPtr.Zero)
            {
                DestroyIcon(_lastHandle);
                _lastHandle = IntPtr.Zero;
            }
        }

        /// <summary>Dark colours for the tray menu, so it matches the widget.</summary>
        private sealed class DarkMenu : ProfessionalColorTable
        {
            private static readonly Color Surface = Color.FromArgb(20, 20, 21);
            private static readonly Color Hover = Color.FromArgb(38, 38, 40);
            private static readonly Color Edge = Color.FromArgb(48, 48, 50);

            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuBorder { get { return Edge; } }
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Edge; } }
            public override Color SeparatorLight { get { return Edge; } }
            public override Color CheckBackground { get { return Color.FromArgb(0xFF, 0x88, 0x39); } }
            public override Color CheckSelectedBackground { get { return Color.FromArgb(0xFF, 0x88, 0x39); } }
        }
    }
}
