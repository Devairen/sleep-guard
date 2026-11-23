using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SleepGuard
{
    /// <summary>
    /// Makes the card behave like a desktop widget (added 18/11/2026): a normal
    /// window that other windows cover, revealed by Show desktop (Win+D, or the
    /// corner of the taskbar) instead of hidden by it. Rainmeter's "On desktop"
    /// works the same way.
    ///
    /// Win+D minimizes nothing. Explorer lifts the desktop window ("Progman")
    /// above every normal window and makes it the foreground window, so a normal
    /// window ends up behind it. When that happens the card is pinned on top
    /// (never activated). As soon as any other window becomes foreground, the
    /// card is unpinned and put back right above the desktop, under every window.
    /// The same logic lives in the Today card (todo-app/desktop-card/desktop-layer.js).
    ///
    /// Two things found while testing on Joris's PC:
    ///  - ShowInTaskbar = false makes WPF hang the card under a hidden owner
    ///    window. The card is a tool window instead (no taskbar button, no
    ///    Alt+Tab entry, no owner).
    ///  - The first pin after a start was silently ignored by Windows when the
    ///    card sat above the desktop at the moment of Win+D: SetWindowPos said
    ///    yes and nothing changed. Clearing the pin first and then setting it
    ///    works. A check 300 ms and 1 s later re-pins if it still did not hold.
    /// </summary>
    internal sealed class DesktopLayer : IDisposable
    {
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        private const uint GW_HWNDLAST = 1, GW_HWNDPREV = 3;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_FLAGS = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

        private static readonly string[] Desktop = { "Progman", "WorkerW" };
        private static readonly string[] KeepsReveal = {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "#32768"
        };

        private readonly Window _window;
        private readonly Func<bool> _pinned;
        private readonly WinEventProc _proc;   // held so the GC never frees the callback
        private readonly DispatcherTimer _delay;
        private IntPtr _hwnd;
        private IntPtr _hook;
        private bool _revealed;
        private int _retries;

        public DesktopLayer(Window window, Func<bool> pinned)
        {
            _window = window;
            _pinned = pinned;
            _proc = OnForeground;
            // No work inside the hook itself; Explorer may still be reordering.
            _delay = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher);
            _delay.Interval = TimeSpan.FromMilliseconds(80);
            _delay.Tick += delegate { _delay.Stop(); Check(); };
        }

        /// <summary>Call from SourceInitialized: the window has a handle and is not shown yet.</summary>
        public void Start()
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero || _hook != IntPtr.Zero) return;
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, 0);
        }

        private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (_delay.IsEnabled) return;
            _retries = 0;
            _delay.Interval = TimeSpan.FromMilliseconds(80);
            _delay.Start();
        }

        private static string ClassOf(IntPtr h)
        {
            StringBuilder s = new StringBuilder(256);
            GetClassName(h, s, 256);
            return s.ToString();
        }

        private static bool In(string[] set, string v) { return Array.IndexOf(set, v) >= 0; }

        private bool IsTopmost() { return (GetWindowLong(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0; }

        private void Pin()
        {
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_FLAGS);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FLAGS);
        }

        /// <summary>Check a moment later that the pin held; put it back if not.</summary>
        private void VerifyAfter(int ms)
        {
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal, _window.Dispatcher);
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate
            {
                t.Stop();
                if (_revealed && !IsTopmost()) Pin();
            };
            t.Start();
        }

        private bool DesktopCoversCard()
        {
            IntPtr h = GetWindow(_hwnd, GW_HWNDPREV);
            for (int i = 0; h != IntPtr.Zero && i < 5000; i++)
            {
                if (In(Desktop, ClassOf(h)) && IsWindowVisible(h)) return true;
                h = GetWindow(h, GW_HWNDPREV);
            }
            return false;
        }

        /// <summary>Put the card directly above the desktop, under every other window.</summary>
        private void SinkToDesktop()
        {
            IntPtr h = GetWindow(_hwnd, GW_HWNDLAST);
            bool seenDesktop = false;
            for (int i = 0; h != IntPtr.Zero && i < 5000; i++)
            {
                if (h == _hwnd) { if (seenDesktop) return; }
                else if (In(Desktop, ClassOf(h))) seenDesktop = true;
                else if (seenDesktop) { SetWindowPos(_hwnd, h, 0, 0, 0, 0, SWP_FLAGS); return; }
                h = GetWindow(h, GW_HWNDPREV);
            }
        }

        private void Check()
        {
            if (_hwnd == IntPtr.Zero || _pinned()) { _revealed = false; return; }
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            string cls = ClassOf(fg);

            if (In(Desktop, cls))
            {
                if (_revealed || !_window.IsVisible) return;
                if (!DesktopCoversCard())
                {
                    // Explorer may finish lifting the desktop after the event: look again.
                    if (_retries++ < 3) { _delay.Interval = TimeSpan.FromMilliseconds(200); _delay.Start(); }
                    return;
                }
                Pin();
                _revealed = true;
                VerifyAfter(300);
                VerifyAfter(1000);
                return;
            }
            if (!_revealed || In(KeepsReveal, cls) || fg == _hwnd) return;

            // Show desktop is over: back onto the desktop, under every window.
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_FLAGS);
            SinkToDesktop();
            _revealed = false;
        }

        public void Dispose()
        {
            _delay.Stop();
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }
    }
}
