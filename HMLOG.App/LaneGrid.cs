using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using System;
using System.Globalization;

namespace HMLOG.App
{
    // text helpers for the custom-drawn controls (LaneGrid, WaveView, RegGrid, TiaView)
    static class Gfx
    {
        public static FormattedText Ft(string t, IBrush b, double size = Table.FontSize, bool bold = false, bool mono = true)
        {
            return new FormattedText(t ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(mono ? Table.Mono : FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, b);
        }

        // text left aligned in a box, vertically centered, cut with an ellipsis
        public static void Text(DrawingContext dc, string t, Rect r, Color c, double size = Table.FontSize, bool bold = false, bool mono = true, int align = 0)
        {
            if (r.Width < 2 || t.Length == 0) return;
            var ft = Ft(t, Pal.Br(c), size, bold, mono);
            ft.MaxTextWidth = r.Width;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            double x = align == 0 ? r.X : align == 1 ? r.X + (r.Width - ft.Width) / 2 : r.Right - ft.Width;
            dc.DrawText(ft, new Point(x, r.Y + (r.Height - ft.Height) / 2));
        }

        public static double Width(string t, double size = Table.FontSize, bool bold = false, bool mono = true)
        {
            return Ft(t, Brushes.Black, size, bold, mono).Width;
        }
    }

    // FRL lane grid: the LTP requests of every lane, one column per change
    //   0 lane done (green), 1..8 pattern (blue), E FFE up (amber), F lower rate (red)
    class LaneGrid : ScrollViewer
    {
        readonly Canvas2 cv = new Canvas2();

        public LaneGrid()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Content = cv;
        }

        public void Set(FrlAttempt a) { cv.Set(a); }

        class Canvas2 : Control
        {
            FrlAttempt t;
            const double RH = 26, CW = 40, LW = 58;

            public void Set(FrlAttempt a)
            {
                t = a;
                InvalidateMeasure();
                InvalidateVisual();
            }

            protected override Size MeasureOverride(Size availableSize)
            {
                int cols = t != null ? t.Ltp.Count : 0;
                return new Size(Math.Max(420, LW + cols * CW + 3 * RH), RH * 10);
            }

            static Color Gray { get { return Pal.Muted; } }

            public override void Render(DrawingContext dc)
            {
                dc.FillRectangle(Pal.Br(Pal.Surface), new Rect(Bounds.Size));
                double x0 = RH / 2, y = RH / 3;
                if (t == null)
                {
                    Gfx.Text(dc, "Select a training to see the LTP requests of each lane here", new Rect(x0, y, 4000, RH), Gray);
                    return;
                }
                string res = t.Result == 1 ? "OK" : t.Result == 0 ? "FAILED" : t.End.Length > 0 ? "aborted" : "in progress";
                string dur = t.EndUs >= 0 ? string.Format(CultureInfo.InvariantCulture, " · {0:F1} ms", (t.EndUs - t.StartUs) / 1000.0) : "";
                Gfx.Text(dc, string.Format("FRL training #{0} · {1} · FFE level {2} · {3}{4}{5}", t.No, Decoder.Rate(t.Rate), t.FfeLevels, res, dur,
                         t.End.Length > 0 ? " · " + t.End : ""), new Rect(x0, y, 4000, RH),
                         t.Result == 1 ? Pal.Good : t.Result == 0 ? Pal.Red : Gray, bold: true);
                y += RH;
                if (t.Ltp.Count == 0)
                {
                    Gfx.Text(dc, "LTP requests not read yet (SCDC 0x41 / 0x42)", new Rect(x0, y, 4000, RH), Gray);
                    return;
                }
                int lanes = t.Ltp[0].Length;
                Gfx.Text(dc, "Step", new Rect(x0, y, LW, RH), Gray);
                for (int k = 0; k < t.Ltp.Count; k++)
                    Gfx.Text(dc, (k + 1).ToString(), new Rect(x0 + LW + k * CW, y, CW, RH), Gray, align: 1);
                y += RH;
                double gap = 2;
                for (int l = 0; l < lanes; l++)
                {
                    Gfx.Text(dc, "Ln" + l, new Rect(x0, y, LW, RH), Pal.Text);
                    for (int k = 0; k < t.Ltp.Count; k++)
                    {
                        char c = l < t.Ltp[k].Length ? t.Ltp[k][l] : '?';
                        Color b, f;
                        Pick(c, out b, out f);
                        var r = new Rect(x0 + LW + k * CW + gap, y + gap, CW - 2 * gap, RH - 2 * gap);
                        dc.FillRectangle(Pal.Br(b), r);
                        Gfx.Text(dc, c.ToString(), r, f, align: 1);
                    }
                    y += RH;
                }
                Gfx.Text(dc, "Reads", new Rect(x0, y, LW, RH), Gray);
                for (int k = 0; k < t.Ltp.Count; k++)
                    Gfx.Text(dc, "×" + t.LtpRep[k], new Rect(x0 + LW + k * CW, y, CW, RH), Gray, align: 1);
                y += RH + RH / 3;
                double x = x0;
                foreach (var p in new[] { ('0', "0", "pass"), ('1', "1-8", "LTP pattern"), ('E', "E", "FFE up"), ('F', "F", "rate down") })
                {
                    Color b, f;
                    Pick(p.Item1, out b, out f);
                    double w = Gfx.Width(p.Item2) + RH / 2;
                    var r = new Rect(x, y + gap, w, RH - 2 * gap);
                    dc.FillRectangle(Pal.Br(b), r);
                    Gfx.Text(dc, p.Item2, r, f, align: 1);
                    x = r.Right + RH / 4;
                    double tw = Gfx.Width(p.Item3);
                    Gfx.Text(dc, p.Item3, new Rect(x, y, tw + 2, RH), Gray);
                    x += tw + RH;
                }
            }

            static void Pick(char c, out Color b, out Color f)
            {
                if (c == '0') { b = Pal.Bg(225, 243, 222); f = Pal.Fg(39, 100, 17); }
                else if (c == 'E') { b = Pal.Bg(250, 236, 210); f = Pal.Fg(133, 79, 11); }
                else if (c == 'F') { b = Pal.Bg(252, 228, 228); f = Pal.Fg(163, 45, 45); }
                else if (c >= '1' && c <= '8') { b = Pal.Bg(230, 241, 251); f = Pal.Fg(24, 95, 165); }
                else { b = Pal.Bg(238, 238, 238); f = Pal.Fg(120, 120, 120); }
            }
        }
    }
}
