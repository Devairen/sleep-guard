using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SleepGuard
{
    /// <summary>
    /// One sliding control for the whole decision. Three stops on a single
    /// track, left to right, each allowing less sleep than the last:
    /// Auto, then the PC awake, then the screen awake as well. Screen implies
    /// PC by construction, because the thumb can only be in one place.
    /// </summary>
    internal sealed class StatePill : UserControl
    {
        private const double PillWidth = 240;
        private const double PillHeight = 42;
        private const double Pad = 3;
        private const double SegWidth = (PillWidth - 2 * Pad) / 3;
        private const double ThumbHeight = PillHeight - 2 * Pad;

        private readonly Border _thumb;
        private readonly TranslateTransform _slide;
        private readonly SolidColorBrush _thumbFill;
        private readonly DropShadowEffect _glow;
        private readonly SolidColorBrush[] _segmentInk = new SolidColorBrush[3];

        private WakeMode _mode = WakeMode.Auto;

        public event Action<WakeMode> ModeChanged;

        public WakeMode Mode { get { return _mode; } }

        public StatePill()
        {
            Width = PillWidth;
            Height = PillHeight;

            _thumbFill = Theme.Live(Theme.ThumbNeutral);

            _glow = new DropShadowEffect();
            _glow.Color = Theme.Accent;
            _glow.BlurRadius = 18;
            _glow.ShadowDepth = 0;
            _glow.Opacity = 0;

            _slide = new TranslateTransform(0, 0);

            _thumb = new Border();
            _thumb.Width = SegWidth;
            _thumb.Height = ThumbHeight;
            _thumb.CornerRadius = new CornerRadius(ThumbHeight / 2);
            _thumb.Background = _thumbFill;
            _thumb.HorizontalAlignment = HorizontalAlignment.Left;
            _thumb.VerticalAlignment = VerticalAlignment.Center;
            _thumb.RenderTransform = _slide;
            _thumb.Effect = _glow;
            _thumb.IsHitTestVisible = false;

            UniformGrid segments = new UniformGrid();
            segments.Rows = 1;
            segments.Columns = 3;
            segments.Children.Add(BuildSegment(0, Icons.Moon(), "Auto"));
            segments.Children.Add(BuildSegment(1, Icons.Bolt(), "Awake"));
            segments.Children.Add(BuildSegment(2, Icons.Monitor(), "Screen"));

            Grid inner = new Grid();
            inner.Children.Add(_thumb);
            inner.Children.Add(segments);

            Border track = new Border();
            track.Width = PillWidth;
            track.Height = PillHeight;
            track.CornerRadius = new CornerRadius(PillHeight / 2);
            track.Background = Theme.Fixed(Theme.Track);
            track.Padding = new Thickness(Pad);
            track.Child = inner;

            Content = track;
            Paint(WakeMode.Auto, false);
        }

        private UIElement BuildSegment(int index, Geometry icon, string label)
        {
            SolidColorBrush ink = Theme.Live(Theme.TextTertiary);
            _segmentInk[index] = ink;

            Path glyph = new Path();
            glyph.Data = icon;
            glyph.Fill = ink;
            glyph.Stretch = Stretch.Uniform;
            glyph.Width = 14;
            glyph.Height = 14;
            glyph.VerticalAlignment = VerticalAlignment.Center;

            TextBlock text = new TextBlock();
            text.Text = label;
            text.FontFamily = Theme.Face;
            text.FontSize = 11.5;
            text.FontWeight = FontWeights.SemiBold;
            text.Foreground = ink;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(6, 0, 0, 0);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.HorizontalAlignment = HorizontalAlignment.Center;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(glyph);
            row.Children.Add(text);

            Border hit = new Border();
            hit.Background = Brushes.Transparent;
            hit.Cursor = Cursors.Hand;
            hit.Child = row;

            WakeMode target = (WakeMode)index;
            hit.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;              // never start a window drag from the pill
                Select(target);
            };

            return hit;
        }

        /// <summary>Move the thumb and tell the window. Ignored if already there.</summary>
        public void Select(WakeMode mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            Paint(mode, true);
            if (ModeChanged != null) ModeChanged(mode);
        }

        /// <summary>Move the thumb without raising ModeChanged (used at startup).</summary>
        public void SetSilently(WakeMode mode)
        {
            _mode = mode;
            Paint(mode, false);
        }

        private void Paint(WakeMode mode, bool animate)
        {
            int index = (int)mode;
            double x = index * SegWidth;

            Color fill = mode == WakeMode.Auto ? Theme.ThumbNeutral
                       : mode == WakeMode.Awake ? Theme.Accent
                       : Theme.AccentDeep;

            double glow = mode == WakeMode.Auto ? 0.0
                        : mode == WakeMode.Awake ? 0.35
                        : 0.65;

            Color activeInk = mode == WakeMode.Auto ? Theme.Text : Theme.Ink;

            if (!animate)
            {
                _slide.X = x;
                _thumbFill.Color = fill;
                _glow.Opacity = glow;
                for (int i = 0; i < 3; i++)
                    _segmentInk[i].Color = (i == index) ? activeInk : Theme.TextTertiary;
                return;
            }

            // A small overshoot on the slide makes the control feel physical
            // without turning into a bounce.
            BackEase ease = new BackEase();
            ease.EasingMode = EasingMode.EaseOut;
            ease.Amplitude = 0.28;

            DoubleAnimation slide = new DoubleAnimation(x, TimeSpan.FromMilliseconds(340));
            slide.EasingFunction = ease;
            _slide.BeginAnimation(TranslateTransform.XProperty, slide);

            ColorAnimation tint = new ColorAnimation(fill, TimeSpan.FromMilliseconds(220));
            _thumbFill.BeginAnimation(SolidColorBrush.ColorProperty, tint);

            DoubleAnimation halo = new DoubleAnimation(glow, TimeSpan.FromMilliseconds(260));
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, halo);

            for (int i = 0; i < 3; i++)
            {
                Color target = (i == index) ? activeInk : Theme.TextTertiary;
                ColorAnimation inkAnim = new ColorAnimation(target, TimeSpan.FromMilliseconds(200));
                _segmentInk[i].BeginAnimation(SolidColorBrush.ColorProperty, inkAnim);
            }
        }
    }

    /// <summary>Solid 24x24 glyphs, drawn so they read at 14 pixels.</summary>
    internal static class Icons
    {
        /// A crescent built by cutting one disc out of another: always a
        /// clean shape, unlike a hand-written two-arc path.
        public static Geometry Moon()
        {
            EllipseGeometry disc = new EllipseGeometry(new Point(12, 12), 9.5, 9.5);
            EllipseGeometry bite = new EllipseGeometry(new Point(17.0, 7.6), 8.2, 8.2);
            CombinedGeometry g = new CombinedGeometry(GeometryCombineMode.Exclude, disc, bite);
            g.Freeze();
            return g;
        }

        public static Geometry Bolt()
        {
            Geometry g = Geometry.Parse("M13,2 L4,13 L10,13 L9,22 L18,11 L12,11 Z");
            g.Freeze();
            return g;
        }

        public static Geometry Monitor()
        {
            Geometry g = Geometry.Parse(
                "M3.2,4 H20.8 A1.2,1.2 0 0 1 22,5.2 V14.8 A1.2,1.2 0 0 1 20.8,16 " +
                "H13.1 V18.4 H16.4 A0.8,0.8 0 0 1 16.4,20 H7.6 A0.8,0.8 0 0 1 7.6,18.4 " +
                "H10.9 V16 H3.2 A1.2,1.2 0 0 1 2,14.8 V5.2 A1.2,1.2 0 0 1 3.2,4 Z");
            g.Freeze();
            return g;
        }
    }
}
