// CEC decoding of a logic analyzer capture (Decoder.WaveCap, channel CEC = state bit 0).
// A CEC bit starts with a falling edge: the low time tells the value (about 0.6 ms = 1, 1.5 ms = 0, bit
// period 2.4 ms); the start bit is low for 3.7 ms (period 4.5 ms). A frame is the start bit and blocks of
// 10 bits: 8 data bits (MSB first), EOM (1 = last block) and ACK. The first block is the header
// (initiator << 4 | destination), the second the opcode, the rest its operands.
// ACK: a directed message is acknowledged by the destination holding the line low (bit reads 0);
// a broadcast (destination 15) is "acknowledged" when nobody objects (bit reads 1).
using System;
using System.Collections.Generic;
using System.Text;

namespace HMLOG
{
    class CecItem
    {
        public long A, B;               // samples
        public string Text;
        public int Kind;                // 0 start bit, 1 header, 2 data block, 3 ACK, 4 spike (ignored)
        public bool Bad;
    }

    class CecFrame
    {
        public long A, B;
        public readonly List<int> Bytes = new List<int>();
        public bool Eom, Acked = true, Error, Partial;
        public bool HeadAcked;          // the header block was acknowledged (destination there / broadcast not refused)
        // 0 normal, 1 NACK that is no problem (nobody at that address, own address check, the initiator then
        // gave up), 2 warning (odd, or a NACK from a device that answered before), 3 error (bit error)
        public int Level;
        public string Why = "";         // why a NACK is a warning
        public bool Lost;               // the FPGA lost log records in this frame (E FF): its edges are not complete
        public int Spikes;              // lows of SpikeUs or less inside the frame, not read as bits
        public string Text = "";
        public int Init { get { return Bytes.Count > 0 ? (Bytes[0] >> 4) & 15 : -1; } }
        public int Dest { get { return Bytes.Count > 0 ? Bytes[0] & 15 : -1; } }
    }

    static class CecDecoder
    {
        // A low this short is no CEC bit (0.6 / 1.5 ms): a spike on the line (seen at the rising edge while the
        // DDC lines move). It is skipped, the frame goes on. The repeater in the FPGA ignores lows under 4 us too.
        public const int SpikeUs = 3;

        static readonly string[] Dev = { "TV", "Rec1", "Rec2", "Tuner1", "Play1", "Audio", "Tuner2", "Tuner3", "Play2", "Rec3", "Tuner4", "Play3", "Res12", "Res13", "Spec", "All" };

        public static string DevName(int a) { return Dev[a & 15]; }

        static readonly string[] DevType = { "TV", "Recording", "Reserved", "Tuner", "Playback", "Audio System", "Pure CEC Switch", "Video Processor" };

        static readonly Dictionary<int, string> Ops = new Dictionary<int, string>
        {
            { 0x00, "Feature Abort" }, { 0x04, "Image View On" }, { 0x05, "Tuner Step Increment" }, { 0x06, "Tuner Step Decrement" },
            { 0x07, "Tuner Device Status" }, { 0x08, "Give Tuner Device Status" }, { 0x09, "Record On" }, { 0x0A, "Record Status" },
            { 0x0B, "Record Off" }, { 0x0D, "Text View On" }, { 0x0F, "Record TV Screen" }, { 0x1A, "Give Deck Status" },
            { 0x1B, "Deck Status" }, { 0x32, "Set Menu Language" }, { 0x33, "Clear Analogue Timer" }, { 0x34, "Set Analogue Timer" },
            { 0x35, "Timer Status" }, { 0x36, "Standby" }, { 0x41, "Play" }, { 0x42, "Deck Control" }, { 0x43, "Timer Cleared Status" },
            { 0x44, "User Control Pressed" }, { 0x45, "User Control Released" }, { 0x46, "Give OSD Name" }, { 0x47, "Set OSD Name" },
            { 0x64, "Set OSD String" }, { 0x67, "Set Timer Program Title" }, { 0x70, "System Audio Mode Request" }, { 0x71, "Give Audio Status" },
            { 0x72, "Set System Audio Mode" }, { 0x7A, "Report Audio Status" }, { 0x7D, "Give System Audio Mode Status" },
            { 0x7E, "System Audio Mode Status" }, { 0x80, "Routing Change" }, { 0x81, "Routing Information" }, { 0x82, "Active Source" },
            { 0x83, "Give Physical Address" }, { 0x84, "Report Physical Address" }, { 0x85, "Request Active Source" }, { 0x86, "Set Stream Path" },
            { 0x87, "Device Vendor ID" }, { 0x89, "Vendor Command" }, { 0x8A, "Vendor Remote Button Down" }, { 0x8B, "Vendor Remote Button Up" },
            { 0x8C, "Give Device Vendor ID" }, { 0x8D, "Menu Request" }, { 0x8E, "Menu Status" }, { 0x8F, "Give Device Power Status" },
            { 0x90, "Report Power Status" }, { 0x91, "Get Menu Language" }, { 0x92, "Select Analogue Service" }, { 0x93, "Select Digital Service" },
            { 0x97, "Set Digital Timer" }, { 0x99, "Clear Digital Timer" }, { 0x9A, "Set Audio Rate" }, { 0x9D, "Inactive Source" },
            { 0x9E, "CEC Version" }, { 0x9F, "Get CEC Version" }, { 0xA0, "Vendor Command With ID" }, { 0xA1, "Clear External Timer" },
            { 0xA2, "Set External Timer" }, { 0xA3, "Report Short Audio Descriptor" }, { 0xA4, "Request Short Audio Descriptor" },
            { 0xC0, "Initiate ARC" }, { 0xC1, "Report ARC Initiated" }, { 0xC2, "Report ARC Terminated" }, { 0xC3, "Request ARC Initiation" },
            { 0xC4, "Request ARC Termination" }, { 0xC5, "Terminate ARC" }, { 0xF8, "CDC Message" }, { 0xFF, "Abort" }
        };

        // operand bytes an opcode needs at least (a shorter message is shown as "operand missing")
        static readonly Dictionary<int, int> Need = new Dictionary<int, int>
        {
            { 0x00, 2 }, { 0x32, 3 }, { 0x42, 1 }, { 0x44, 1 }, { 0x47, 1 }, { 0x72, 1 }, { 0x7E, 1 }, { 0x7A, 1 }, { 0x80, 4 }, { 0x81, 2 },
            { 0x82, 2 }, { 0x84, 3 }, { 0x86, 2 }, { 0x87, 3 }, { 0x8D, 1 }, { 0x8E, 1 }, { 0x90, 1 }, { 0x9D, 2 }, { 0x9E, 1 }, { 0x1B, 1 }, { 0x41, 1 },
            { 0xA0, 3 }
        };

        // messages that are broadcast by definition
        static readonly int[] BroadcastOps = { 0x80, 0x81, 0x82, 0x84, 0x85, 0x86 };
        // ARC control (Initiate / Report Initiated / Report Terminated / Request Initiation / Request Termination /
        // Terminate). OWM-HM repeats CEC but not the ARC / eARC audio return (HEAC lines): the devices can agree on
        // ARC over CEC while no sound comes back, so these messages get a warning.
        static readonly int[] ArcOps = { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 };

        // messages that take no operand
        static readonly int[] NoOperand = { 0x04, 0x0D, 0x36, 0x45, 0x46, 0x71, 0x7D, 0x83, 0x85, 0x8C, 0x8F, 0x91, 0x9F, 0xC0, 0xC3, 0xC4, 0xC5 };

        static readonly Dictionary<int, string> Vendors = new Dictionary<int, string>
        {
            { 0x0000F0, "Samsung" }, { 0x00E091, "LG" }, { 0x080046, "Sony" }, { 0x008045, "Panasonic" }, { 0x00903E, "Philips" },
            { 0x000039, "Toshiba" }, { 0x08001F, "Sharp" }, { 0x00E036, "Pioneer" }, { 0x0009B0, "Onkyo" }, { 0x00A0DE, "Yamaha" },
            { 0x0005CD, "Denon" }, { 0x001A11, "Google" }, { 0x000CE7, "MediaTek" }, { 0x00D0B8, "Marantz" }
        };

        public static string OpName(int op)
        {
            string n;
            return Ops.TryGetValue(op, out n) ? n : "Opcode " + op.ToString("X2");
        }

        static string Addr(int hi, int lo) { return string.Format("{0}.{1}.{2}.{3}", (hi >> 4) & 15, hi & 15, (lo >> 4) & 15, lo & 15); }

        static string Key(int k)
        {
            switch (k)
            {
                case 0x00: return "Select"; case 0x01: return "Up"; case 0x02: return "Down"; case 0x03: return "Left"; case 0x04: return "Right";
                case 0x05: return "Right-Up"; case 0x06: return "Right-Down"; case 0x07: return "Left-Up"; case 0x08: return "Left-Down";
                case 0x09: return "Root menu"; case 0x0A: return "Setup menu"; case 0x0B: return "Contents menu"; case 0x0C: return "Favorite menu";
                case 0x0D: return "Exit"; case 0x2A: return "."; case 0x2B: return "Enter"; case 0x2C: return "Clear";
                case 0x2F: return "Next favorite"; case 0x30: return "Channel up"; case 0x31: return "Channel down"; case 0x32: return "Previous channel";
                case 0x33: return "Sound select"; case 0x34: return "Input select"; case 0x35: return "Display info"; case 0x36: return "Help";
                case 0x37: return "Page up"; case 0x38: return "Page down"; case 0x40: return "Power"; case 0x41: return "Volume up";
                case 0x42: return "Volume down"; case 0x43: return "Mute"; case 0x44: return "Play"; case 0x45: return "Stop"; case 0x46: return "Pause";
                case 0x47: return "Record"; case 0x48: return "Rewind"; case 0x49: return "Fast forward"; case 0x4A: return "Eject"; case 0x4B: return "Forward";
                case 0x4C: return "Backward"; case 0x60: return "Play function"; case 0x61: return "Pause-Play function"; case 0x62: return "Record function";
                case 0x63: return "Pause-Record function"; case 0x64: return "Stop function"; case 0x65: return "Mute function"; case 0x66: return "Restore volume function";
                case 0x6B: return "Power toggle"; case 0x6C: return "Power off"; case 0x6D: return "Power on";
                case 0x71: return "Blue"; case 0x72: return "Red"; case 0x73: return "Green"; case 0x74: return "Yellow"; case 0x75: return "F5";
            }
            if (k >= 0x20 && k <= 0x29) return "digit " + (k - 0x20);
            return "key " + k.ToString("X2");
        }

        static string Power(int v) { return v == 0 ? "On" : v == 1 ? "Standby" : v == 2 ? "Standby > On" : v == 3 ? "On > Standby" : v.ToString("X2"); }

        static string Version(int v) { return v == 4 ? "1.3a" : v == 5 ? "1.4" : v == 6 ? "2.0" : v.ToString("X2"); }

        static string Reason(int v) { return v == 0 ? "unrecognized opcode" : v == 1 ? "not in correct mode" : v == 2 ? "cannot provide source" : v == 3 ? "invalid operand" : v == 4 ? "refused" : v.ToString("X2"); }

        static string Deck(int v)
        {
            switch (v)
            {
                case 0x11: return "Play"; case 0x12: return "Record"; case 0x13: return "Play reverse"; case 0x14: return "Still"; case 0x15: return "Slow";
                case 0x16: return "Slow reverse"; case 0x17: return "Search forward"; case 0x18: return "Search reverse"; case 0x19: return "No media";
                case 0x1A: return "Stop"; case 0x1B: return "Wind"; case 0x1C: return "Rewind"; case 0x1F: return "Other";
            }
            return v.ToString("X2");
        }

        static string Ascii(List<int> a, int from, int count)
        {
            var t = new StringBuilder();
            for (int i = from; i < a.Count && i < from + count; i++) t.Append(a[i] >= 32 && a[i] < 127 ? ((char)a[i]).ToString() : "?");
            return t.ToString();
        }

        static string Hex(List<int> a, int from)
        {
            var sb = new StringBuilder();
            for (int i = from; i < a.Count; i++) { if (i > from) sb.Append(' '); sb.Append(a[i].ToString("X2")); }
            return sb.ToString();
        }

        // the operands of a message as text
        static string Operands(CecFrame f)
        {
            var a = f.Bytes;
            int op = a[1], n = a.Count - 2;
            switch (op)
            {
                case 0x82: case 0x86: case 0x9D:
                    return n >= 2 ? Addr(a[2], a[3]) : "";
                case 0x84:
                    return n >= 3 ? Addr(a[2], a[3]) + ", " + (a[4] < DevType.Length ? DevType[a[4]] : a[4].ToString("X2")) : "";
                case 0x80:
                    return n >= 4 ? Addr(a[2], a[3]) + " > " + Addr(a[4], a[5]) : "";
                case 0x81:
                    return n >= 2 ? Addr(a[2], a[3]) : "";
                case 0x44:
                    return n >= 1 ? Key(a[2]) : "";
                case 0x47:
                    return n >= 1 ? "\"" + Ascii(a, 2, 14) + "\"" : "";
                case 0x64:
                    return n >= 1 ? (a[2] == 0 ? "display now" : a[2] == 0x40 ? "clear" : "control " + a[2].ToString("X2")) + " \"" + Ascii(a, 3, 13) + "\"" : "";
                case 0x32:
                    return n >= 3 ? "\"" + Ascii(a, 2, 3) + "\"" : "";
                case 0x90:
                    return n >= 1 ? Power(a[2]) : "";
                case 0x9E:
                    return n >= 1 ? Version(a[2]) : "";
                case 0x00:
                    return n >= 2 ? OpName(a[2]) + ", reason: " + Reason(a[3]) : "";
                case 0x8D:
                    return n >= 1 ? (a[2] == 0 ? "activate" : a[2] == 1 ? "deactivate" : a[2] == 2 ? "query" : a[2].ToString("X2")) : "";
                case 0x8E:
                    return n >= 1 ? (a[2] == 0 ? "activated" : a[2] == 1 ? "deactivated" : a[2].ToString("X2")) : "";
                case 0x72: case 0x7E:
                    return n >= 1 ? (a[2] == 0 ? "Off" : "On") : "";
                case 0x70:
                    return n >= 2 ? Addr(a[2], a[3]) : "";
                case 0x7A:
                    return n >= 1 ? ((a[2] & 0x80) != 0 ? "muted, " : "") + "volume " + (a[2] & 0x7F) : "";
                case 0x1B:
                    return n >= 1 ? Deck(a[2]) : "";
                case 0x42:
                    return n >= 1 ? (a[2] == 1 ? "skip forward" : a[2] == 2 ? "skip reverse" : a[2] == 3 ? "stop" : a[2] == 4 ? "eject" : a[2].ToString("X2")) : "";
                case 0x41:
                    return n >= 1 ? (a[2] == 0x24 ? "play forward" : a[2] == 0x20 ? "play reverse" : a[2] == 0x25 ? "play still" : a[2].ToString("X2")) : "";
                case 0x87:
                    {
                        if (n < 3) return "";
                        int id = (a[2] << 16) | (a[3] << 8) | a[4];
                        string v;
                        return "0x" + id.ToString("X6") + (Vendors.TryGetValue(id, out v) ? " (" + v + ")" : "");
                    }
                case 0xA0:
                    {
                        if (n < 3) return "";
                        int id = (a[2] << 16) | (a[3] << 8) | a[4];
                        string v;
                        return "vendor 0x" + id.ToString("X6") + (Vendors.TryGetValue(id, out v) ? " (" + v + ")" : "") + (n > 3 ? ": " + Hex(a, 5) : "");
                    }
            }
            return n > 0 ? Hex(a, 2) : "";
        }

        // the parts of a message for the event list
        public static string Message(CecFrame f) { return f.Bytes.Count == 0 ? "" : f.Bytes.Count == 1 ? "poll" : OpName(f.Bytes[1]); }

        public static string OperandText(CecFrame f) { return f.Bytes.Count >= 2 ? Operands(f) : ""; }

        public static string HexBytes(CecFrame f)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < f.Bytes.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(f.Bytes[i].ToString("X2")); }
            return sb.ToString();
        }

        // what is odd about the message (operand count, broadcast / directed, ...)
        public static string Notes(CecFrame f)
        {
            string note = "";
            if (f.Bytes.Count >= 2)
            {
                int op = f.Bytes[1], n = f.Bytes.Count - 2;
                int need;
                if (Need.TryGetValue(op, out need) && n < need) note += " operand missing";
                else if (Array.IndexOf(NoOperand, op) >= 0 && n > 0) note += " " + n + " extra byte" + (n > 1 ? "s" : "");
                if (Array.IndexOf(BroadcastOps, op) >= 0 && f.Dest != 15) note += " directed, should be broadcast";
                if (Array.IndexOf(ArcOps, op) >= 0) note += " no ARC on OWM-HM";
            }
            if (f.Bytes.Count > 0 && f.Init == f.Dest && f.Dest != 15) note += " sent to itself";
            return note.Trim();
        }

        // Level of a frame (CecFrame.Level). known: bit a = logical address a acknowledged a message or sent one
        // with an opcode before this frame. A NACK is part of the protocol: polls find out which addresses are used, a device
        // polls its own address before it takes it, and a message to an address nobody uses is not acknowledged
        // (the initiator then stops: "cut"). It is a warning only when that device answered before, refused a
        // later byte, or a broadcast was refused.
        public static void Classify(CecFrame f, int known)
        {
            f.Why = "";
            if (f.Error || f.Bytes.Count == 0) { f.Level = 3; return; }
            bool bc = f.Dest == 15;
            int lv = 0;
            if (!f.Acked)
            {
                if (bc) lv = 2;                                              // a device objected
                else if (f.HeadAcked) { lv = 2; f.Why = "byte refused"; }    // the device is there
                else if (f.Init == f.Dest) lv = 1;                           // own address check: free
                else if (((known >> f.Dest) & 1) != 0) { lv = 2; f.Why = "answered before"; }
                else lv = 1;                                                 // nobody at that address
            }
            if (f.Partial) { if (lv != 1) lv = 2; }                          // cut after such a NACK is normal
            else if (!f.Eom) lv = 2;
            if (Notes(f).Replace("sent to itself", "").Trim().Length > 0) lv = 2;
            f.Level = lv;
        }

        // ACK / NACK / broadcast / bit error / cut, and a kind: 0 normal (a NACK that is no problem too), 1 odd, 2 bad
        public static string ResultText(CecFrame f, out int kind)
        {
            kind = f.Level >= 3 ? 2 : f.Level == 2 ? 1 : 0;
            string r;
            if (f.Lost) { kind = 1; return "log records lost" + (f.Error ? " (bit error not real)" : ""); }
            if (f.Error) return "bit error";
            if (f.Bytes.Count == 0) return "start bit only";
            if (f.Dest == 15) r = f.Acked ? "broadcast" : "refused by a device";
            else r = f.Acked ? "ACK" : "NACK";
            if (f.Why.Length > 0) r += " (" + f.Why + ")";
            if (f.Partial) r += ", cut";
            else if (!f.Eom) r += ", no EOM";
            string n = Notes(f);
            if (n.Length > 0) r += ", " + n;
            if (f.Spikes > 0) r += ", " + f.Spikes + " spike" + (f.Spikes > 1 ? "s" : "") + " ignored";
            return r;
        }

        // "Play1 > TV  Image View On" with the operands and what is odd about the message
        public static string FrameText(CecFrame f)
        {
            if (f.Bytes.Count == 0) return f.Lost ? "(log records lost)" : "";
            var sb = new StringBuilder();
            // fixed width names: the arrows and the message names line up one under the other (Log tab, mono font)
            sb.Append(DevName(f.Init).PadRight(6)).Append(" > ").Append(DevName(f.Dest).PadRight(6));
            string note = "";
            if (f.Bytes.Count == 1) sb.Append("  poll");
            else
            {
                int op = f.Bytes[1], n = f.Bytes.Count - 2;
                sb.Append("  ").Append(OpName(op));
                string ops = Operands(f);
                if (ops.Length > 0) sb.Append(' ').Append(ops);
                int need;
                if (Need.TryGetValue(op, out need) && n < need) note += " operand missing";
                else if (Array.IndexOf(NoOperand, op) >= 0 && n > 0) note += " " + n + " extra byte" + (n > 1 ? "s" : "");
                if (Array.IndexOf(BroadcastOps, op) >= 0 && f.Dest != 15) note += " directed, should be broadcast";
                if (Array.IndexOf(ArcOps, op) >= 0) note += " no ARC on OWM-HM";
                if (f.Init == f.Dest && f.Dest != 15) note += " sent to itself";
            }
            if (!f.Acked)
                sb.Append(f.Dest == 15 ? "  NACK (refused by a device)" : f.Why.Length > 0 ? "  NACK (" + f.Why + ")"
                          : f.Init == f.Dest ? "  NACK (address free)" : "  NACK (no device)");
            if (note.Length > 0) sb.Append("  (").Append(note.Trim()).Append(')');
            if (f.Spikes > 0) sb.Append("  (spike ignored)");
            if (f.Lost) sb.Append("  (log records lost)");
            else if (f.Error) sb.Append("  (bit error)");
            else if (f.Partial) sb.Append("  (cut)");
            else if (!f.Eom) sb.Append("  (no EOM)");
            return sb.ToString();
        }

        // edges: (us, level) in time order; endUs: where the record ends (a bit still low is cut there).
        // Items and frames are in us.
        // gaps of the CEC record: lost log records (E FF at time m) took the edges between the last edge before m
        // and m (the events that waited in the queue come after the mark, with earlier times). At most 300 ms.
        public static List<KeyValuePair<long, long>> Gaps(List<long> marks, List<KeyValuePair<long, int>> a, List<KeyValuePair<long, int>> b)
        {
            var g = new List<KeyValuePair<long, long>>();
            foreach (long m in marks)
            {
                long s = Math.Max(LastBefore(a, m), LastBefore(b, m));
                g.Add(new KeyValuePair<long, long>(Math.Max(s, m - 300000), m));
            }
            return g;
        }

        static long LastBefore(List<KeyValuePair<long, int>> l, long t)
        {
            int lo = 0, hi = l.Count - 1, r = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (l[mid].Key < t) { r = mid; lo = mid + 1; } else hi = mid - 1; }
            return r >= 0 ? l[r].Key : long.MinValue;
        }

        // a frame touches a gap, or broke off (bit error, cut, no EOM) at most 5 ms before it (its last low went
        // on into the gap): mark it (warning, its bits are not to be trusted) and make its text again
        public static void MarkLost(List<CecFrame> frames, List<CecItem> items, List<KeyValuePair<long, long>> gaps)
        {
            if (gaps.Count > 0)
                foreach (var it in items)                    // a "?" bit that ran into a gap is no real bit error
                    if (it.Bad && it.Text == "?")
                        foreach (var g in gaps)
                            if (it.A <= g.Value && it.B + 5000 >= g.Key) { it.Bad = false; it.Kind = 2; it.Text = "log lost"; break; }
            if (gaps.Count == 0) return;
            foreach (var f in frames)
            {
                double fb = f.B > f.A ? f.B : f.A + 1;
                bool broke = f.Error || f.Partial || !f.Eom;
                foreach (var g in gaps)
                    if (f.A <= g.Value && (fb >= g.Key - 1 || broke && fb + 5000 >= g.Key))
                    {
                        f.Lost = true; f.Level = 2; f.Text = FrameText(f);
                        break;
                    }
            }
        }

        public static void Decode(List<KeyValuePair<long, int>> edges, long endUs, out List<CecFrame> frames, out List<CecItem> items)
        {
            var st = new CecStream();
            if (edges != null && edges.Count >= 2)
            {
                long fall = -1;
                int prev = 1;
                for (int i = 0; i < edges.Count; i++)
                {
                    int cur = edges[i].Value;
                    if (prev == 1 && cur == 0) fall = edges[i].Key;
                    else if (prev == 0 && cur == 1 && fall >= 0) { st.Feed(fall, edges[i].Key); fall = -1; }
                    prev = cur;
                }
                if (fall >= 0) st.Feed(fall, Math.Max(endUs, fall + 1));        // still low at the end
                st.Finish();
            }
            frames = st.Frames;
            items = st.Items;
        }
    }

    // CEC bit by bit: Feed(fall, rise) with the falling and the rising edge of every low pulse, in us. Frames and
    // their blocks are collected; OnFrame is called when a frame is complete (EOM), broken or cut off.
    class CecStream
    {
        public readonly List<CecFrame> Frames = new List<CecFrame>();
        public readonly List<CecItem> Items = new List<CecItem>();
        public Action<CecFrame> OnFrame;
        CecFrame f;
        int bit, val;
        long byteStart, prevFall, lastFall = -1;
        int known;                      // bit a: logical address a acknowledged or sent a message (CecDecoder.Classify)

        void Done(CecFrame fr)
        {
            CecDecoder.Classify(fr, known);
            if (!fr.Error && fr.Bytes.Count > 0)
            {
                // a sender counts only with a message (an opcode): a poll may be an address check of an address
                // the device then does not take. 15 as initiator = unregistered.
                if (fr.Bytes.Count >= 2 && fr.Init != 15) known |= 1 << fr.Init;
                if (fr.Dest != 15 && fr.HeadAcked) known |= 1 << fr.Dest;
            }
            fr.Text = CecDecoder.FrameText(fr);
            if (OnFrame != null) OnFrame(fr);
        }

        public void Finish()
        {
            if (f != null) { f.Partial = true; Done(f); f = null; }
        }

        public int Spikes;              // all spikes seen (in frames and between them)

        public void Feed(long fp, long rp)
        {
            double low = rp - fp;
            if (low <= CecDecoder.SpikeUs)                                    // spike: no bit, the frame goes on
            {
                Spikes++;
                if (f != null) f.Spikes++;
                Items.Add(new CecItem { A = fp, B = rp, Text = "spike", Kind = 4 });
                return;
            }
            long eomFall = lastFall;
            lastFall = fp;
            if (low >= 3300 && low <= 4200)                                  // start bit
            {
                if (f != null) { f.Partial = true; f.B = fp; Done(f); }
                f = new CecFrame { A = fp };
                Frames.Add(f);
                Items.Add(new CecItem { A = fp, B = rp, Text = "S", Kind = 0 });
                bit = 0; val = 0; prevFall = fp;
                return;
            }
            if (f == null) return;
            if (fp - prevFall > 5000)                                          // the next bit did not come (the start bit period is 4.5 ms)
            {
                f.Partial = true; f.B = prevFall; Done(f); f = null;
                return;
            }
            prevFall = fp;
            int b;
            if (low >= 300 && low <= 950) b = 1;
            else if (low >= 1150 && low <= 2100) b = 0;
            else
            {
                f.Error = true; f.B = fp;
                Items.Add(new CecItem { A = fp, B = rp, Text = "?", Kind = 3, Bad = true });
                Done(f); f = null;
                return;
            }
            int pos = bit % 10;
            if (pos == 0) { byteStart = fp; val = 0; }
            if (pos < 8) val = (val << 1) | b;
            else if (pos == 8) f.Eom = b == 1;
            else
            {
                bool bcast = f.Bytes.Count == 0 ? (val & 15) == 15 : f.Dest == 15;
                bool ack = bcast ? b == 1 : b == 0;
                f.Bytes.Add(val);
                if (f.Bytes.Count == 1) { f.Acked = ack; f.HeadAcked = ack; }
                else if (!ack) f.Acked = false;
                string tx = f.Bytes.Count == 1 ? CecDecoder.DevName((val >> 4) & 15) + ">" + CecDecoder.DevName(val & 15) : val.ToString("X2");
                Items.Add(new CecItem { A = byteStart, B = eomFall, Text = tx + (f.Eom ? " EOM" : ""), Kind = f.Bytes.Count == 1 ? 1 : 2 });
                Items.Add(new CecItem { A = fp, B = rp, Text = ack ? "A" : "N", Kind = 3, Bad = !ack });
                f.B = rp;
                if (f.Eom) { Done(f); f = null; bit = 0; return; }
            }
            bit++;
        }
    }
}