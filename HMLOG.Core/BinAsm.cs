// Binary records of common/source/dbg_i2c_uart.vhd put together into the
// text lines the decoder reads (and the log file keeps):
//   tttttttt I 54W 40 S 54R 41 3E E5- P | ! | ~
//   tttttttt E cc          tttttttt E FF llll          tttttttt S bb .. (12)
// A record starts with a byte with bit 7 = 1, the others have bit 7 = 0;
// 7 payload bits per byte, MSB first: P = kind(3) flag(1) byte(8) "00"
// time(32) "000". KS (0) / KV (4) have 7 bytes, KT (5) 31 (+ 24 status
// nibbles), KR (1) / KD (2) / KE (3) 2, KX (6) 8: P = kind flag value(8)
// index(8) "0" time(32) "000" -> "tttttttt X ii vv". A line without its end before the
// next KS / KV / KT is closed with " !"; KR / KD / KE without an open line
// are dropped (as tb dbg_bin_pkg.vhd).
// KW (7, logic analyzer, wave_cap.vhd): flag 1 = header (8 bytes, KX layout:
// value = byte, index = aux): aux 0 "tttttttt W H cc" (cc = trigger cause, time =
// trigger time), aux 1 "tttttttt W N vvv ttt" (valid entries, trigger index, both
// hex); flag 0 = one entry (3 bytes: kind flag entry(16) "0"), collected into
// "tttttttt W D xxxx xxxx .." lines of up to 16 entries (the time of the header).
using System.Collections.Generic;
using System.Text;

namespace HMLOG
{
    class BinAsm
    {
        readonly int[] rec = new int[32];
        int rn, need;
        readonly StringBuilder line = new StringBuilder();
        bool open;
        public int SyncErr;                       // records cut by the next start byte

        // logic analyzer entries of the capture going on
        string waveTs = "00000000";
        int waveLeft;                             // entries still to come (from the N header)
        readonly StringBuilder waveLine = new StringBuilder();
        int waveInLine;

        // one byte; completed lines are added to output
        public void Feed(int b, List<string> output)
        {
            if ((b & 0x80) != 0)
            {
                if (need != 0) SyncErr++;
                int kind = (b >> 4) & 7;
                need = kind == 0 || kind == 4 ? 7 : kind == 5 ? 31 : kind == 6 ? 8 : kind == 7 ? ((b & 8) != 0 ? 8 : 3) : 2;
                rec[0] = b;
                rn = 1;
            }
            else if (need != 0)
                rec[rn++] = b;
            if (need != 0 && rn == need)
            {
                Done(output);
                need = 0;
                rn = 0;
            }
        }

        void CloseCut(List<string> output)
        {
            if (!open) return;
            line.Append(" !");
            output.Add(line.ToString());
            open = false;
        }

        // the entries collected so far as one line
        void FlushWave(List<string> output)
        {
            if (waveInLine == 0) return;
            output.Add(waveTs + " W D" + waveLine);
            waveLine.Length = 0;
            waveInLine = 0;
        }

        void Done(List<string> output)
        {
            int k0 = (rec[0] >> 4) & 7;
            if (k0 != 7) FlushWave(output);                   // other records may come in between: the rest of the entries still follows
            if (k0 == 7)
            {
                if ((rec[0] & 8) != 0)                        // header
                {
                    FlushWave(output);
                    CloseCut(output);
                    ulong px = 0;
                    for (int i = 0; i < 8; i++) px = (px << 7) | (uint)(rec[i] & 0x7F);
                    int val = (int)(px >> 44) & 0xFF, idx = (int)(px >> 36) & 0xFF;
                    uint t = (uint)(px >> 3);
                    if (idx == 0)
                    {
                        waveTs = t.ToString("X8");
                        waveLeft = 0;
                        output.Add(waveTs + " W H " + val.ToString("X2"));
                    }
                    else
                    {
                        int valid = (int)((t & 0xF) << 8) | val, trig = (int)(t >> 4) & 0xFFF;
                        waveLeft = valid;
                        output.Add(waveTs + " W N " + valid.ToString("X3") + " " + trig.ToString("X3"));
                    }
                }
                else if (waveLeft > 0)                        // one entry
                {
                    int p21 = ((rec[0] & 0x7F) << 14) | ((rec[1] & 0x7F) << 7) | (rec[2] & 0x7F);
                    waveLine.Append(' ').Append(((p21 >> 1) & 0xFFFF).ToString("X4"));
                    waveInLine++;
                    waveLeft--;
                    if (waveInLine == 16 || waveLeft == 0) FlushWave(output);
                }
                return;
            }
            ulong p = 0;
            for (int i = 0; i < 7; i++)
                p = (p << 7) | (uint)(i < need ? rec[i] & 0x7F : 0);
            int kind = (int)(p >> 46) & 7, flag = (int)(p >> 45) & 1, bt = (int)(p >> 37) & 0xFF;
            uint tt = (uint)(p >> 3);
            string ts = tt.ToString("X8");
            string addr = (bt >> 1).ToString("X2") + ((bt & 1) != 0 ? "R" : "W");
            switch (kind)
            {
                case 0:                                       // KS
                    CloseCut(output);
                    line.Length = 0;
                    line.Append(ts).Append(" I ").Append(addr);
                    if (flag != 0) line.Append('-');
                    open = true;
                    break;
                case 1:                                       // KR
                    if (open) { line.Append(" S ").Append(addr); if (flag != 0) line.Append('-'); }
                    break;
                case 2:                                       // KD
                    if (open) { line.Append(' ').Append(bt.ToString("X2")); if (flag != 0) line.Append('-'); }
                    break;
                case 3:                                       // KE
                    if (open)
                    {
                        line.Append(flag == 0 ? " P" : (bt & 1) != 0 ? " ~" : " !");
                        output.Add(line.ToString());
                        open = false;
                    }
                    break;
                case 4:                                       // KV
                    CloseCut(output);
                    output.Add(flag != 0 ? ts + " E FF " + bt.ToString("X4") : ts + " E " + bt.ToString("X2"));
                    break;
                case 6:                                       // KX: value of another monitor
                    {
                        CloseCut(output);
                        ulong px = 0;
                        for (int i = 0; i < 8; i++) px = (px << 7) | (uint)(rec[i] & 0x7F);
                        int val = (int)(px >> 44) & 0xFF, idx = (int)(px >> 36) & 0xFF;
                        output.Add(((uint)(px >> 3)).ToString("X8") + " X " + idx.ToString("X2") + " " + val.ToString("X2"));
                        break;
                    }
                case 5:                                       // KT
                    {
                        CloseCut(output);
                        var s = new StringBuilder(ts + " S");
                        for (int k = 0; k < 12; k++)
                            s.Append(' ').Append((((rec[7 + 2 * k] & 15) << 4) | (rec[8 + 2 * k] & 15)).ToString("X2"));
                        output.Add(s.ToString());
                        break;
                    }
            }
        }
    }
}
