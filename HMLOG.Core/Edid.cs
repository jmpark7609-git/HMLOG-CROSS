// EDID decoding for the HMLOG EDID tab and the one-line block summaries in
// the log (from tools/DpcdLog of OWM-DP V200; HF-VSDB / HF-SCDB: max FRL rate).
using System;
using System.Collections.Generic;
using System.Text;

namespace HMLOG
{
    static class Edid
    {
        public static bool ChecksumOk(byte[] d, int o)
        {
            int s = 0;
            for (int i = 0; i < 128; i++) s += d[o + i];
            return (s & 0xFF) == 0;
        }

        static string Mfg(byte[] d, int o)
        {
            int v = (d[o + 8] << 8) | d[o + 9];
            return new string(new[] { (char)('A' - 1 + ((v >> 10) & 31)), (char)('A' - 1 + ((v >> 5) & 31)),
                                      (char)('A' - 1 + (v & 31)) });
        }

        // descriptor text (13 bytes, ends at LF)
        static string Text(byte[] d, int o)
        {
            var b = new StringBuilder();
            for (int i = 0; i < 13; i++)
            {
                byte x = d[o + i];
                if (x == 0x0A) break;
                b.Append(x >= 0x20 && x < 0x7F ? (char)x : '?');
            }
            return b.ToString().Trim();
        }

        // detailed timing descriptor at o
        static string Timing(byte[] d, int o, bool detail)
        {
            int pclk = d[o] | (d[o + 1] << 8);                     // 10 kHz
            int ha = d[o + 2] | ((d[o + 4] >> 4) << 8), hb = d[o + 3] | ((d[o + 4] & 0xF) << 8);
            int va = d[o + 5] | ((d[o + 7] >> 4) << 8), vb = d[o + 6] | ((d[o + 7] & 0xF) << 8);
            bool il = (d[o + 17] & 0x80) != 0;
            long tot = (long)(ha + hb) * (va + vb);
            string hz = tot > 0 ? Math.Round(pclk * 10000.0 / tot).ToString() : "?";
            string s = string.Format("{0}x{1}{2} @ {3} Hz", ha, va, il ? "i" : "", hz);
            if (detail)
                s += string.Format("  (pixel clock {0:F2} MHz, total {1}x{2})", pclk / 100.0, ha + hb, va + vb);
            return s;
        }

        static bool HasHeader(byte[] d, int o)
        {
            if (d[o] != 0 || d[o + 7] != 0) return false;
            for (int i = 1; i <= 6; i++) if (d[o + i] != 0xFF) return false;
            return true;
        }

        public static string Block0Summary(byte[] d, int o)
        {
            string ck = ChecksumOk(d, o) ? "OK" : "BAD";
            if (!HasHeader(d, o)) return "no EDID header, checksum " + ck;
            string name = "", pref = null;
            for (int k = 0; k < 4; k++)
            {
                int p = o + 54 + 18 * k;
                if ((d[p] | d[p + 1]) != 0) { if (pref == null) pref = Timing(d, p, false); }
                else if (d[p + 3] == 0xFC) name = Text(d, p + 5);
            }
            return string.Format("{0} {1:X4}{2}, {3}, EDID {4}.{5}, {6} extension(s), checksum {7}",
                Mfg(d, o), d[o + 10] | (d[o + 11] << 8), name.Length > 0 ? " \"" + name + "\"" : "",
                pref ?? "no preferred timing", d[o + 18], d[o + 19], d[o + 126], ck);
        }

        // short form for the status panel: maker, monitor name, max FRL rate
        public static string Short(byte[] d, bool[] valid)
        {
            if (!valid[0] || !HasHeader(d, 0)) return "-";
            string name = "";
            for (int k = 0; k < 4; k++)
            {
                int p = 54 + 18 * k;
                if ((d[p] | d[p + 1]) == 0 && d[p + 3] == 0xFC) name = Text(d, p + 5);
            }
            string s = Mfg(d, 0) + (name.Length > 0 ? " " + name : "");
            for (int b = 1; b <= d[126] && b < valid.Length; b++)
            {
                if (!valid[b]) continue;
                string e = ExtSummary(d, b * 128);
                int k = e.IndexOf("max FRL ");
                if (k >= 0) s += " · FRL " + e.Substring(k + 8, e.IndexOf(',', k) - k - 8);
            }
            return s;
        }

        public static string ExtSummary(byte[] d, int o)
        {
            int t = d[o];
            string k = t == 0x02 ? "CTA-861 rev " + d[o + 1] : t == 0x70 ? "DisplayID" : t == 0xF0 ? "block map"
                     : string.Format("tag {0:X2}", t);
            string frl = "";
            if (t == 0x02)
            {
                int end = d[o + 2], p = o + 4;
                while (end >= 4 && p < o + end && p < o + 127)
                {
                    int tag = d[p] >> 5, len = d[p] & 31;
                    bool vsdb = tag == 3 && len >= 7 && (d[p + 1] | (d[p + 2] << 8) | (d[p + 3] << 16)) == 0xC45DD8;
                    bool scdb = tag == 7 && len >= 7 && d[p + 1] == 0x79;
                    if (vsdb || scdb)
                    {
                        int r = d[p + 7] >> 4;
                        frl = ", max FRL " + (r == 0 ? "none" : r < FrlRates.Length ? FrlRates[r] : "reserved " + r);
                    }
                    p += len + 1;
                }
            }
            return k + frl + ", checksum " + (ChecksumOk(d, o) ? "OK" : "BAD");
        }

        // ------------------------------------------------------------------
        // full description for the EDID tab
        // ------------------------------------------------------------------
        static readonly Dictionary<int, string> Iface = new Dictionary<int, string> {
            {1, "DVI"}, {2, "HDMI-a"}, {3, "HDMI-b"}, {4, "MDDI"}, {5, "DisplayPort"} };

        static void Base(StringBuilder b, byte[] d, int o)
        {
            b.AppendFormat("Block 0  base EDID   checksum {0}\r\n", ChecksumOk(d, o) ? "OK" : "BAD");
            if (!HasHeader(d, o)) { b.AppendLine("  no EDID header"); return; }
            b.AppendFormat("  Manufacturer    {0}\r\n", Mfg(d, o));
            b.AppendFormat("  Product code    {0:X4}\r\n", d[o + 10] | (d[o + 11] << 8));
            uint sn = (uint)(d[o + 12] | (d[o + 13] << 8) | (d[o + 14] << 16) | (d[o + 15] << 24));
            b.AppendFormat("  Serial number   {0} (0x{0:X8})\r\n", sn);
            if (d[o + 16] == 0xFF) b.AppendFormat("  Model year      {0}\r\n", 1990 + d[o + 17]);
            else b.AppendFormat("  Made            {0} week {1}\r\n", 1990 + d[o + 17], d[o + 16]);
            b.AppendFormat("  EDID version    {0}.{1}\r\n", d[o + 18], d[o + 19]);
            int inp = d[o + 20];
            if ((inp & 0x80) != 0)
            {
                int bits = (inp >> 4) & 7;
                string iface;
                if (!Iface.TryGetValue(inp & 0xF, out iface)) iface = "interface undefined";
                b.AppendFormat("  Input           digital, {0}, {1}\r\n",
                    bits >= 1 && bits <= 6 ? (4 + 2 * bits) + " bit/color" : "color depth undefined", iface);
            }
            else b.AppendLine("  Input           analog");
            b.AppendFormat("  Screen size     {0} x {1} cm\r\n", d[o + 21], d[o + 22]);
            for (int k = 0; k < 4; k++)
            {
                int p = o + 54 + 18 * k;
                string label = string.Format("  Descriptor {0}    ", k + 1);
                if ((d[p] | d[p + 1]) != 0) { b.AppendLine(label + Timing(d, p, true) + (k == 0 ? "  preferred" : "")); continue; }
                switch (d[p + 3])
                {
                    case 0xFC: b.AppendLine("  Monitor name    " + Text(d, p + 5)); break;
                    case 0xFF: b.AppendLine("  Serial string   " + Text(d, p + 5)); break;
                    case 0xFE: b.AppendLine("  Text            " + Text(d, p + 5)); break;
                    case 0xFD:
                        {
                            int f = d[p + 4];
                            int vmin = d[p + 5] + ((f & 1) != 0 ? 255 : 0), vmax = d[p + 6] + ((f & 2) != 0 ? 255 : 0);
                            int hmin = d[p + 7] + ((f & 4) != 0 ? 255 : 0), hmax = d[p + 8] + ((f & 8) != 0 ? 255 : 0);
                            b.AppendFormat("  Range limits    V {0}-{1} Hz, H {2}-{3} kHz, max pixel clock {4} MHz\r\n",
                                vmin, vmax, hmin, hmax, d[p + 9] * 10);
                            break;
                        }
                    case 0x10: break;                               // dummy
                    default: b.AppendFormat("{0}tag {1:X2}\r\n", label, d[p + 3]); break;
                }
            }
            b.AppendFormat("  Extensions      {0}\r\n", d[o + 126]);
        }

        // HF-VSDB (OUI C4-5D-D8) / HF-SCDB (extended tag 0x79): same layout from
        // byte 4: version, Max_TMDS_Character_Rate (x 5 MHz), flags (bit 7
        // SCDC_Present), Max_FRL_Rate (byte 7, bits 7:4)
        static readonly string[] FrlRates = { "no FRL", "3G 3L", "6G 3L", "6G 4L", "8G 4L", "10G 4L", "12G 4L" };

        public static string HfInfo(byte[] d, int p, int len)
        {
            var l = new List<string>();
            if (len >= 5) l.Add(string.Format("max TMDS {0} MHz", d[p + 5] * 5));
            if (len >= 6) l.Add((d[p + 6] & 0x80) != 0 ? "SCDC" : "no SCDC");
            if (len >= 7)
            {
                int r = d[p + 7] >> 4;
                l.Add("max FRL " + (r < FrlRates.Length ? FrlRates[r] : "reserved " + r));
            }
            return l.Count > 0 ? string.Join(", ", l) : "-";
        }

        static string Vics(byte[] d, int p, int len)
        {
            var l = new List<string>();
            for (int i = 1; i <= len; i++)
            {
                int x = d[p + i];
                bool native = (x & 0x80) != 0 && (x & 0x7F) <= 64;
                l.Add((native ? x & 0x7F : x) + (native ? "*" : ""));
            }
            return string.Join(" ", l);
        }

        static void Cta(StringBuilder b, byte[] d, int o, int blk)
        {
            b.AppendFormat("Block {0}  CTA-861 rev {1}   checksum {2}\r\n", blk, d[o + 1], ChecksumOk(d, o) ? "OK" : "BAD");
            int f = d[o + 3];
            var fl = new List<string>();
            if ((f & 0x80) != 0) fl.Add("underscan");
            if ((f & 0x40) != 0) fl.Add("basic audio");
            if ((f & 0x20) != 0) fl.Add("YCbCr 4:4:4");
            if ((f & 0x10) != 0) fl.Add("YCbCr 4:2:2");
            b.AppendFormat("  Supports        {0}\r\n", fl.Count > 0 ? string.Join(", ", fl) : "-");
            int end = d[o + 2];
            int p = o + 4;
            while (end >= 4 && p < o + end && p < o + 127)
            {
                int tag = d[p] >> 5, len = d[p] & 31;
                switch (tag)
                {
                    case 1: b.AppendFormat("  Audio           {0} format(s)\r\n", len / 3); break;
                    case 2: b.AppendFormat("  Video VIC       {0}   (* = native)\r\n", Vics(d, p, len)); break;
                    case 3:
                        {
                            int oui = len >= 3 ? d[p + 1] | (d[p + 2] << 8) | (d[p + 3] << 16) : 0;
                            string n;
                            if (oui == 0x000C03) n = "HDMI 1.4 (VSDB)";
                            else if (oui == 0xC45DD8) n = "HDMI Forum (HF-VSDB), " + HfInfo(d, p, len);
                            else if (oui == 0x00001A) n = "AMD (FreeSync)";
                            else if (oui == 0x90848B) n = "HDR10+";
                            else n = string.Format("OUI {0:X6}", oui);
                            b.AppendLine("  Vendor block    " + n);
                            break;
                        }
                    case 4: b.AppendLine("  Speaker allocation"); break;
                    case 5: b.AppendLine("  VESA display transfer characteristic"); break;
                    case 7:
                        {
                            int et = len >= 1 ? d[p + 1] : -1;
                            string n = et == 0 ? "Video capability" : et == 5 ? "Colorimetry" : et == 6 ? "HDR static metadata"
                                     : et == 7 ? "HDR dynamic metadata" : et == 14 ? "YCbCr 4:2:0 video"
                                     : et == 15 ? "YCbCr 4:2:0 capability map"
                                     : et == 0x79 ? "HDMI Forum (HF-SCDB), " + HfInfo(d, p, len) : "extended tag " + et;
                            b.AppendLine("  Extended block  " + n);
                            break;
                        }
                    default: b.AppendFormat("  Data block      tag {0}, {1} bytes\r\n", tag, len); break;
                }
                p += len + 1;
            }
            // detailed timings after the data blocks
            for (int q = o + (end >= 4 ? end : 4); q + 18 <= o + 127; q += 18)
            {
                if ((d[q] | d[q + 1]) == 0) break;
                b.AppendLine("  Detailed timing " + Timing(d, q, true));
            }
        }

        static void Hex(StringBuilder b, byte[] d, int o, int blk)
        {
            for (int r = 0; r < 8; r++)
            {
                b.AppendFormat("  {0:X3}: ", blk * 128 + r * 16);
                for (int i = 0; i < 16; i++) b.AppendFormat("{0:X2} ", d[o + r * 16 + i]);
                b.AppendLine();
            }
        }

        public static string Describe(byte[] data, bool[] valid)
        {
            var b = new StringBuilder();
            int n = 0;
            for (int k = 0; k < valid.Length; k++)
            {
                if (!valid[k]) continue;
                n++;
                int o = k * 128;
                if (k == 0) Base(b, data, o);
                else if (data[o] == 0x02) Cta(b, data, o, k);
                else b.AppendFormat("Block {0}  {1}\r\n", k, ExtSummary(data, o));
                b.AppendLine();
            }
            if (n == 0) return "No complete EDID block yet.\r\n(It appears once the source reads the EDID: reconnect the board or power-cycle the monitor to read it again.)";
            if (valid[0] && data[126] + 1 > n)
                b.AppendFormat("Note: only {1} of {0} blocks received (extension blocks not read yet, or FIFO overflow).\r\n\r\n", data[126] + 1, n);
            b.AppendLine("Hex");
            for (int k = 0; k < valid.Length; k++) if (valid[k]) Hex(b, data, k * 128, k);
            return b.ToString();
        }
    }
}
