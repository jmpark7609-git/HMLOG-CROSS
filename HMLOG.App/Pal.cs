using Avalonia.Media;
using System;

namespace HMLOG.App
{
    // Colors of the window. Every color is written for the light theme (as in the Windows version);
    // in the dark theme text colors are lightened and background tints darkened (Pal.Fg / Pal.Bg).
    static class Pal
    {
        public static bool Dark;

        static byte B(double v) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v))); }

        public static Color Fg(int r, int g, int b)
        {
            if (!Dark) return Color.FromRgb((byte)r, (byte)g, (byte)b);
            return Color.FromRgb(B(r * 0.45 + 140), B(g * 0.45 + 140), B(b * 0.45 + 140));
        }

        public static Color Bg(int r, int g, int b)
        {
            if (!Dark) return Color.FromRgb((byte)r, (byte)g, (byte)b);
            return Color.FromRgb(B(r * 0.17 + 24), B(g * 0.17 + 24), B(b * 0.17 + 28));
        }

        public static Color Canvas { get { return Dark ? Color.FromRgb(24, 25, 28) : Color.FromRgb(240, 241, 244); } }
        public static Color Surface { get { return Dark ? Color.FromRgb(32, 33, 37) : Colors.White; } }
        public static Color Header { get { return Dark ? Color.FromRgb(42, 44, 50) : Color.FromRgb(236, 238, 242); } }
        public static Color Line { get { return Dark ? Color.FromRgb(80, 88, 100) : Color.FromRgb(214, 217, 224); } }
        public static Color Text { get { return Dark ? Color.FromRgb(224, 226, 230) : Color.FromRgb(30, 30, 30); } }
        public static Color Muted { get { return Dark ? Color.FromRgb(168, 172, 180) : Color.FromRgb(100, 104, 112); } }
        public static Color Sel { get { return Dark ? Color.FromRgb(38, 79, 120) : Color.FromRgb(204, 228, 247); } }
        public static Color Accent { get { return Color.FromRgb(0, 120, 212); } }
        public static Color Zebra { get { return Dark ? Color.FromRgb(31, 35, 41) : Color.FromRgb(247, 248, 250); } }
        public static Color Faint { get { return Dark ? Color.FromRgb(109, 116, 128) : Color.FromRgb(138, 147, 163); } }

        public static Color Red { get { return Fg(200, 30, 30); } }
        public static Color RedBack { get { return Bg(252, 225, 225); } }
        public static Color Good { get { return Fg(20, 130, 50); } }
        public static Color GoodBack { get { return Bg(225, 245, 230); } }
        public static Color Warn { get { return Fg(190, 120, 0); } }
        public static Color ChgBack { get { return Dark ? Color.FromRgb(96, 80, 20) : Color.FromRgb(255, 232, 140); } }
        public static Color HitBack { get { return Dark ? Color.FromRgb(84, 74, 24) : Color.FromRgb(255, 246, 190); } }
        public static Color BandText { get { return Fg(25, 85, 160); } }
        public static Color BandBack { get { return Bg(226, 238, 252); } }
        public static Color ScdcText { get { return Fg(20, 85, 165); } }
        public static Color ScdcBack { get { return Bg(221, 235, 252); } }
        public static Color HdcpText { get { return Fg(95, 55, 165); } }
        public static Color HdcpBack { get { return Bg(236, 228, 250); } }
        public static Color EvtText { get { return Fg(140, 40, 150); } }
        public static Color CecText { get { return Fg(0, 121, 128); } }
        public static Color CecBack { get { return Bg(224, 242, 243); } }
        public static Color EdidText { get { return Fg(0, 115, 100); } }
        public static Color EdidBack { get { return Bg(214, 241, 236); } }
        public static Color EvtBack { get { return Bg(246, 228, 248); } }
        public static Color ResetText { get { return Fg(170, 90, 0); } }
        public static Color ResetBack { get { return Bg(255, 238, 214); } }
        public static Color I2cText { get { return Fg(90, 90, 90); } }
        public static Color I2cBack { get { return Bg(232, 232, 232); } }

        // a Color of the Core (System.Drawing, light theme) as Avalonia color of the current theme
        public static Color Of(System.Drawing.Color c)
        {
            return Fg(c.R, c.G, c.B);
        }

        public static Color OfBack(System.Drawing.Color c)
        {
            return Bg(c.R, c.G, c.B);
        }

        public static IBrush Br(Color c) { return new SolidColorBrush(c); }

        // WCAG contrast ratio of two colors
        public static double Contrast(Color a, Color b)
        {
            double l1 = Lum(a), l2 = Lum(b);
            if (l1 < l2) { double t = l1; l1 = l2; l2 = t; }
            return (l1 + 0.05) / (l2 + 0.05);
        }

        static double Lum(Color c)
        {
            Func<int, double> ch = v => { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        // a text color with at least min contrast on back: the color itself, else the same hue made darker (light
        // background) or lighter (dark background) step by step, else the theme text color
        public static Color Ensure(Color fore, Color back, double min)
        {
            if (Contrast(fore, back) >= min) return fore;
            double h, s, l;
            ToHsl(fore, out h, out s, out l);
            double step = Lum(back) > 0.4 ? -0.02 : 0.02;
            for (int i = 0; i < 45; i++)
            {
                l = Math.Max(0, Math.Min(1, l + step));
                Color c = FromHsl(h, s, l);
                if (Contrast(c, back) >= min) return c;
            }
            if (Contrast(Text, back) >= min) return Text;
            return Contrast(Colors.White, back) >= Contrast(Colors.Black, back) ? Colors.White : Colors.Black;
        }

        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            double d = max - min;
            if (d < 1e-9) { h = 0; s = 0; return; }
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (max == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }

        static Color FromHsl(double h, double s, double l)
        {
            if (s < 1e-9) { byte v = B(l * 255); return Color.FromRgb(v, v, v); }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return Color.FromRgb(B(Hue(p, q, h + 1.0 / 3) * 255), B(Hue(p, q, h) * 255), B(Hue(p, q, h - 1.0 / 3) * 255));
        }

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
