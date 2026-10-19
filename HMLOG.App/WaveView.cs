using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HMLOG.App
{
    // The CEC line and the sink HPD as digital waveforms with a history of up to 60 s (custom Render).
    // The FPGA puts every CEC edge into the log as an event (0A falls, 0B rises, time stamp in us), HPD edges are
    // the events 01 / 02. Decoder keeps the edges (CecEdges / HpdEdges), CecDecoder turns the CEC edges into
    // frames (start bit, header, data, EOM, ACK) shown on the waveform. Port of WaveView of the Windows version.
    class WaveView : Control
    {
        public Decoder Dec;
        public double V0, VL = 10e6;                           // view start (FPGA us) and length (us): 0.5 ms .. 60 s
        public bool Live = true;                               // the right edge follows the newest time
        public double CurA = double.NaN, CurB = double.NaN;    // cursors (FPGA us)
        public List<CecFrame> Frames = new List<CecFrame>();
        public List<CecItem> Items = new List<CecItem>();
        public event Action CursorsChanged, ViewChanged;
        public const double MaxSpan = 60e6, MinSpan = 500;

        static readonly string[] Names = { "CEC", "HPD" };
        static readonly double[] Weight = { 3.0, 1.0 };
        const double LaneH = 22, SmallSize = 12.5;
        int decVer = -1;
        double hoverUs = double.NaN;
        bool drag, moved;
        double dragX, dragV0;

        public WaveView()
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            Focusable = true;
            ClipToBounds = true;
            MinHeight = 200;
        }

        static Color ChColor(int c) { return c == 0 ? Pal.Accent : Pal.Fg(40, 96, 200); }
        static Color LineStrong { get { return Pal.Dark ? Color.FromRgb(96, 100, 110) : Color.FromRgb(170, 174, 182); } }

        // every tick: new edges -> decode again, follow the newest time
        public void Refresh2()
        {
            if (Dec == null) return;
            if (Dec.EdgeVer != decVer)
            {
                decVer = Dec.EdgeVer;
                CecDecoder.Decode(Dec.CecEdges, Dec.LastUs, out Frames, out Items);
            }
            if (Dec.LastUs != lastUsSeen) { lastUsSeen = Dec.LastUs; stampMs = clock.ElapsedMilliseconds; }
            if (Live) V0 = Newest - VL * 0.98;
            InvalidateVisual();
        }

        // the newest time: the last FPGA time seen, moved on with the PC clock while the board is connected, so the view keeps
        // scrolling between two log lines instead of jumping when one arrives
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long lastUsSeen = long.MinValue, stampMs;
        public bool Running;
        public double Newest
        {
            get
            {
                if (Dec == null) return 0;
                if (!Running || !Live) return Dec.LastUs;
                return Dec.LastUs + Math.Min(2000, clock.ElapsedMilliseconds - stampMs) * 1000.0;
            }
        }

        public void SetSpan(double us)
        {
            double c = Live ? Newest - VL * 0.48 : V0 + VL / 2;
            VL = Math.Max(MinSpan, Math.Min(MaxSpan, us));
            V0 = Live ? Newest - VL * 0.98 : c - VL / 2;
            Fire();
        }

        public void Zoom(double factor, double centerUs)
        {
            double nl = Math.Max(MinSpan, Math.Min(MaxSpan, VL / factor));
            double f = (centerUs - V0) / VL;
            V0 = centerUs - f * nl;
            VL = nl;
            if (Live) V0 = Newest - VL * 0.98;
            Fire();
        }

        public void CenterOn(double us, double span)
        {
            Live = false;
            VL = Math.Max(MinSpan, Math.Min(MaxSpan, span));
            V0 = us - VL / 2;
            Fire();
        }

        public void FitAll()
        {
            Live = true;
            double first = Dec != null && Dec.CecEdges.Count > 0 ? Dec.CecEdges[0].Key : Newest;
            if (Dec != null && Dec.HpdEdges.Count > 0) first = Math.Min(first, Dec.HpdEdges[0].Key);
            VL = Math.Max(1e6, Math.Min(MaxSpan, Newest - first + 1e5));
            V0 = Newest - VL * 0.98;
            Fire();
        }

        void Fire() { InvalidateVisual(); if (ViewChanged != null) ViewChanged(); }

        // ---------- geometry ----------
        double Rel(double us) { return us - Newest; }

        Rect PlotRect(Size sz)
        {
            double l = 58, t = 6 + 2 * LaneH, r = sz.Width - 10, b = sz.Height - 26;
            return new Rect(l, t, Math.Max(10, r - l), Math.Max(10, b - t));
        }

        double X(double us, Rect pr) { return pr.Left + (us - V0) / VL * pr.Width; }
        double UsAt(double x, Rect pr) { return V0 + (x - pr.Left) / pr.Width * VL; }

        static void RowBounds(int row, Rect pr, out double top, out double h)
        {
            double sum = 0;
            for (int i = 0; i < Weight.Length; i++) sum += Weight[i];
            double y = pr.Top;
            for (int i = 0; i < row; i++) y += pr.Height * Weight[i] / sum;
            top = y;
            h = pr.Height * Weight[row] / sum;
        }

        static string Num(double v, string f) { return v.ToString(f, CultureInfo.InvariantCulture); }

        static string FmtRel(double us, double step)
        {
            if (Math.Abs(us) < step * 0.01) return "0";
            if (step >= 1e6) return Num(us / 1e6, "0.###") + " s";
            if (step >= 1000) return Num(us / 1000, "0.###") + " ms";
            return Num(us, "0.###") + " us";
        }

        public static string FmtT(double us)
        {
            double a = Math.Abs(us);
            string s = a >= 1e6 ? Num(us / 1e6, "0.###") + " s" : a >= 1000 ? Num(us / 1000, "0.###") + " ms" : Num(us, "0.##") + " us";
            return us > 0 ? "+" + s : s;
        }

        public static string FmtDelta(double us)
        {
            string s = us >= 1e6 ? Num(us / 1e6, "0.###") + " s" : us >= 1000 ? Num(us / 1000, "0.###") + " ms" : Num(us, "0.##") + " us";
            return "Δ " + s;
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double m = raw / p;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        }

        public string CursorText()
        {
            string a = double.IsNaN(CurA) ? "-" : FmtT(Rel(CurA));
            string b = double.IsNaN(CurB) ? "-" : FmtT(Rel(CurB));
            return a + "  /  " + b;
        }

        public string DeltaText()
        {
            if (double.IsNaN(CurA) || double.IsNaN(CurB)) return "-";
            return FmtDelta(Math.Abs(CurB - CurA)).Substring(2);
        }

        // level of an edge list at a time (before the first edge: the opposite of its level)
        static int LevelAt(List<KeyValuePair<long, int>> l, double us, int none)
        {
            if (l.Count == 0) return none;
            int lo = 0, hi = l.Count - 1, r = -1;
            while (lo <= hi) { int m = (lo + hi) / 2; if (l[m].Key <= us) { r = m; lo = m + 1; } else hi = m - 1; }
            return r >= 0 ? l[r].Value : 1 - l[0].Value;
        }

        static int FirstAfter(List<KeyValuePair<long, int>> l, double us)
        {
            int lo = 0, hi = l.Count - 1, r = l.Count;
            while (lo <= hi) { int m = (lo + hi) / 2; if (l[m].Key > us) { r = m; hi = m - 1; } else lo = m + 1; }
            return r;
        }

        // ---------- painting ----------
        public override void Render(DrawingContext dc) { Draw(dc, Bounds.Size, true); }

        // the waveform as a PNG file
        public void SavePng(string path)
        {
            var px = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using (var bmp = new RenderTargetBitmap(px, new Vector(96, 96)))
            {
                using (var dc = bmp.CreateDrawingContext()) Draw(dc, Bounds.Size, false);
                bmp.Save(path);
            }
        }

        static void Poly(DrawingContext dc, List<Point> pts, Pen pen)
        {
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
        }

        List<Point> Steps(List<KeyValuePair<long, int>> ed, int none, double yH, double yL, Rect pr)
        {
            double ve = V0 + VL;
            var pts = new List<Point>();
            int lvl = LevelAt(ed, V0, none);
            pts.Add(new Point(pr.Left - 2, lvl != 0 ? yH : yL));
            for (int i = FirstAfter(ed, V0); i < ed.Count && ed[i].Key <= ve; i++)
            {
                double x = X(ed[i].Key, pr);
                pts.Add(new Point(x, lvl != 0 ? yH : yL));
                lvl = ed[i].Value;
                pts.Add(new Point(x, lvl != 0 ? yH : yL));
            }
            pts.Add(new Point(pr.Right + 2, lvl != 0 ? yH : yL));
            return pts;
        }

        void Draw(DrawingContext dc, Size size, bool live)
        {
            dc.FillRectangle(Pal.Br(Pal.Surface), new Rect(size));
            if (Dec == null) return;
            Rect pr = PlotRect(size);
            double ve = V0 + VL;
            double usPerPx = VL / pr.Width;
            var linePen = new Pen(Pal.Br(Pal.Line), 1);
            var strongPen = new Pen(Pal.Br(LineStrong), 1);
            for (int row = 0; row < 2; row++)
            {
                double top, h;
                RowBounds(row, pr, out top, out h);
                if (row % 2 == 0) dc.FillRectangle(Pal.Br(Pal.Canvas), new Rect(0, top, pr.Right - 0 + 6, h));
                Gfx.Text(dc, Names[row], new Rect(6, top, 50, Math.Min(h, 26)), ChColor(row), 14, true, false);
                if (row == 1 && Dec.Hpd >= 0) Gfx.Text(dc, Dec.Hpd == 1 ? "HIGH" : "LOW", new Rect(6, top + 22, 50, 20), Dec.Hpd == 1 ? Pal.Good : Pal.Warn, 12, true, false);
            }
            // time grid and ruler (0 = the newest time)
            double stepUs = NiceStep(VL / Math.Max(2, pr.Width / 90));
            double firstU = Math.Ceiling(Rel(V0) / stepUs) * stepUs;
            for (double u = firstU; ; u += stepUs)
            {
                double x = X(Newest + u, pr);
                if (x > pr.Right + 1) break;
                if (x < pr.Left - 1) continue;
                dc.DrawLine(linePen, new Point(x, pr.Top), new Point(x, pr.Bottom));
                dc.DrawLine(strongPen, new Point(x, pr.Bottom), new Point(x, pr.Bottom + 4));
                Gfx.Text(dc, FmtRel(u, stepUs), new Rect(x - 50, pr.Bottom + 5, 100, 14), Pal.Muted, SmallSize, false, false, 1);
            }
            dc.DrawRectangle(null, strongPen, pr);

            using (dc.PushClip(pr))
            {
                // CEC waveform: steps, or (zoomed out) the bursts of activity as bars
                {
                    double top, h;
                    RowBounds(0, pr, out top, out h);
                    double yH = top + h * 0.3, yL = top + h * 0.5;
                    var ed = Dec.CecEdges;
                    if (usPerPx > 600)
                    {
                        int i = FirstAfter(ed, V0 - 20000);
                        double gapUs = Math.Max(15000, usPerPx * 3);
                        while (i < ed.Count && ed[i].Key <= ve + 20000)
                        {
                            int j = i;
                            while (j + 1 < ed.Count && ed[j + 1].Key - ed[j].Key < gapUs) j++;
                            double x0 = X(ed[i].Key, pr), x1 = X(ed[j].Key, pr);
                            dc.FillRectangle(Pal.Br(Pal.Accent), new Rect(x0, yH, Math.Max(2, x1 - x0), yL - yH));
                            i = j + 1;
                        }
                        dc.DrawLine(new Pen(Pal.Br(Pal.Accent), 1), new Point(pr.Left, yH), new Point(pr.Right, yH));
                    }
                    else Poly(dc, Steps(ed, 1, yH, yL, pr), new Pen(Pal.Br(ChColor(0)), 1.6));

                    // CEC decoding: a banner per frame above, a box per block under the trace
                    double by = top + 2, bh = Math.Min(h * 0.18, SmallSize + 8);
                    foreach (var fr in Frames)
                    {
                        double fe = fr.B > fr.A ? fr.B : fr.A + 1;
                        if (fe < V0 || fr.A > ve) continue;
                        double x0 = X(fr.A, pr), x1 = X(fe, pr);
                        var rb = new Rect(x0, by, Math.Max(2, x1 - x0), bh);
                        bool bad = fr.Error || !fr.Acked;
                        Color fc = bad ? Pal.Red : Pal.Accent;
                        dc.DrawRectangle(Pal.Br(bad ? Pal.RedBack : Pal.CecBack), new Pen(Pal.Br(fc), 1), rb);
                        var tr = rb.Intersect(pr);
                        if (tr.Width > 8)
                        {
                            double tw = Gfx.Width(fr.Text, SmallSize, false, false);
                            Gfx.Text(dc, fr.Text, tr.Deflate(new Thickness(2, 0)), fc, SmallSize, false, false, tw + 6 <= tr.Width ? 1 : 0);
                        }
                    }
                    if (usPerPx < 300)
                    {
                        double ly = top + h * 0.6, lh = h * 0.2;
                        foreach (var it in Items)
                        {
                            if (it.B < V0 - 5 || it.A > ve + 5) continue;
                            double x0 = X(it.A, pr), x1 = X(it.B, pr);
                            var r = new Rect(x0, ly, Math.Max(x1 - x0, 3), lh);
                            Color fill = it.Kind <= 1 ? Pal.CecBack : it.Kind == 3 ? (it.Bad ? Pal.RedBack : Pal.GoodBack) : Pal.Surface;
                            Color edge = it.Kind == 3 ? (it.Bad ? Pal.Red : Pal.Good) : it.Kind == 0 ? Pal.Accent : LineStrong;
                            dc.DrawRectangle(Pal.Br(fill), new Pen(Pal.Br(edge), 1), r);
                            if (r.Width >= Gfx.Width(it.Text, SmallSize, false, false) + 2)
                                Gfx.Text(dc, it.Text, r, it.Kind == 3 && it.Bad ? Pal.Red : Pal.Text, SmallSize, false, false, 1);
                        }
                    }
                }
                // HPD waveform
                {
                    double top, h;
                    RowBounds(1, pr, out top, out h);
                    double yH = top + h * 0.2, yL = top + h * 0.78;
                    var ed = Dec.HpdEdges;
                    int none = Dec.Hpd;
                    if (ed.Count > 0 || none >= 0) Poly(dc, Steps(ed, none, yH, yL, pr), new Pen(Pal.Br(ChColor(1)), 1.6));
                    // an HPD edge runs through both rows
                    var dot = new Pen(Pal.Br(ChColor(1)), 1) { DashStyle = DashStyle.Dot };
                    for (int i = FirstAfter(ed, V0); i < ed.Count && ed[i].Key <= ve; i++)
                    {
                        double x = X(ed[i].Key, pr);
                        dc.DrawLine(dot, new Point(x, pr.Top), new Point(x, pr.Bottom));
                    }
                }
                if (!double.IsNaN(CurA)) VLine(dc, pr, CurA);
                if (!double.IsNaN(CurB)) VLine(dc, pr, CurB);
                if (live && !double.IsNaN(hoverUs))
                {
                    double x = X(hoverUs, pr);
                    dc.DrawLine(new Pen(Pal.Br(LineStrong), 1) { DashStyle = DashStyle.Dot }, new Point(x, pr.Top), new Point(x, pr.Bottom));
                }
            }
            DrawStrip(dc, pr, live);
        }

        void VLine(DrawingContext dc, Rect pr, double us)
        {
            double x = X(us, pr);
            if (x < pr.Left - 1 || x > pr.Right + 1) return;
            dc.DrawLine(new Pen(Pal.Br(Pal.Accent), 1.2), new Point(x, pr.Top), new Point(x, pr.Bottom));
        }

        Rect StripTag(DrawingContext dc, Rect pr, double us, string text, Color c, int lane, bool left, bool mono = false)
        {
            double x = X(us, pr);
            double tw = Gfx.Width(text, SmallSize, false, mono);
            double y = pr.Top - (2 - lane) * LaneH;
            dc.DrawLine(new Pen(Pal.Br(c), 1.2), new Point(x, y + LaneH * 0.5), new Point(x, pr.Top));
            double w = tw + 8;
            var r = new Rect(left ? x - w : x, y + 1, w, LaneH - 2);
            dc.DrawRectangle(Pal.Br(Pal.Surface), new Pen(Pal.Br(c), 1), r);
            Gfx.Text(dc, text, r, c, SmallSize, false, mono, 1);
            return r;
        }

        void DrawStrip(DrawingContext dc, Rect pr, bool live)
        {
            var used = new List<Rect>();
            Action<double, string, Color, int> tag = (us, text, c, lane) =>
            {
                double x = X(us, pr);
                if (x < pr.Left - 1 || x > pr.Right + 1) return;
                used.Add(StripTag(dc, pr, us, text, c, lane, x + Gfx.Width(text, SmallSize, false, false) + 8 > pr.Right));
            };
            if (!double.IsNaN(CurA)) tag(CurA, "A", Pal.Accent, 1);
            if (!double.IsNaN(CurB)) tag(CurB, "B", Pal.Accent, 1);
            if (!double.IsNaN(CurA) && !double.IsNaN(CurB))
            {
                double xa = X(CurA, pr), xb = X(CurB, pr);
                double x0 = Math.Min(xa, xb), x1 = Math.Max(xa, xb);
                string d = FmtDelta(Math.Abs(CurB - CurA));
                double dw = Gfx.Width(d, SmallSize, false, false);
                double y = pr.Top - LaneH;
                double cx = (x0 + x1) / 2 - dw / 2 - 3;
                if (x1 - x0 < dw + 40) cx = x1 + 22;
                var rr = new Rect(Math.Max(pr.Left, Math.Min(cx, pr.Right - dw - 6)), y + 1, dw + 6, LaneH - 2);
                bool hit = false;
                foreach (var u in used) if (u.Intersects(rr)) hit = true;
                if (!hit)
                {
                    dc.DrawLine(new Pen(Pal.Br(Pal.Accent), 1), new Point(x0, y + LaneH * 0.5), new Point(x1, y + LaneH * 0.5));
                    dc.FillRectangle(Pal.Br(Pal.Surface), rr);
                    Gfx.Text(dc, d, rr, Pal.Accent, SmallSize, false, false, 1);
                }
            }
            if (live && !double.IsNaN(hoverUs))
            {
                string tx = FmtT(Rel(hoverUs)) + "   CEC " + LevelAt(Dec.CecEdges, hoverUs, 1) + "   HPD " +
                            (Dec.HpdEdges.Count > 0 || Dec.Hpd >= 0 ? LevelAt(Dec.HpdEdges, hoverUs, Dec.Hpd).ToString() : "-");
                double w = Gfx.Width(tx, SmallSize) + 12;
                var rr = new Rect(pr.Right - w - 4, pr.Top - 2 * LaneH + 1, w, LaneH - 2);
                dc.DrawRectangle(Pal.Br(Pal.Surface), new Pen(Pal.Br(LineStrong), 1), rr);
                Gfx.Text(dc, tx, rr, Pal.Text, SmallSize, false, true, 1);
            }
        }

        // ---------- mouse ----------
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsLeftButtonPressed) { drag = true; moved = false; dragX = pt.Position.X; dragV0 = V0; e.Pointer.Capture(this); }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pr = PlotRect(Bounds.Size);
            var p = e.GetPosition(this);
            if (drag)
            {
                if (Math.Abs(p.X - dragX) > 3) moved = true;
                if (moved)
                {
                    Live = false;
                    V0 = dragV0 - (p.X - dragX) / pr.Width * VL;
                    Fire();
                }
            }
            hoverUs = p.X >= pr.Left && p.X <= pr.Right ? UsAt(p.X, pr) : double.NaN;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hoverUs = double.NaN; InvalidateVisual(); }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var pr = PlotRect(Bounds.Size);
            var p = e.GetPosition(this);
            bool inside = p.X >= pr.Left && p.X <= pr.Right;
            var kind = e.InitialPressMouseButton;
            if (kind == MouseButton.Left && drag && !moved && inside)
            {
                double us = UsAt(p.X, pr);
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0) CurB = us; else CurA = us;
                if (CursorsChanged != null) CursorsChanged();
            }
            else if (kind == MouseButton.Right && inside)
            {
                CurB = UsAt(p.X, pr);
                if (CursorsChanged != null) CursorsChanged();
            }
            else if (kind == MouseButton.Middle)
            {
                CurA = CurB = double.NaN;
                if (CursorsChanged != null) CursorsChanged();
            }
            drag = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var pr = PlotRect(Bounds.Size);
            var p = e.GetPosition(this);
            double c = p.X >= pr.Left && p.X <= pr.Right ? UsAt(p.X, pr) : V0 + VL / 2;
            Zoom(e.Delta.Y > 0 ? 1.25 : 0.8, c);
            e.Handled = true;
        }

        public void OnDoubleClick() { FitAll(); }
    }
}
