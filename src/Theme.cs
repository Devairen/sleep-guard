using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SleepGuard
{
    /// <summary>
    /// The same palette and type as the Claude Usage widget, so the two sit
    /// together on the desktop as one family.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Accent = Rgb(0xFF, 0x88, 0x39);
        public static readonly Color AccentDeep = Rgb(0xFF, 0x6A, 0x1A);
        public static readonly Color Card = Argb(0xF2, 0x0E, 0x0E, 0x0F);
        public static readonly Color Ink = Rgb(0x0A, 0x0A, 0x0A);
        public static readonly Color Text = Rgb(0xF4, 0xF4, 0xF5);
        public static readonly Color TextSecondary = Argb(0xA8, 0xF4, 0xF4, 0xF5);
        public static readonly Color TextTertiary = Argb(0x6B, 0xF4, 0xF4, 0xF5);
        public static readonly Color Track = Argb(0x16, 0xFF, 0xFF, 0xFF);
        public static readonly Color ThumbNeutral = Argb(0x26, 0xFF, 0xFF, 0xFF);
        public static readonly Color Hairline = Argb(0x14, 0xFF, 0xFF, 0xFF);

        public static readonly FontFamily Face =
            new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif");

        public static readonly FontFamily Numerals =
            new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif");

        /// <summary>A brush that can still be animated (never frozen).</summary>
        public static SolidColorBrush Live(Color c)
        {
            return new SolidColorBrush(c);
        }

        public static SolidColorBrush Fixed(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Color Rgb(byte r, byte g, byte b)
        {
            return Color.FromRgb(r, g, b);
        }

        private static Color Argb(byte a, byte r, byte g, byte b)
        {
            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>Tabular figures, so a ticking countdown never jitters.</summary>
        public static void UseTabularFigures(DependencyObject o)
        {
            Typography.SetNumeralAlignment(o, FontNumeralAlignment.Tabular);
        }
    }
}
