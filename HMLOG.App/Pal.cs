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
        public static Color Line { get { return Dark ? Color.FromRgb(58, 60, 66) : Color.FromRgb(214, 217, 224); } }
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
    }
}
