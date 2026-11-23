using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SleepGuard
{
    internal sealed class MainWindow : Window
    {
        private const double CardWidth = 272;
        private const double ContentWidth = 240;
        private const double ShadowRoom = 12;

        /// Height the timer row needs. The default bottom-right placement
        /// leaves this much free below the card, so arming it grows the card
        /// downwards into empty space instead of shoving the whole window up.
        private const double ArmedRoom = 80;

        private static readonly int[] Durations = { 3600, 7200, 14400, 28800, 0 };
        private static readonly string[] DurationLabels = { "1h", "2h", "4h", "8h", "∞" };

        private readonly Settings _settings;
        private readonly Tray _tray;
        private readonly DesktopLayer _layer;
        private readonly StatePill _pill;
        private readonly DispatcherTimer _timer;

        private readonly TextBlock _caption;
        private readonly TextBlock _countdown;
        private readonly TextBlock _endLabel;
        private readonly Border _armedPanel;
        private readonly Border _progressFill;
        private readonly Border[] _chips = new Border[5];
        private readonly TextBlock[] _chipLabels = new TextBlock[5];

        private double _armedHeight;
        private DateTime _deadline;
        private bool _hasDeadline;
        private bool _warned;
        private int _ticks;
        private bool _quitting;

        private bool _placedByDefault;

        private readonly string _autoCaption;

        // ---- taskbar mode (23/11/2026) ---------------------------------------
        // The card becomes a flyout: a chip in the taskbar (TaskbarChip.cs)
        // springs it up above the taskbar, a click anywhere else closes it.
        private readonly Border _card;
        private readonly TranslateTransform _lift = new TranslateTransform();
        private readonly TaskbarChip _chip;
        private bool _flyOpen;
        private double _flyBottom;
        private DateTime _lastHide = DateTime.MinValue;

        /// Off during the self-tests: they need a visible card to measure.
        private bool TaskbarOn { get { return _settings.Taskbar && !Program.TestRun; } }

        public MainWindow()
        {
            _settings = Settings.Load();

            // ---- window chrome ------------------------------------------
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            // Kept out of the taskbar by DesktopLayer (a tool window), not by
            // ShowInTaskbar = false: that makes WPF hang the card under a hidden
            // owner window, and Windows then ignored the Win+D pin (18/11/2026).
            ShowInTaskbar = true;
            SizeToContent = SizeToContent.Height;
            Width = CardWidth + 2 * ShadowRoom;
            Topmost = _settings.Topmost || TaskbarOn;
            ShowActivated = !TaskbarOn;
            Title = "Sleep Guard";
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            // ---- the card -----------------------------------------------
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 22;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Opacity = 0.55;

            Border card = new Border();
            card.Width = CardWidth;
            card.Margin = new Thickness(ShadowRoom);
            card.CornerRadius = new CornerRadius(16);
            card.Background = Theme.Fixed(Theme.Card);
            card.BorderBrush = Theme.Fixed(Theme.Hairline);
            card.BorderThickness = new Thickness(1);
            card.Effect = shadow;
            card.MouseLeftButtonDown += OnCardPressed;
            card.RenderTransform = _lift;
            if (TaskbarOn) card.Opacity = 0;   // invisible until the chip opens it
            _card = card;

            StackPanel content = new StackPanel();
            // 15, not 16: the 1 px border leaves 270 inside, and 16+240+16 was
            // 2 px too wide, so WPF clipped the right edge ("then hibernat",
            // the pill's end). Found 23/11/2026.
            content.Margin = new Thickness(15, 13, 15, 13);
            content.Width = ContentWidth;
            card.Child = content;

            content.Children.Add(BuildHeader());

            _pill = new StatePill();
            _pill.Margin = new Thickness(0, 10, 0, 0);
            _pill.ModeChanged += OnModeChanged;
            content.Children.Add(_pill);

            string displayOff = Native.FormatIdle(Native.DisplayOffAfterSeconds());
            string sleepAfter = Native.FormatIdle(Native.SleepAfterSeconds());
            if (displayOff != null && sleepAfter != null)
                _autoCaption = "Screen off " + displayOff + "  ·  sleeps " + sleepAfter;
            else
                _autoCaption = "Windows decides when to sleep";

            _caption = new TextBlock();
            _caption.Text = _autoCaption;
            _caption.FontFamily = Theme.Face;
            _caption.FontSize = 11;
            _caption.Foreground = Theme.Fixed(Theme.TextSecondary);
            _caption.Margin = new Thickness(2, 9, 0, 0);
            content.Children.Add(_caption);

            // ---- the part that only exists while armed -------------------
            StackPanel armedInner = new StackPanel();
            armedInner.Width = ContentWidth;
            armedInner.VerticalAlignment = VerticalAlignment.Top;
            armedInner.Children.Add(BuildChips());
            armedInner.Children.Add(BuildProgress(out _progressFill));
            armedInner.Children.Add(BuildFooter(out _countdown, out _endLabel));

            armedInner.Measure(new Size(ContentWidth, double.PositiveInfinity));
            _armedHeight = armedInner.DesiredSize.Height;

            _armedPanel = new Border();
            _armedPanel.ClipToBounds = true;
            _armedPanel.Height = 0;
            _armedPanel.Opacity = 0;
            _armedPanel.Child = armedInner;
            content.Children.Add(_armedPanel);

            Content = card;

            // ---- tray ----------------------------------------------------
            _tray = new Tray();
            _tray.ShowRequested += ToggleVisible;
            _tray.IconClicked += OnTrayClick;
            // A desktop widget: other windows cover it, Win+D reveals it.
            // The flyout manages its own z-order, so the layer leaves it alone.
            _layer = new DesktopLayer(this, delegate { return _settings.Topmost || TaskbarOn; });
            SourceInitialized += delegate { _layer.Start(); };
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _tray.QuitRequested += Quit;
            _tray.TopmostToggled += OnTopmostToggled;
            _tray.TaskbarToggled += SetTaskbarMode;
            _tray.ExtendRequested += Extend30;
            _tray.SetTopmostChecked(_settings.Topmost);
            _tray.SetTaskbarChecked(TaskbarOn);

            _chip = new TaskbarChip();
            _chip.Clicked += ToggleFlyout;
            _chip.RightClicked += delegate { _tray.ShowMenuAtCursor(); };
            Deactivated += delegate { if (TaskbarOn && _flyOpen) HideFlyout(); };

            // ---- timer ---------------------------------------------------
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;

            SelectDuration(IndexOfDuration(_settings.DurationSeconds), false);
            PaintEndLabel();
            Place();

            Loaded += delegate
            {
                // Claim the wake state from the UI thread: Windows drops the
                // hold when the thread that set it exits, so it must always be
                // this one.
                Native.Apply(WakeMode.Auto);
                RefreshTray();

                // The real height is only known now, so finish the default
                // placement here, keeping ArmedRoom free underneath.
                if (_placedByDefault)
                {
                    Rect area = SystemParameters.WorkArea;
                    Top = area.Bottom - ActualHeight - ArmedRoom;
                    if (Top < area.Top) Top = area.Top;
                }
                SizeChanged += OnWindowSizeChanged;

                // Taskbar mode: the first Show only gave the card its layout;
                // it waits hidden for a click on the chip.
                if (TaskbarOn)
                {
                    Hide();
                    _chip.Start();
                }
            };

            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (_quitting) return;
                e.Cancel = true;          // the × hides; only the menu quits
                HideWidget();
            };
        }

        // ---- building blocks --------------------------------------------

        private UIElement BuildHeader()
        {
            Border dot = new Border();
            dot.Width = 6;
            dot.Height = 6;
            dot.CornerRadius = new CornerRadius(3);
            dot.Background = Theme.Fixed(Theme.Accent);
            dot.VerticalAlignment = VerticalAlignment.Center;

            TextBlock name = new TextBlock();
            name.Text = "SLEEP GUARD";
            name.FontFamily = Theme.Face;
            name.FontSize = 9.5;
            name.FontWeight = FontWeights.SemiBold;
            name.Foreground = Theme.Fixed(Theme.TextTertiary);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(7, 0, 0, 0);

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.Children.Add(dot);
            left.Children.Add(name);

            TextBlock close = new TextBlock();
            close.Text = "✕";
            close.FontFamily = Theme.Face;
            close.FontSize = 11;
            close.Foreground = Theme.Fixed(Theme.TextTertiary);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Opacity = 0;
            close.Cursor = Cursors.Hand;
            close.Padding = new Thickness(6, 2, 0, 2);
            ToolTipService.SetToolTip(close, "Hide to the tray. The widget keeps running.");
            close.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                HideWidget();
            };

            Grid row = new Grid();
            row.Height = 16;
            row.Children.Add(left);
            row.Children.Add(close);

            row.MouseEnter += delegate { Fade(close, 1.0); };
            row.MouseLeave += delegate { Fade(close, 0.0); };

            return row;
        }

        private UIElement BuildChips()
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, 13, 0, 0);
            row.Height = 26;
            for (int i = 0; i < 5; i++)
            {
                ColumnDefinition cd = new ColumnDefinition();
                cd.Width = new GridLength(1, GridUnitType.Star);
                row.ColumnDefinitions.Add(cd);
            }

            for (int i = 0; i < 5; i++)
            {
                TextBlock label = new TextBlock();
                label.Text = DurationLabels[i];
                label.FontFamily = Theme.Face;
                label.FontSize = 11;
                label.FontWeight = FontWeights.SemiBold;
                label.Foreground = Theme.Live(Theme.TextTertiary);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
                _chipLabels[i] = label;

                Border chip = new Border();
                chip.CornerRadius = new CornerRadius(13);
                chip.Background = Theme.Live(Colors.Transparent);
                chip.Margin = new Thickness(i == 0 ? 0 : 2.5, 0, i == 4 ? 0 : 2.5, 0);
                chip.Cursor = Cursors.Hand;
                chip.Child = label;
                _chips[i] = chip;

                int index = i;
                chip.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    SelectDuration(index, true);
                };

                Grid.SetColumn(chip, i);
                row.Children.Add(chip);
            }
            return row;
        }

        private UIElement BuildProgress(out Border fill)
        {
            Border track = new Border();
            track.Height = 3;
            track.CornerRadius = new CornerRadius(1.5);
            track.Background = Theme.Fixed(Theme.Hairline);
            track.Margin = new Thickness(0, 11, 0, 0);
            track.ClipToBounds = true;

            fill = new Border();
            fill.Height = 3;
            fill.Width = ContentWidth;
            fill.CornerRadius = new CornerRadius(1.5);
            fill.Background = Theme.Fixed(Theme.Accent);
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            track.Child = fill;

            return track;
        }

        private UIElement BuildFooter(out TextBlock countdown, out TextBlock end)
        {
            countdown = new TextBlock();
            countdown.FontFamily = Theme.Numerals;
            countdown.FontSize = 14;
            countdown.FontWeight = FontWeights.SemiBold;
            countdown.Foreground = Theme.Fixed(Theme.Text);
            countdown.VerticalAlignment = VerticalAlignment.Center;
            countdown.HorizontalAlignment = HorizontalAlignment.Left;
            Theme.UseTabularFigures(countdown);

            end = new TextBlock();
            end.FontFamily = Theme.Face;
            end.FontSize = 11;
            end.Foreground = Theme.Fixed(Theme.TextTertiary);
            end.VerticalAlignment = VerticalAlignment.Center;
            end.HorizontalAlignment = HorizontalAlignment.Right;
            end.Cursor = Cursors.Hand;
            ToolTipService.SetToolTip(end, "Click to change what happens when the time is up.");

            TextBlock endRef = end;
            end.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                CycleEndAction();
            };
            end.MouseEnter += delegate { endRef.Foreground = Theme.Fixed(Theme.TextSecondary); };
            end.MouseLeave += delegate { endRef.Foreground = Theme.Fixed(Theme.TextTertiary); };

            Grid row = new Grid();
            row.Margin = new Thickness(0, 9, 0, 0);
            row.Height = 18;
            row.Children.Add(countdown);
            row.Children.Add(end);
            return row;
        }

        // ---- interaction -------------------------------------------------

        private void OnCardPressed(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (TaskbarOn) return;   // the flyout sits where the chip puts it
            try { DragMove(); }
            catch { }
            SavePosition();
        }

        /// <summary>
        /// Drive the pill from code so the geometry test can prove the card
        /// does not move when the timer row opens. WPF hit-tests against the
        /// real cursor, so a synthetic click cannot do this.
        /// </summary>
        internal void ArmForTest()
        {
            _pill.Select(WakeMode.Awake);
        }

        internal string GeometryLine(string label)
        {
            return label + ": left=" + Left.ToString("F0") + " top=" + Top.ToString("F0") +
                " height=" + ActualHeight.ToString("F0");
        }

        private void OnModeChanged(WakeMode mode)
        {
            Native.Apply(mode);
            _caption.Text = CaptionFor(mode);
            FlashCaption();

            if (mode == WakeMode.Auto)
            {
                _hasDeadline = false;
                _timer.Stop();
                SetArmed(false);
            }
            else
            {
                if (!_timer.IsEnabled) _timer.Start();
                // Coming from Auto starts the clock; switching between Awake
                // and Screen keeps whatever time is left.
                if (!_hasDeadline && _settings.DurationSeconds > 0) StartCountdown();
                SetArmed(true);
            }
            Tick();
        }

        private string CaptionFor(WakeMode mode)
        {
            if (mode == WakeMode.Awake) return "PC stays awake  ·  screen still blanks";
            if (mode == WakeMode.Screen) return "Screen and PC both stay on";
            return _autoCaption;
        }

        private void SelectDuration(int index, bool fromClick)
        {
            if (index < 0 || index > 4) index = 1;
            _settings.DurationSeconds = Durations[index];
            _settings.Save();

            for (int i = 0; i < 5; i++)
            {
                bool on = i == index;
                SolidColorBrush bg = (SolidColorBrush)_chips[i].Background;
                bg.Color = on ? Color.FromArgb(0x2E, 0xFF, 0x88, 0x39) : Colors.Transparent;
                ((SolidColorBrush)_chipLabels[i].Foreground).Color = on ? Theme.Accent : Theme.TextTertiary;
            }

            if (!fromClick) return;

            if (_settings.DurationSeconds == 0)
            {
                _hasDeadline = false;
                _warned = false;
            }
            else if (_pill.Mode != WakeMode.Auto)
            {
                StartCountdown();
            }
            Tick();
        }

        private void StartCountdown()
        {
            _deadline = DateTime.UtcNow.AddSeconds(_settings.DurationSeconds);
            _hasDeadline = true;
            _warned = false;
        }

        private void CycleEndAction()
        {
            if (_settings.End == EndAction.Hibernate) _settings.End = EndAction.Sleep;
            else if (_settings.End == EndAction.Sleep) _settings.End = EndAction.Release;
            else _settings.End = EndAction.Hibernate;
            _settings.Save();
            PaintEndLabel();
        }

        private void PaintEndLabel()
        {
            if (_settings.End == EndAction.Sleep) _endLabel.Text = "then sleep";
            else if (_settings.End == EndAction.Release) _endLabel.Text = "then nothing";
            else _endLabel.Text = "then hibernate";
        }

        private void Extend30()
        {
            if (_pill.Mode == WakeMode.Auto) return;
            if (!_hasDeadline) return;
            _deadline = _deadline.AddMinutes(30);
            _warned = false;
            Tick();
        }

        private void OnTopmostToggled(bool on)
        {
            Topmost = on;
            _settings.Topmost = on;
            _settings.Save();
        }

        // ---- the clock ---------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            _ticks++;
            // Belt and braces: re-state the hold every half minute in case
            // something else cleared it.
            if (_ticks % 30 == 0) Native.Apply(_pill.Mode);
            Tick();
        }

        private void Tick()
        {
            if (_pill.Mode == WakeMode.Auto) { RefreshTray(); return; }

            if (!_hasDeadline)
            {
                _countdown.Text = "no limit";
                _progressFill.Width = ContentWidth;
                RefreshTray();
                return;
            }

            double left = (_deadline - DateTime.UtcNow).TotalSeconds;

            if (left <= 0)
            {
                // This runs every second, so being more than 90 s late means
                // the PC was asleep or hibernated when the deadline passed.
                // The machine already went down: just go back to Auto. Running
                // the end action now would hibernate it again the moment he
                // woke it up.
                Finish(left > -LateGraceSeconds);
                return;
            }

            _countdown.Text = FormatRemaining(left);
            double fraction = left / _settings.DurationSeconds;
            if (fraction > 1) fraction = 1;
            _progressFill.Width = ContentWidth * fraction;

            if (!_warned && left <= 120 && _settings.End != EndAction.Release)
            {
                _warned = true;
                string verb = _settings.End == EndAction.Sleep ? "sleep" : "hibernate";
                _tray.Warn("Sleep Guard",
                    "The PC will " + verb + " in 2 minutes. Click here for 30 more.");
            }

            RefreshTray();
        }

        private const double LateGraceSeconds = 90;

        /// <summary>What the last Finish decided; read by the state test.</summary>
        internal string LastFinish = "none";

        /// <param name="runEndAction">False when the deadline passed while the
        /// PC was already down: back to Auto, nothing else.</param>
        private void Finish(bool runEndAction)
        {
            _hasDeadline = false;
            _timer.Stop();
            EndAction action = runEndAction ? _settings.End : EndAction.Release;
            LastFinish = runEndAction ? "end action " + action : "missed while asleep: back to Auto only";

            _pill.SetSilently(WakeMode.Auto);
            _caption.Text = _autoCaption;
            SetArmed(false);
            Native.Apply(WakeMode.Auto);
            RefreshTray();

            if (action == EndAction.Release) return;

            // Let the card draw Auto before the PC goes down. Hibernating in the
            // same breath froze the old picture, so after waking it still showed
            // Awake (19/11/2026).
            System.Windows.Threading.DispatcherTimer later = new System.Windows.Threading.DispatcherTimer();
            later.Interval = TimeSpan.FromMilliseconds(1500);
            later.Tick += delegate
            {
                later.Stop();
                // He may have re-armed it in that second and a half.
                if (_pill.Mode != WakeMode.Auto) return;

                // The widget's one job is that the PC goes down when the timer
                // ends. If Windows refuses, say so: otherwise the machine sits
                // awake all night and nothing on screen admits it.
                bool wentDown = action == EndAction.Hibernate ? Native.Hibernate() : Native.Sleep();
                if (!wentDown)
                {
                    string what = (action == EndAction.Hibernate) ? "hibernate" : "sleep";
                    _tray.Warn("Sleep Guard",
                        "The timer ended but Windows refused to " + what +
                        ". The PC is still awake.");
                }
            };
            later.Start();
        }

        /// <summary>
        /// Back from sleep or hibernation: settle the clock at once (it may have
        /// run out while the PC was down), re-state the wake hold, and redraw.
        /// A see-through WPF window can come back showing its old picture.
        /// </summary>
        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Native.Apply(_pill.Mode);
                if (_pill.Mode != WakeMode.Auto && !_timer.IsEnabled) _timer.Start();
                Tick();
                InvalidateVisual();
                UpdateLayout();
            }));
        }

        // ---- state test hooks (--statetest) ------------------------------

        internal int DrawnIndex { get { return _pill.DrawnIndex; } }
        internal WakeMode Mode { get { return _pill.Mode; } }
        internal bool ArmedRowOpen { get { return _armedPanel.ActualHeight > 1; } }

        /// <summary>Move the deadline; ends with "then nothing" so a test never powers down.</summary>
        internal void DeadlineForTest(double secondsFromNow)
        {
            _settings.End = EndAction.Release;
            _deadline = DateTime.UtcNow.AddSeconds(secondsFromNow);
            _hasDeadline = true;
        }

        internal void TickForTest() { Tick(); }
        internal void SelectForTest(WakeMode mode) { _pill.Select(mode); }

        private static string FormatRemaining(double seconds)
        {
            int total = (int)Math.Ceiling(seconds);
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0) return h + ":" + m.ToString("00") + ":" + s.ToString("00");
            return m + ":" + s.ToString("00");
        }

        // ---- presentation helpers ---------------------------------------

        private void SetArmed(bool armed)
        {
            DoubleAnimation height = new DoubleAnimation(
                armed ? _armedHeight : 0, TimeSpan.FromMilliseconds(260));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            height.EasingFunction = ease;
            _armedPanel.BeginAnimation(HeightProperty, height);

            DoubleAnimation fade = new DoubleAnimation(
                armed ? 1.0 : 0.0, TimeSpan.FromMilliseconds(armed ? 300 : 150));
            _armedPanel.BeginAnimation(OpacityProperty, fade);
        }

        private void FlashCaption()
        {
            DoubleAnimationUsingKeyFrames anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            _caption.BeginAnimation(OpacityProperty, anim);
        }

        private static void Fade(UIElement element, double to)
        {
            element.BeginAnimation(OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
        }

        private void RefreshTray()
        {
            WakeMode mode = _pill.Mode;
            bool limited = _hasDeadline;
            double fraction = 1.0;
            string tip;

            if (mode == WakeMode.Auto)
            {
                tip = "Sleep Guard - Windows decides";
            }
            else
            {
                string what = mode == WakeMode.Screen ? "Screen and PC on" : "PC awake";
                if (limited)
                {
                    double left = (_deadline - DateTime.UtcNow).TotalSeconds;
                    if (left < 0) left = 0;
                    fraction = left / _settings.DurationSeconds;
                    tip = "Sleep Guard - " + what + ", " + FormatRemaining(left) + " left";
                }
                else
                {
                    tip = "Sleep Guard - " + what + ", no limit";
                }
            }

            _tray.Draw(mode, fraction, limited);
            _tray.SetTooltip(tip);
            _tray.SetShowLabel(IsVisible);

            if (_chip != null)
            {
                double secondsLeft = limited ? Math.Max(0, (_deadline - DateTime.UtcNow).TotalSeconds) : 0;
                _chip.Update(mode, fraction, limited, secondsLeft);
                _chip.ToolTip = tip.Replace("Sleep Guard - ", "Sleep Guard: ");
            }
        }

        // ---- taskbar flyout ----------------------------------------------

        private void ToggleFlyout()
        {
            if (_flyOpen) { HideFlyout(); return; }
            // A click elsewhere (the tray icon, say) closes the card through
            // Deactivated a moment before this runs: that click meant "close".
            if ((DateTime.UtcNow - _lastHide).TotalMilliseconds < 300) return;
            OpenFlyout();
        }

        /// <summary>
        /// Spring the card up out of the taskbar: right edge on the chip's right
        /// edge, bottom 12 px above the taskbar. It rises 28 px, overshoots a
        /// touch and settles, fading in over the first 220 ms. Same motion as
        /// the Today card (todo-app/desktop-card/taskbar.js).
        /// </summary>
        private void OpenFlyout()
        {
            _flyOpen = true;
            Rect area = SystemParameters.WorkArea;
            double w = CardWidth + 2 * ShadowRoom;
            double cardRight = (_chip.Running && _chip.IsVisible) ? _chip.Bounds.Right : area.Right - 12;
            if (cardRight > area.Right - 12) cardRight = area.Right - 12;
            double left = cardRight + ShadowRoom - w;
            if (left < area.Left) left = area.Left;
            Left = left;
            _flyBottom = area.Bottom - 12 + ShadowRoom;
            if (ActualHeight > 0) Top = _flyBottom - ActualHeight;

            Topmost = true;
            _card.BeginAnimation(OpacityProperty, null);
            _card.Opacity = 0;
            Show();
            Top = _flyBottom - ActualHeight;
            // He clicked the chip, so taking focus is his own action, and it is
            // what lets a click elsewhere close the card again.
            Activate();
            _tray.SetShowLabel(true);

            _lift.BeginAnimation(TranslateTransform.YProperty, SpringFrames(28, 0, 520));
            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            fade.EasingFunction = ease;
            _card.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>The reverse, quicker: sink 10 px and fade, then hide.</summary>
        private void HideFlyout()
        {
            if (!_flyOpen) return;
            _flyOpen = false;
            _lastHide = DateTime.UtcNow;

            DoubleAnimation drop = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(140));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseIn;
            drop.EasingFunction = ease;
            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += delegate { if (!_flyOpen) Hide(); };
            _lift.BeginAnimation(TranslateTransform.YProperty, drop);
            _card.BeginAnimation(OpacityProperty, fade);
            _tray.SetShowLabel(false);
        }

        /// <summary>
        /// A damped spring as key frames: about 7% overshoot, settled in ~0.45 s.
        /// WPF's BackEase front-loads the motion so much it reads as a pop.
        /// </summary>
        private static DoubleAnimationUsingKeyFrames SpringFrames(double from, double to, int ms)
        {
            const double zeta = 0.65, omega = 14;
            double wd = omega * Math.Sqrt(1 - zeta * zeta);
            DoubleAnimationUsingKeyFrames a = new DoubleAnimationUsingKeyFrames();
            for (int t = 0; t <= ms; t += 16)
            {
                double s = t / 1000.0;
                double p = 1 - Math.Exp(-zeta * omega * s) * (Math.Cos(wd * s) + (zeta * omega / wd) * Math.Sin(wd * s));
                a.KeyFrames.Add(new LinearDoubleKeyFrame(from + (to - from) * p, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
            }
            a.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            return a;
        }

        /// <summary>The tray menu's "Show in the taskbar": switch live, no restart.</summary>
        private void SetTaskbarMode(bool on)
        {
            _settings.Taskbar = on;
            _settings.Save();
            _tray.SetTaskbarChecked(on);
            _lift.BeginAnimation(TranslateTransform.YProperty, null);
            _card.BeginAnimation(OpacityProperty, null);
            _lift.Y = 0;
            _flyOpen = false;
            if (on)
            {
                Hide();
                _card.Opacity = 0;
                Topmost = true;
                _chip.Start();
            }
            else
            {
                _chip.Stop();
                _card.Opacity = 1;
                Topmost = _settings.Topmost;
                Place();
                ShowWidget();
            }
            RefreshTray();
        }

        // ---- window plumbing ---------------------------------------------

        private void Place()
        {
            double w = CardWidth + 2 * ShadowRoom;
            Rect area = SystemParameters.WorkArea;

            // The saved spot is checked against every monitor, not just the
            // primary one: he keeps the card on the portrait screen, whose
            // coordinates are negative above the main desktop.
            Rect all = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

            if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top)
                && _settings.Left > all.Left - w + 60 && _settings.Left < all.Right - 60
                && _settings.Top > all.Top - 10 && _settings.Top < all.Bottom - 40)
            {
                Left = _settings.Left;
                Top = _settings.Top;
                return;
            }

            Left = area.Right - w - 12;
            Top = area.Bottom - 310;      // refined in Loaded, once the real height is known
            _placedByDefault = true;
        }

        /// <summary>
        /// The top edge is the anchor: opening or closing the timer row grows
        /// the card downwards and never shifts what he just clicked. The only
        /// exception is a card parked so low that the taller version would run
        /// off the bottom of the screen, which pulls it up by just enough.
        /// </summary>
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // The flyout is anchored at the bottom instead: the timer row grows
            // it upwards, away from the taskbar.
            if (TaskbarOn)
            {
                if (_flyOpen) Top = _flyBottom - ActualHeight;
                return;
            }
            Rect area = CurrentWorkArea();
            if (Top + ActualHeight <= area.Bottom) return;

            double top = area.Bottom - ActualHeight;
            if (top < area.Top) top = area.Top;
            Top = top;
        }

        /// <summary>
        /// Usable area of the monitor the card currently sits on. He keeps it
        /// on the portrait screen, where the primary monitor's bounds would be
        /// the wrong yardstick entirely.
        /// </summary>
        private Rect CurrentWorkArea()
        {
            try
            {
                System.Drawing.Rectangle box = new System.Drawing.Rectangle(
                    (int)Left, (int)Top, (int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight));
                System.Drawing.Rectangle wa =
                    System.Windows.Forms.Screen.FromRectangle(box).WorkingArea;
                return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
            }
            catch { return SystemParameters.WorkArea; }
        }

        /// <summary>
        /// A left click on the tray icon. The card is a normal window by default,
        /// so a covered card comes to the front (unfocused) instead of hiding;
        /// only a card pinned above other windows hides on a click.
        /// </summary>
        private void OnTrayClick()
        {
            if (TaskbarOn) { ToggleFlyout(); return; }
            if (!IsVisible) { ShowWidget(); return; }
            if (_settings.Topmost) { HideWidget(); return; }
            Topmost = true;    // the WPF way to raise a window without activating it
            Topmost = false;
        }

        private void ToggleVisible()
        {
            if (TaskbarOn) { ToggleFlyout(); return; }
            if (IsVisible) HideWidget();
            else ShowWidget();
        }

        private void ShowWidget()
        {
            Show();
            Activate();
            _tray.SetShowLabel(true);
        }

        /// <summary>
        /// Store where the card sits. The top edge is the anchor and the timer
        /// row only ever grows downwards, so the current top is already the
        /// value the next launch should start from.
        /// </summary>
        private void SavePosition()
        {
            // The flyout's spot is the chip's, not his: keep the desktop card's.
            if (TaskbarOn) return;
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }

        private void HideWidget()
        {
            if (TaskbarOn) { HideFlyout(); return; }
            SavePosition();
            Hide();
            _tray.SetShowLabel(false);
        }

        private void Quit()
        {
            _quitting = true;
            _timer.Stop();
            Native.Apply(WakeMode.Auto);     // give the normal timeouts back
            SavePosition();
            _tray.Dispose();
            _layer.Dispose();
            _chip.Stop();
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Application.Current.Shutdown();
        }

        private static int IndexOfDuration(int seconds)
        {
            for (int i = 0; i < Durations.Length; i++)
                if (Durations[i] == seconds) return i;
            return 1;
        }
    }
}
