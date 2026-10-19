using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace HMLOG.App
{
    // bit view of the detail panel: the register value as 8 bit cells, below them the bit fields
    // (changed bits against the previous value yellow). Port of the BitView of the Windows version.
    class BitView : Control
    {
        RegItem r;

        public BitView() { Height = 96; ClipToBounds = true; }

        public void Set(RegItem x) { r = x; InvalidateVisual(); }

        FormattedText Ft(string t, IBrush b, bool bold = false)
        {
            return new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Table.Mono, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), Table.FontSize, b);
        }

        void Cell(DrawingContext dc, Rect c, string t, Color fore, Color? back, Pen line)
        {
            dc.DrawRectangle(back.HasValue ? Pal.Br(back.Value) : null, line, c);
            var ft = Ft(t, Pal.Br(fore));
            ft.MaxTextWidth = Math.Max(1, c.Width - 4);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(c.X + Math.Max(2, (c.Width - ft.Width) / 2), c.Y + (c.Height - ft.Height) / 2));
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(Pal.Br(Pal.Surface), new Rect(Bounds.Size));
            var line = new Pen(Pal.Br(Pal.Line), 1);
            dc.DrawLine(line, new Point(0, 0), new Point(Bounds.Width, 0));
            double pad = 8, rh = (Bounds.Height - pad) / 3;
            var muted = Pal.Muted;
            if (r == null)
            {
                var t = Ft("Select a register to see its bits here", Pal.Br(muted));
                dc.DrawText(t, new Point(pad, pad / 2 + (rh - t.Height) / 2));
                return;
            }
            int v = r.Value;
            string bin = Convert.ToString(v, 2).PadLeft(8, '0');
            string cap = string.Format("{0:X2}:{1:X2} {2} = 0x{3:X2}  ({4} {5})", r.Dev, r.Off, r.Name, v, bin.Substring(0, 4), bin.Substring(4));
            if (r.Prev >= 0) cap += string.Format("   previous 0x{0:X2}", r.Prev);
            var cp = Ft(cap, Pal.Br(Pal.Text));
            dc.DrawText(cp, new Point(pad, pad / 2 + (rh - cp.Height) / 2));

            double cw = Math.Max(1, Math.Min((Bounds.Width - 2 * pad) / 8, 90));
            double y1 = pad / 2 + rh, y2 = y1 + rh;
            var onB = Pal.Bg(222, 236, 252);
            var onT = Pal.Fg(20, 85, 165);
            for (int b = 7; b >= 0; b--)
            {
                var c = new Rect(pad + (7 - b) * cw, y1, cw, rh);
                bool on = (v >> b & 1) != 0, chg = r.Prev >= 0 && (r.Prev >> b & 1) != (v >> b & 1);
                Cell(dc, c, string.Format("b{0}  {1}", b, on ? 1 : 0), on ? Pal.Text : muted, chg ? Pal.ChgBack : (Color?)null, line);
            }
            var fields = Decoder.BitFields(r.Dev, r.Off) ?? new List<BitField> { new BitField(7, 0, "") };
            foreach (var f in fields)
            {
                var c = new Rect(pad + (7 - f.Hi) * cw, y2, (f.Hi - f.Lo + 1) * cw, rh);
                int x = f.Of(v);
                bool chg = r.Prev >= 0 && f.Of(r.Prev) != x;
                Color? back = chg ? Pal.ChgBack : f.Name.Length > 0 && x != 0 ? onB : (Color?)null;
                string t;
                if (f.Name.Length == 0) t = f.Hi - f.Lo == 7 ? "0x" + v.ToString("X2") : "-";
                else if (f.Hi == f.Lo) t = f.Name;
                else t = f.Name + " = " + Decoder.FieldText(r.Dev, r.Off, f, v);
                Color fore = f.Name.Length == 0 ? muted : x != 0 || chg ? (chg && Pal.Dark ? Pal.Text : onT) : muted;
                Cell(dc, c, t, fore, back, line);
            }
        }
    }
}
