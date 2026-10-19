using System;
using System.Collections.Generic;

namespace HMLOG.App
{
    // Development aid for the capture scripts (HMLOG_DEMO=tx|rx): the lines a THCS board would send, so the BOARD and CEC
    // tabs have something to show without hardware. The serial write path goes to a sink that only takes the bytes.
    static class Demo
    {
        // status record of the board (every second, like the FPGA: the controls need a fresh echo)
        public static string Status(bool tx)
        {
            return MainWindow.CtrlTestLine(tx, tx ? new[] { 1, 12, 0 } : new[] { 3, 6, 0 }, false, 0, 3, 0, false);
        }

        // one-time lines: PS8419 read-back, TIA sweep is set by the caller, CEC messages and HPD edges
        public static List<string> Once(bool tx)
        {
            var l = new List<string>();
            long t = 400000;
            Action<long, int> ev = (us, code) => l.Add(string.Format("{0:X8} E {1:X2}", us, code));
            ev(t, 0x01); ev(t + 80000, 0x02);                       // HPD low, high
            if (tx)
            {
                int[] chip = Decoder.PsEqDefault;
                for (int r = 0; r < 5; r++) l.Add(string.Format("{0:X8} X {1:X2} {2:X2}", 1000000, 12 + r, chip[r]));
                l.Add(string.Format("{0:X8} X 00 23", 1000000));
            }
            long at = 1500000;
            at = Cec(l, at, 0x40, 0x04);                            // Playback 1 > TV: Image View On
            at = Cec(l, at + 400000, 0x40, 0x8F);                   // Give Device Power Status? (shows the decoder)
            at = Cec(l, at + 600000, 0x04, 0x90, 0x00);             // TV > Playback 1: Report Power Status (on)
            at = Cec(l, at + 700000, 0x4F, 0x82, 0x10, 0x00);       // Playback 1 > all: Active Source 1.0.0.0
            Cec(l, at + 500000, 0x40);                              // a poll
            return l;
        }

        // the edges of one frame: start bit, then per byte 8 bits + EOM + ACK (the follower pulls the ACK low)
        static long Cec(List<string> l, long t, params int[] bytes)
        {
            Action<long, bool> edge = (us, rise) => l.Add(string.Format("{0:X8} E {1:X2}", us, rise ? 0x0B : 0x0A));
            edge(t, false); edge(t + 3700, true); t += 4500;          // start bit
            for (int b = 0; b < bytes.Length; b++)
            {
                for (int i = 7; i >= 0; i--)
                {
                    bool one = (bytes[b] >> i & 1) != 0;
                    edge(t, false); edge(t + (one ? 600 : 1500), true); t += 2400;
                }
                bool eom = b == bytes.Length - 1;
                edge(t, false); edge(t + (eom ? 600 : 1500), true); t += 2400;          // EOM
                bool ack = (bytes[0] & 15) != 15 && bytes[0] >> 4 != (bytes[0] & 15);
                edge(t, false); edge(t + (ack ? 1500 : 600), true); t += 2400;          // ACK (0 = acknowledged)
            }
            return t;
        }
    }
}
