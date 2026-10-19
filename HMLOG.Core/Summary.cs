// One-line summary of a transfer for the log view, as text segments:
//   SCDC registers "NAME = meaning" (LTP requests folded over the lanes,
//   "Ln0-3 FFE up"), HDCP blocks "Bksv: 5 bytes", EDID / other devices as
//   decoded by the Decoder.
// Segment marks: changed against the previous value of the register (yellow
// in the view), muted (gray).
using System.Collections.Generic;

namespace HMLOG
{
    enum Mark { None, Changed, Muted }

    class Seg
    {
        public string Text;
        public Mark Mark;
        public Seg(string t, Mark m) { Text = t; Mark = m; }
    }

    static class Summary
    {
        public static List<Seg> Of(Group g)
        {
            var s = new List<Seg>();
            if (g.Kind == "EVENT")
            {
                s.Add(new Seg(g.EventText, Mark.None));
                return s;
            }
            if (!g.IsTransaction)
            {
                s.Add(new Seg(g.Rows.Count > 0 ? g.Rows[0].Text : "", Mark.None));
                return s;
            }
            if (g.Kind == "I2C")                                // as DpcdLog: text, then the data muted
            {
                // fixed width: the data of every I2C row starts in one column
                s.Add(new Seg(g.Text.Length > 0 ? g.I2cText.PadRight(Decoder.I2cTextW) : g.I2cText, Mark.None));
                if (g.Text.Length > 0)
                {
                    s.Add(new Seg(" ", Mark.Muted));
                    s.Add(new Seg(g.Text, Mark.Muted));
                }
                if (g.Cut) s.Add(new Seg("   (cut off)", Mark.Muted));
                return s;
            }
            var parts = new List<List<Seg>>();

            var map = new Dictionary<int, RegItem>();
            foreach (var r in g.Regs) map[r.Key] = r;
            var done = new HashSet<int>();
            RegItem ri;
            int sc = Decoder.DevScdc << 8;

            // LTP requests 0x41 / 0x42 folded over the lanes
            if (map.ContainsKey(sc | 0x41) || map.ContainsKey(sc | 0x42))
            {
                var t = new string[4];
                var c = new bool[4];
                for (int lane = 0; lane < 4; lane++)
                    if (map.TryGetValue(sc | (0x41 + lane / 2), out ri))
                    {
                        int n = (lane & 1) == 0 ? ri.Value & 0xF : ri.Value >> 4;
                        t[lane] = Decoder.LtpName(n);
                        int pn = ri.Prev < 0 ? -1 : (lane & 1) == 0 ? ri.Prev & 0xF : ri.Prev >> 4;
                        c[lane] = pn >= 0 && pn != n;
                    }
                var p = new List<Seg> { new Seg("LTP".PadRight(Decoder.HdcpNameW + 2) + "  ", Mark.None) };
                var idx = new List<int>();
                for (int i = 0; i < 4; i++) if (t[i] != null) idx.Add(i);
                bool same = true, any = false;
                foreach (int i in idx) { if (t[i] != t[idx[0]]) same = false; if (c[i]) any = true; }
                if (same)
                {
                    p.Add(new Seg((idx.Count == 1 ? "Ln" + idx[0] : string.Format("Ln{0}-{1}", idx[0], idx[idx.Count - 1])) + " ", Mark.None));
                    p.Add(new Seg(t[idx[0]], any ? Mark.Changed : Mark.None));
                }
                else
                    for (int k = 0; k < idx.Count; k++)
                    {
                        int i = idx[k];
                        p.Add(new Seg((k > 0 ? "  " : "") + "Ln" + i + " ", Mark.None));
                        p.Add(new Seg(t[i], c[i] ? Mark.Changed : Mark.None));
                    }
                parts.Add(p);
                done.Add(sc | 0x41); done.Add(sc | 0x42);
            }

            // CED: lanes with a valid count, as numbers
            bool ced = false;
            for (int a = 0x50; a <= 0x5A; a++) if (map.ContainsKey(sc | a)) ced = true;
            if (ced)
            {
                var p = new List<Seg> { new Seg("CED".PadRight(Decoder.HdcpNameW + 2) + "  ", Mark.None) };
                int[] lo = { 0x50, 0x52, 0x54, 0x57, 0x59 };
                string[] nm = { "Ln0", "Ln1", "Ln2", "Ln3", "RS" };
                bool first = true;
                for (int k = 0; k < 5; k++)
                {
                    RegItem l, h;
                    if (!map.TryGetValue(sc | lo[k], out l) || !map.TryGetValue(sc | (lo[k] + 1), out h)) continue;
                    bool valid = (h.Value & 0x80) != 0;
                    int cnt = ((h.Value & 0x7F) << 8) | l.Value;
                    bool chg = l.Changed || h.Changed;
                    p.Add(new Seg((first ? "" : "  ") + nm[k] + " ", Mark.None));
                    p.Add(new Seg(valid ? cnt.ToString() : "-", chg ? Mark.Changed : valid ? Mark.None : Mark.Muted));
                    first = false;
                }
                parts.Add(p);
                for (int a = 0x50; a <= 0x5A; a++) done.Add(sc | a);
            }

            // HDCP: one part per register block, name / meaning / data in fixed
            // columns so the rows line up ("Write_Message  AKE_Init  02 ...")
            foreach (var b in Decoder.HdcpBlocks(g))
            {
                string hex = string.Join(" ", Decoder.HexLines(b.Bytes));
                if (b.Bytes.Count > Decoder.HexPerLine * 2)
                    hex = string.Join(" ", Decoder.HexLines(b.Bytes.GetRange(0, Decoder.HexPerLine * 2))) +
                          string.Format(" … ({0} bytes)", b.Bytes.Count);
                parts.Add(new List<Seg> {
                    new Seg(b.Name.PadRight(Decoder.HdcpNameW) + " ", Mark.None),
                    new Seg(b.Meaning.PadRight(Decoder.HdcpMeanW) + " ", Mark.None),
                    new Seg(hex, Mark.Muted) });
            }
            foreach (var r in g.Regs) if (r.Dev == Decoder.DevHdcp) done.Add(r.Key);

            // the other SCDC registers in transfer order
            foreach (var r in g.Regs)
            {
                if (done.Contains(r.Key)) continue;
                var p = new List<Seg>();
                string name = r.Name.Length > 0 ? r.Name : r.Off.ToString("X2");
                p.Add(new Seg(name.PadRight(Decoder.HdcpNameW + 2) + "= ", Mark.None));   // values in one column
                string m = r.Meaning.Length > 0 ? r.Meaning : r.Value.ToString("X2");
                if (r.Name.Length > 0 && r.Meaning.Length > 0) m = r.Value.ToString("X2") + " " + m;
                p.Add(new Seg(m, r.Changed ? Mark.Changed : Mark.None));
                parts.Add(p);
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) s.Add(new Seg("  ·  ", Mark.Muted));
                s.AddRange(parts[i]);
            }
            if (parts.Count == 0)
                s.Add(new Seg(g.Status == "NACK" && g.Len == 0 ? "no device (address NACK)" : g.Len == 0 ? "x0 (register pointer only)" : "x" + g.Len, Mark.Muted));
            if (g.Cut) s.Add(new Seg("   (cut off)", Mark.Muted));
            return s;
        }

        public static string Text(List<Seg> segs)
        {
            var b = new System.Text.StringBuilder();
            foreach (var x in segs) b.Append(x.Text);
            return b.ToString();
        }
    }
}
