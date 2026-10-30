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

        public MainWindow()
        {
            _settings = Settings.Load();

            // ---- window chrome ------------------------------------------
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = CardWidth + 2 * ShadowRoom;
            Topmost = _settings.Topmost;
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

            StackPanel content = new StackPanel();
            content.Margin = new Thickness(16, 13, 16, 13);
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
            _tray.QuitRequested += Quit;
            _tray.TopmostToggled += OnTopmostToggled;
            _tray.ExtendRequested += Extend30;
            _tray.SetTopmostChecked(_settings.Topmost);

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
                Finish();
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

        private void Finish()
        {
            _hasDeadline = false;
            _timer.Stop();
            EndAction action = _settings.End;

            _pill.SetSilently(WakeMode.Auto);
            _caption.Text = _autoCaption;
            SetArmed(false);
            Native.Apply(WakeMode.Auto);
            RefreshTray();

            if (action == EndAction.Hibernate) Native.Hibernate();
            else if (action == EndAction.Sleep) Native.Sleep();
        }

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

        private void ToggleVisible()
        {
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
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }

        private void HideWidget()
        {
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
