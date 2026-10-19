// OWM-HM-8B10B DDC debug log decoder (raw lines of common/source/dbg_i2c_uart.vhd)
//   tttttttt I 54W 31 46 P                 one I2C transfer (us time stamp)
//   tttttttt I 54W 40 S 54R 41 3E E5- P    repeated START, "-" = NACK
//   tttttttt E cc                          event (time it happened)
//   tttttttt E FF llll                     log FIFO full, llll records lost so far
//   tttttttt S bb ss vv vv vv vv hh ll ll c1 c1 c2   FPGA status (every second)
//   tttttttt X ii vv                       PS8419 register value (THCS TX)
//   # ...                                  PC notes in the saved log (connect, UART errors, PC time)
// aa = 7-bit address, R / W, S = repeated START, P = STOP, ! = cut off,
// ~ = bytes of the transfer lost in the FPGA (log FIFO full).
// Devices: 0x50 EDID (0x30 segment pointer), 0x54 SCDC, 0x3A HDCP, 0x37 DDC/CI.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HMLOG
{
    enum RowKind { Xfer, Reply, Reg, Dump, Event, Training, Info, Edid }

    class Row
    {
        public RowKind Kind;
        public string Time = "";
        public string Type = "";
        public string Addr = "";
        public string Name = "";
        public string Value = "";
        public string Text = "";
        public Color Color = Color.Black;
        public Color Back = Color.Empty;
        public long Us = -1;              // time of rows that carry one
        public bool Changed;              // register value differs from the previous one
    }

    // one register byte of a transfer (SCDC / HDCP)
    class RegItem
    {
        public int Dev, Off, Value;
        public char Access;               // 'R' read, 'W' written
        public int Prev = -1;             // previous value of this register (-1 = first time)
        public string Name = "", Meaning = "";
        public bool Changed { get { return Prev >= 0 && Prev != Value; } }
        public int Key { get { return (Dev << 8) | Off; } }
    }

    // one I2C transfer or one event
    class Group
    {
        public int Id;
        public long Us;
        public List<Row> Rows = new List<Row>();
        public string Sig = "";           // transfer text without the time, for folding repeats
        public bool IsTransaction;
        public bool Training;             // SCDC FRL training registers
        public bool Error;                // NACK, cut off, error events
        public bool IsEvent;
        public List<int> Addrs = new List<int>();   // dev << 8 | register
        public bool HasResult;            // contains a training result (never folded)

        public string Kind = "";          // RD, WR (SCDC / HDCP registers), I2C (EDID, others), EVENT, ??
        public int Dev = -1;              // 7-bit address of the first segment
        public List<int> Devs = new List<int>();    // every device addressed (segment order)
        public int Addr = -1;             // RD / WR: dev << 8 | first register (-1 = unknown)
        public int Len;                   // data bytes (without address / offset)
        public string I2cText = "";       // I2C: "RD 50 (EDID) seg 01 off 80 x128"
        public string Status = "";        // ACK, NACK, cut off
        public string DevLabel = "";      // "SCDC", "HDCP", "EDID", ...
        public bool Cut;
        public List<RegItem> Regs = new List<RegItem>();
        public FrlAttempt TrainStart, TrainEnd;
        public string EventText = "";
        public int EventCode = -1;          // event: its code
        public string Text = "";          // I2C: data summary ("EDID 180: 00 FF ...")
        public List<string> EdidLines = new List<string>();   // EDID blocks completed here
        public DateTime Pc;               // PC time the line arrived (MinValue: from a file)
        public int Cat;                   // filter category: Decoder.CatScdc ...
        public int Session;               // session number (1 ..)
    }

    // one connection of the sink: from the log start or a sink HPD high on
    class Session
    {
        public int No, GroupId;
        public long Us;
        public DateTime Pc;
        public int TrainOk, TrainFail, Xfers;
        public int OkRate = -1;           // rate of the last training that ended OK
        public bool Tmds, Scramble;       // TMDS_Config written
        public HdcpAttempt Hdcp;          // last HDCP authentication
        public string End = "";           // why it ended (sink HPD low, ...)
    }

    // one HDCP authentication: 1.4 from the Aksv write, 2.x from AKE_Init
    class HdcpAttempt
    {
        public int No, Session, GroupId;
        public long Us;
        public string Ver = "";           // "1.4", "2.x"
        public List<string> Steps = new List<string>();
        public List<int> Reps = new List<int>();
        public List<bool> FromSink = new List<bool>();   // read from the sink (else written by the source)
        public int Result = -1;           // -1 running, 0 failed, 1 done (SKE_Send_Eks / R0' read)
        public string End = "";
        public long LastUs;               // time of the last step

        public void Step(string name, bool sink)
        {
            int k = Steps.Count - 1;
            if (k >= 0 && Steps[k] == name) { Reps[k]++; return; }
            Steps.Add(name); Reps.Add(1); FromSink.Add(sink);
        }
    }

    // a bit field of a register for the bit view
    class BitField
    {
        public int Hi, Lo;
        public string Name;
        public BitField(int hi, int lo, string name) { Hi = hi; Lo = lo; Name = name; }
        public int Of(int v) { return (v >> Lo) & ((1 << (Hi - Lo + 1)) - 1); }
    }

    class FrlAttempt
    {
        public int No, GroupId;          // GroupId: group of the FRL_Rate write
        public long StartUs, EndUs = -1;
        public int Rate, FfeLevels;
        public List<string> Ltp = new List<string>();     // distinct lane request sets, "5678"
        public List<int> LtpRep = new List<int>();        // how often each was read
        public int Updates;               // FLT_update seen
        public int FfeReq;                // FLT_update answered with an E request
        public bool SawF;                 // rate change requested
        public int Result = -1;           // -1 running, 0 failed, 1 OK (FRL_start)
        public string End = "";           // why it ended
        public long LastUs;               // last training register access
    }

    class RegState
    {
        public int Dev, Off, Value;
        public char Access;
        public long Us;
        public int Count;
    }

    class Stats
    {
        public int Lines, Xfers, Reads, Writes, I2c, Ack, Nack, AddrNack, DataNack, Cut, Unknown;
        public int Lost;                  // transfers with bytes lost in the FPGA ("~")
        public int FifoLost;              // records lost in the FPGA (count of the last E FF / status line)
        public int CedErr;                // CED reads with errors counted
        public int Stalls;                // HDCP / FRL attempts that stopped without a result
        public int StatusLines, StatusGaps, PcNotes, UartErr, DecodeErr;
        public int Beats;                 // heartbeat events (levels 1-2: no status line)
        public long BytesRd, BytesWr;
        public SortedDictionary<int, int> Devs = new SortedDictionary<int, int>();
        public int TrainOk, TrainFail;
        public SortedDictionary<int, int> Events = new SortedDictionary<int, int>();
    }

    class Decoder
    {
        public readonly List<string> Lines = new List<string>();      // decoded text
        public readonly List<Group> Groups = new List<Group>();
        public readonly List<FrlAttempt> Trainings = new List<FrlAttempt>();
        public readonly SortedDictionary<int, RegState> Regs = new SortedDictionary<int, RegState>();
        public Stats St = new Stats();
        public int Version;               // bumped on every change

        // live state for the status panel and the session / HDCP lists
        public DateTime FeedPc = DateTime.MinValue;       // PC time of the line being fed
        public int Hpd = -1, Power = -1, Link = -1;       // -1 unknown, 0 low / off / down, 1 high / on / up
        public readonly List<Session> Sessions = new List<Session>();
        public readonly List<HdcpAttempt> HdcpTries = new List<HdcpAttempt>();
        public readonly int[] CatCount = new int[6];
        public int Hdcp2Cap = -1;                          // HDCP2Version bit 2 (-1 not read)

        // logic analyzer captures (KW records, "W" lines) and the state in the status line (HM RX 0x23)
        public class WaveCap
        {
            public long Us;                                // trigger time (FPGA us)
            public DateTime Pc = DateTime.MinValue;        // PC time the header arrived (live)
            public int Cause, Valid, Trig = -1;            // cause bit mask, entries announced, index of the trigger entry
            public readonly List<int> Ent = new List<int>();   // state(4) & delta(12) per entry
            public bool Complete { get { return Valid > 0 && Ent.Count >= Valid; } }
            public bool Open;                              // entries still coming
            public bool Synthetic;                         // no capture: a time window around an I2C transfer of the log (WAVE tab)
            public void EnsureBuilt() { if (Pos == null || Pos.Length != Ent.Count) Build(); }
            // sample position (40 ns units) of each entry; the first entry is at 0
            public long[] Pos;
            public const double SampleUs = 20.0;           // one sample = 20 us
            public void Build()
            {
                Pos = new long[Ent.Count];
                long t = 0;
                for (int i = 0; i < Ent.Count; i++) { t += Ent[i] & 0xFFF; Pos[i] = t; }
            }
            public long TrigPos { get { return Pos != null && Trig >= 0 && Trig < Pos.Length ? Pos[Trig] : 0; } }
            public long EndPos { get { return Pos != null && Pos.Length > 0 ? Pos[Pos.Length - 1] : 0; } }
            public static string CauseText(int c)
            {
                var s = new List<string>();
                if ((c & 1) != 0) s.Add("manual");
                if ((c & 2) != 0) s.Add("CEC");
                if ((c & 4) != 0) s.Add("HPD edge");
                if ((c & 8) != 0) s.Add("5V edge");

                return s.Count > 0 ? string.Join(" + ", s.ToArray()) : "?";
            }
        }
        public List<WaveCap> Waves = new List<WaveCap>();
        // edges of the CEC line (events 0A fall / 0B rise) and of the sink HPD (events 01 low / 02 high): (FPGA us, level)
        public List<KeyValuePair<long, int>> CecEdges = new List<KeyValuePair<long, int>>();
        public List<KeyValuePair<long, int>> HpdEdges = new List<KeyValuePair<long, int>>();
        // this board's CEC drive (events 0C starts / 0D stops pulling the line low, CEC repeater): (FPGA us, 1 = driving)
        public List<KeyValuePair<long, int>> DrvEdges = new List<KeyValuePair<long, int>>();
        public bool DrvSeen;                               // the board sends drive events (RX with the drive output)
        public List<long> CecLostMarks = new List<long>(); // times of E FF (log records lost): holes in the CEC record
        public long LastUs;                                // newest FPGA time seen (us, continuous over the wrap)
        public int EdgeVer;                                // changes with every edge
        CecStream cecStream;                               // CEC bits -> frames for the log rows
        long cecFall = -1;
        const int EdgeMax = 60000;
        static void AddEdge(List<KeyValuePair<long, int>> l, long us, int level)
        {
            if (l.Count >= EdgeMax) l.RemoveRange(0, EdgeMax / 6);
            l.Add(new KeyValuePair<long, int>(us, level));
        }
        public int WaveVer;                                // changes with every entry line
        public int WaveState = -1, UartSel = -1;           // from the status line: 0 idle 1 armed 2 post 3 upload; 0 921600, 1 2 M, 2 3 M
        WaveCap waveCur;

        // FPGA status line (S): board, build, levels, counters; -1 / "" = none yet
        public int Board = -1, StatSeq = -1, StatBits = -1, LinkErr = -1, TiaErr = -1;
        public string Build = "";
        public DateTime StatPc = DateTime.MinValue;        // PC time of the last status line (live)
        public long StatUs = -1;
        public int LinkErrNew;                             // frame errors since the log start
        public const int LinkRateS = 10;
        public double LinkErrRate;                         // errors per second over the last LinkRateS s
        readonly List<KeyValuePair<long, int>> linkHist = new List<KeyValuePair<long, int>>();
        public long LinkErrUs = -1, CedUs = -1;            // last increase / last CED error
        public int TiaErrNew;                              // HM RX TIA I2C failures since the log start
        public long TiaErrUs = -1;                         // last increase
        public bool BusStuck;

        // runtime control echo of the THCS boards built with G_RT_CTRL (Rx 0x23 / 0x26: TIA,
        // Tx 0x24 / 0x25: VCSEL; V0.12 and V0.13 boards), in the status line counters: counter 1 = 1 present | ovr
        // (applied values differ from the build defaults) | err (a command was
        // rejected) | 0 | values; counter 2 low nibble = commands accepted (wraps).
        // Tx values: EQ(3) Imod(4) Imean(3) 00; Rx values: Vswing(2) TIAbias(5) 00000
        public int CtrlKind;                               // 0 = none, 1 = VCSEL (0x24 / 0x25), 2 = TIA (0x23 / 0x26)
        public static bool CtrlBoardTx(int b) { return b == 0x24 || b == 0x25; }
        public static bool CtrlBoardRx(int b) { return b == 0x23 || b == 0x26; }
        public int CtrlOvr, CtrlErr, CtrlCnt = -1;
        public bool PsAbsent;                              // TX: no PS8419 monitor in the build (counter 1 bit 12), the HPD output still works
        public int HpdOvr;                                 // HDMI HPD output (B12) override echoed by the TX: 0 auto, 1 forced low, 2 forced high
        public readonly int[] CtrlVal = new int[3];        // VCSEL: EQ, Imod, Imean / TIA: Vswing, TIAbias
        public int CtrlVer;                                // bumped on every echo
        public long CtrlUs = -1;
        public DateTime CtrlPc = DateTime.MinValue;
        public string Ps8419 = "";                         // last PS8419 status (P4:D6)
        public int Ps8419En = -1;
        public readonly int[] PsEq = { -1, -1, -1, -1, -1 };   // P6.3Dh 3Eh 3Fh 40h 41h read back (-1 = unknown / not read)
        public int PsEqVer;                                // bumped on every change of PsEq
        public int PsLink = -1;                            // EQ row of the link in use (0 12G, 1 10G, 2 8G, 3 6G, 4 3G), -1 unknown (P4.D6h)
        public string PsLinkName = "";
        public int PsRdVal;                                // result of the last register read request (record 41h / C1h)
        public bool PsRdNack;
        public int PsRdVer;                                // bumped on every result
        DateTime pcRef = DateTime.MinValue;                // "# pc" note: PC time at FPGA time pcRefUs
        long pcRefUs;
        int linkErr0 = -1, tiaErr0 = -1;
        // HDCP / FRL attempt without progress this long: stopped (us)
        public const long HdcpStallUs = 3000000, FrlStallUs = 2000000;
        HdcpAttempt hdcp;                                  // running HDCP authentication
        readonly List<string> hdcpPre = new List<string>();   // 1.4 steps before the Aksv write (Bcaps, Bksv, An)
        public const int CatScdc = 0, CatHdcp = 1, CatEdid = 2, CatI2c = 3, CatEvent = 4, CatCec = 5;

        // Hotplug / OPTICAL reset clears the log view, not the left cards: the card state (registers,
        // EDID, FPGA status, control echo, last training / HDCP) is taken over from the decoder it replaces
        public Decoder Prev;
        public int TrainShow, HdcpShow;                    // attempts before these indexes are not shown on the cards

        // optical link down: what was read from the sink (mode, FFE level, training, CED, HDCP, EDID) is gone
        void ClearSink()
        {
            Regs.Clear();
            Array.Clear(EdidData, 0, EdidData.Length);
            Array.Clear(EdidValid, 0, EdidValid.Length);
            Array.Clear(edHave, 0, edHave.Length);
            EdidVersion++;
            Hdcp2Cap = -1;
            TrainShow = Trainings.Count;
            HdcpShow = HdcpTries.Count;
        }
        public void CarryFrom(Decoder o)
        {
            Prev = o.Trainings.Count == 0 && o.HdcpTries.Count == 0 && o.Prev != null ? o.Prev : o;
            if (Prev == o) o.Prev = null;
            Hpd = o.Hpd; Power = o.Power; Link = o.Link; Hdcp2Cap = o.Hdcp2Cap;
            Board = o.Board; StatSeq = o.StatSeq; StatBits = o.StatBits; LinkErr = o.LinkErr; TiaErr = o.TiaErr;
            Build = o.Build; StatPc = o.StatPc; StatUs = o.StatUs;
            LinkErrNew = o.LinkErrNew; LinkErrUs = o.LinkErrUs; CedUs = o.CedUs;
            TiaErrNew = o.TiaErrNew; TiaErrUs = o.TiaErrUs; BusStuck = o.BusStuck;
            linkErr0 = o.linkErr0; tiaErr0 = o.tiaErr0;
            CtrlKind = o.CtrlKind; CtrlOvr = o.CtrlOvr; CtrlErr = o.CtrlErr; CtrlCnt = o.CtrlCnt; HpdOvr = o.HpdOvr; PsAbsent = o.PsAbsent;
            Array.Copy(o.CtrlVal, CtrlVal, CtrlVal.Length);
            CtrlVer = o.CtrlVer + 1; CtrlUs = o.CtrlUs; CtrlPc = o.CtrlPc;
            Ps8419 = o.Ps8419; Ps8419En = o.Ps8419En;
            Array.Copy(o.PsEq, PsEq, PsEq.Length); PsEqVer = o.PsEqVer; PsLink = o.PsLink; PsLinkName = o.PsLinkName;
            PsRdVal = o.PsRdVal; PsRdNack = o.PsRdNack; PsRdVer = o.PsRdVer;
            pcRef = o.pcRef; pcRefUs = o.pcRefUs;
            Waves = o.Waves; WaveVer = o.WaveVer + 1; WaveState = o.WaveState; UartSel = o.UartSel;
            CecEdges = o.CecEdges; HpdEdges = o.HpdEdges; DrvEdges = o.DrvEdges; DrvSeen = o.DrvSeen; CecLostMarks = o.CecLostMarks; LastUs = o.LastUs; EdgeVer = o.EdgeVer + 1;
            foreach (var kv in o.Regs) Regs[kv.Key] = kv.Value;
            Array.Copy(o.EdidData, EdidData, EdidData.Length);
            Array.Copy(o.EdidValid, EdidValid, EdidValid.Length);
            Array.Copy(o.edHave, edHave, edHave.Length);
            EdidVersion = o.EdidVersion + 1;
        }

        // EDID (E-DDC: segment pointer 0x30, data 0x50): bytes by segment * 256 + offset
        public readonly byte[] EdidData = new byte[32768];
        public readonly bool[] EdidValid = new bool[256];
        public int EdidVersion;
        readonly bool[] edHave = new bool[32768];

        readonly int[] ptr = new int[128];               // register pointer per device

        public const int DevEdid = 0x50, DevSeg = 0x30, DevScdc = 0x54, DevHdcp = 0x3A, DevCi = 0x37;

        static readonly Color CScdc = Color.FromArgb(0, 90, 160);
        static readonly Color CErr = Color.FromArgb(200, 30, 30);
        static readonly Color CEvt = Color.FromArgb(140, 40, 150);
        static readonly Color COk = Color.FromArgb(0, 130, 40);
        static readonly Color CGray = Color.Gray;
        static readonly Color CWarn = Color.FromArgb(176, 112, 0);
        static readonly Color CEdid = Color.FromArgb(0, 115, 100);

        // ------------------------------------------------------------------
        // names and value decoding
        // ------------------------------------------------------------------
        public static readonly Dictionary<int, string> ScdcNames = new Dictionary<int, string> {
            {0x01, "Sink_Version"}, {0x02, "Source_Version"},
            {0x10, "Update_0"}, {0x11, "Update_1"},
            {0x20, "TMDS_Config"}, {0x21, "Scrambler_Status"},
            {0x30, "Config_0"}, {0x31, "Config_1"}, {0x35, "Source_Test_Config"},
            {0x40, "Status_Flags_0"}, {0x41, "Status_Flags_1"}, {0x42, "Status_Flags_2"},
            {0x50, "Err_Det_0_L"}, {0x51, "Err_Det_0_H"}, {0x52, "Err_Det_1_L"}, {0x53, "Err_Det_1_H"},
            {0x54, "Err_Det_2_L"}, {0x55, "Err_Det_2_H"}, {0x56, "Err_Det_Checksum"},
            {0x57, "Err_Det_3_L"}, {0x58, "Err_Det_3_H"}, {0x59, "RS_Corr_L"}, {0x5A, "RS_Corr_H"},
            {0xC0, "Test_Config_0"},
            {0xD0, "Manufacturer_OUI_0"}, {0xD1, "Manufacturer_OUI_1"}, {0xD2, "Manufacturer_OUI_2"},
            {0xDB, "HW_Revision"}, {0xDC, "SW_Major_Rev"}, {0xDD, "SW_Minor_Rev"}
        };

        // HDCP port 0x3A: register blocks (start, length, name)
        static readonly object[] HdcpRegs = {
            0x00, 5, "Bksv", 0x08, 2, "Ri'", 0x0A, 1, "Pj'", 0x10, 5, "Aksv", 0x15, 1, "Ainfo",
            0x18, 8, "An", 0x20, 20, "V'", 0x40, 1, "Bcaps", 0x41, 2, "Bstatus", 0x43, 1, "KSV FIFO",
            0x50, 1, "HDCP2Version", 0x60, 1, "Write_Message", 0x70, 2, "RxStatus", 0x80, 1, "Read_Message"
        };

        // HDCP 2.x over DDC: Write_Message (0x60) and Read_Message (0x80) are
        // FIFOs, the offset does not increase within a message
        public const int HdcpWrMsg = 0x60, HdcpRdMsg = 0x80;

        static readonly Dictionary<int, string> HdcpMsgs = new Dictionary<int, string> {
            {2, "AKE_Init"}, {3, "AKE_Send_Cert"}, {4, "AKE_No_Stored_km"}, {5, "AKE_Stored_km"},
            {7, "AKE_Send_H_prime"}, {8, "AKE_Send_Pairing_Info"}, {9, "LC_Init"}, {10, "LC_Send_L_prime"},
            {11, "SKE_Send_Eks"}, {12, "RepeaterAuth_Send_ReceiverID_List"}, {15, "RepeaterAuth_Send_Ack"},
            {16, "RepeaterAuth_Stream_Manage"}, {17, "RepeaterAuth_Stream_Ready"}
        };

        // meaning of an HDCP register block (bytes in order), "" when none
        public static string HdcpMeaning(string block, List<int> b)
        {
            if (b.Count == 0) return "";
            switch (block)
            {
                case "Write_Message":
                case "Read_Message":
                    {
                        string m;
                        return HdcpMsgs.TryGetValue(b[0], out m) ? m : string.Format("msg_id {0}", b[0]);
                    }
                case "RxStatus":
                    if (b.Count < 2) return "";
                    {
                        int size = b[0] | ((b[1] & 3) << 8);
                        var f = new List<string>();
                        if ((b[1] & 4) != 0) f.Add("READY");
                        if ((b[1] & 8) != 0) f.Add("REAUTH_REQ");
                        return string.Format("size {0}{1}", size, f.Count > 0 ? ", " + string.Join(", ", f) : "");
                    }
                case "HDCP2Version": return (b[0] & 4) != 0 ? "HDCP 2.2" : "no HDCP 2.2";
                case "Bcaps":
                    {
                        var f = new List<string>();
                        if ((b[0] & 0x40) != 0) f.Add("repeater");
                        if ((b[0] & 0x20) != 0) f.Add("KSV FIFO ready");
                        if ((b[0] & 0x10) != 0) f.Add("400 kHz");
                        if ((b[0] & 0x02) != 0) f.Add("1.1 features");
                        if ((b[0] & 0x80) != 0) f.Add("HDMI");
                        return string.Join(", ", f);
                    }
            }
            return "";
        }

        // column widths of the aligned texts (log summary, text output)
        public const int HdcpNameW = 14, HdcpMeanW = 22, I2cTextW = 34, HexPerLine = 16;

        static readonly Dictionary<int, string[]> EventText = new Dictionary<int, string[]> {
            {0x00, new[] {"--- log start (FPGA reset) ---", "info"}},
            {0x01, new[] {"sink HPD: LOW", "evt"}},
            {0x02, new[] {"sink HPD: HIGH", "evt"}},
            {0x03, new[] {"5 V to the sink: OFF", "evt"}},
            {0x04, new[] {"5 V to the sink: ON", "evt"}},
            {0x05, new[] {"optical link DOWN", "evt"}},
            {0x06, new[] {"optical link UP", "evt"}},
            {0x07, new[] {"!! DDC bus stuck: SCL or SDA held low (no change)", "err"}},
            {0x08, new[] {"DDC bus moves again", "evt"}},
            {0xFF, new[] {"!! log FIFO full in the FPGA, lines were lost", "err"}}
        };

        public static string EventName(int c)
        {
            string[] e;
            if (EventText.TryGetValue(c, out e)) return e[0];
            return string.Format("event {0:X2}", c);
        }

        public static string DevName(int a)
        {
            switch (a)
            {
                case DevEdid: return "EDID";
                case DevSeg: return "EDID segment";
                case DevScdc: return "SCDC";
                case DevHdcp: return "HDCP";
                case DevCi: return "DDC/CI";
                default: return "";
            }
        }

        static string KindOf(int a)
        {
            switch (a)
            {
                case DevEdid: case DevSeg: return "EDID";
                case DevScdc: return "SCDC";
                case DevHdcp: return "HDCP";
                case DevCi: return "DDC/CI";
                default: return "I2C";
            }
        }

        // FRL_Rate (SCDC 0x31 3:0)
        public static string Rate(int r)
        {
            switch (r)
            {
                case 0: return "FRL off (TMDS)";
                case 1: return "3G 3L";
                case 2: return "6G 3L";
                case 3: return "6G 4L";
                case 4: return "8G 4L";
                case 5: return "10G 4L";
                case 6: return "12G 4L";
                default: return "rate " + r + " (reserved)";
            }
        }

        public static int Lanes(int r) { return r >= 1 && r <= 2 ? 3 : r >= 3 && r <= 6 ? 4 : 0; }

        // Ln_LTP_req nibble
        public static string LtpName(int n)
        {
            switch (n)
            {
                case 0: return "done";
                case 1: return "LTP1 all 1";
                case 2: return "LTP2 all 0";
                case 3: return "LTP3 Nyquist";
                case 4: return "LTP4 FFE test";
                case 5: case 6: case 7: case 8: return "LTP" + n + " LFSR" + (n - 5);
                case 0xE: return "FFE up";
                case 0xF: return "lower rate";
                default: return "LTP " + n + " (reserved)";
            }
        }

        static string Flags(int v, params object[] list)       // bit, text, ...
        {
            var r = new List<string>();
            for (int i = 0; i < list.Length; i += 2)
                if ((v & (1 << (int)list[i])) != 0) r.Add((string)list[i + 1]);
            return r.Count == 0 ? "-" : string.Join(", ", r);
        }

        public static string DecodeScdc(int a, int v)
        {
            switch (a)
            {
                case 0x01: case 0x02: return "version " + v;
                case 0x10: return Flags(v, 0, "Status_Update", 1, "CED_Update", 2, "RR_Test", 3, "Source_Test_Update",
                                        4, "FRL_start", 5, "FLT_update", 6, "RSED_Update");
                case 0x11: return Flags(v, 0, "Source_Test_Update");
                case 0x20:
                    return string.Format("scrambling {0}, clock ratio 1/{1}", (v & 1) != 0 ? "on" : "off", (v & 2) != 0 ? 40 : 10);
                case 0x21: return (v & 1) != 0 ? "scrambling detected" : "no scrambling";
                case 0x30: return Flags(v, 0, "RR_Enable", 1, "FLT_no_retrain");
                case 0x31: return string.Format("{0}, FFE levels {1}", Rate(v & 0xF), v >> 4);
                case 0x40: return Flags(v, 0, "Clock_Detected", 1, "Ln0_Locked", 2, "Ln1_Locked", 3, "Ln2_Locked",
                                        4, "Ln3_Locked", 5, "DSC_Decode_Fail", 6, "FLT_ready");
                case 0x41: return string.Format("Ln0 {0}  Ln1 {1}", LtpName(v & 0xF), LtpName(v >> 4));
                case 0x42: return string.Format("Ln2 {0}  Ln3 {1}", LtpName(v & 0xF), LtpName(v >> 4));
                case 0x51: case 0x53: case 0x55: case 0x58: case 0x5A:
                    return (v & 0x80) != 0 ? "valid" : "not valid";
            }
            return "";
        }

        static string HdcpName(int off, out int start)
        {
            for (int i = 0; i < HdcpRegs.Length; i += 3)
            {
                int s = (int)HdcpRegs[i], n = (int)HdcpRegs[i + 1];
                if (off >= s && off < s + n) { start = s; return (string)HdcpRegs[i + 2]; }
            }
            start = off;
            return "";
        }

        // ------------------------------------------------------------------
        // state
        // ------------------------------------------------------------------
        FrlAttempt cur;                   // running FRL training attempt
        int frlRate;
        bool updPend;                     // FLT_update read, LTP request not read yet
        int[] ltp = new int[] { -1, -1, -1, -1 };
        long wrap, tlast = -1;
        long pendT = -1, pendWrap;        // a large step of the time not confirmed yet (ToUs)
        bool anchorSeen;                  // a status line or heartbeat came (they confirm large steps)
        bool timeSuspect;                 // the time of the record being fed is not confirmed (ToUs: a large step)
        public int SuspectEdges;          // CEC / drive / HPD edges left out of the waveforms for such a time

        public static string FmtTime(long us)
        {
            return string.Format("[{0,5}.{1:D6}]", us / 1000000, us % 1000000);
        }

        // 32-bit us time, wraps after 71.6 min. A step from the last time of up to SmallFwd forward or LateUs back
        // (events carry the time they happened and wait while a transfer line is open), also across the wrap, is
        // taken at once. A larger step (a gap of the log, or a record with a broken time after UART sync errors)
        // is taken only when a status line (S) or heartbeat (E 09) goes on from it: they come every second with
        // the time they are sent, while the records of a burst of UART errors can carry broken times that agree
        // with each other (one broken record gives an X and an I line). A broken record never confirms, so it
        // changes neither the wrap count nor the newest time. Logs without status lines and heartbeats: any
        // record confirms. A step back of more than half the period is a wrap (as
        // before); a smaller one is a restart of the FPGA time whose reset event was lost (the time goes back).
        const long LateUs = 10000000, SmallFwd = 120000000, Period = 4294967296L;

        // t from the state (w, tl): the time, the wrap count after it and whether the step is small
        static long Interpret(long t, long w, long tl, out long nw, out bool small)
        {
            long d = t - tl;
            nw = w;
            small = true;
            if (d >= -LateUs && d <= SmallFwd) return w + t;
            if (d > Period - LateUs) return w - Period + t;                  // late time from before the wrap
            if (d < SmallFwd - Period) { nw = w + Period; return nw + t; }   // the wrap, a small step over it
            small = false;
            if (d < -0x80000000L) nw = w + Period;                            // more than half the period back: a wrap
            return nw + t;
        }

        long ToUs(string hex, bool anchor)
        {
            long t = long.Parse(hex, NumberStyles.HexNumber);
            if (anchor) anchorSeen = true;
            timeSuspect = false;
            if (tlast < 0) { tlast = t; pendT = -1; return Take(wrap + t); }
            long nw;
            bool small;
            long r = Interpret(t, wrap, tlast, out nw, out small);
            // over the wrap: also confirmed by a status line / heartbeat (a broken time just after the wrap looks
            // the same); the records in between are shown with the wrapped time
            if (small && nw != wrap && anchorSeen && !anchor) small = false;
            if (small)
            {
                pendT = -1;
                Step(t, r, nw);
                return Take(r);
            }
            if (pendT >= 0 && (anchor || !anchorSeen))
            {
                long nw2;
                bool small2;
                long r2 = Interpret(t, pendWrap, pendT, out nw2, out small2);
                if (small2)                                   // the large step was real: the log goes on from it
                {
                    wrap = pendWrap; tlast = pendT;
                    Take(pendWrap + pendT);
                    pendT = -1;
                    Step(t, r2, nw2);
                    return Take(r2);
                }
            }
            pendT = t; pendWrap = nw;
            timeSuspect = true;
            return r;                                         // shown with its own time, the state stays
        }

        // a small step taken: over the wrap, or forward in this period (a late time keeps the last time)
        void Step(long t, long r, long nw)
        {
            if (nw != wrap) { wrap = nw; tlast = t; }
            else if (r == wrap + t && t > tlast) tlast = t;
        }

        long Take(long r)
        {
            if (r > LastUs) LastUs = r;
            return r;
        }

        Group NewGroup(long us)
        {
            var pc = FeedPc;
            if (pc == DateTime.MinValue && pcRef != DateTime.MinValue && us >= 0)
                pc = pcRef.AddTicks((us - pcRefUs) * 10);       // file: from the "# pc" notes
            var g = new Group { Id = Groups.Count, Us = us, Pc = pc, Cat = CatEvent };
            if (Sessions.Count == 0) NewSession(g);
            g.Session = Sessions[Sessions.Count - 1].No;
            CatCount[CatEvent]++;
            Groups.Add(g);
            return g;
        }

        Session CurSession { get { return Sessions[Sessions.Count - 1]; } }

        void NewSession(Group g)
        {
            Sessions.Add(new Session { No = Sessions.Count + 1, GroupId = g.Id, Us = g.Us, Pc = g.Pc });
            g.Session = Sessions.Count;
        }

        void Add(Group g, Row r, string line)
        {
            g.Rows.Add(r);
            Lines.Add(line);
        }

        // ------------------------------------------------------------------
        // registers (SCDC, HDCP)
        // ------------------------------------------------------------------
        // line = false: the caller writes the line (HDCP: one line per register block)
        void EmitReg(Group g, int dev, int off, int v, char access, long us, bool line)
        {
            int key = (dev << 8) | off;
            RegState rs;
            int prev = -1;
            if (Regs.TryGetValue(key, out rs)) prev = rs.Value;
            else { rs = new RegState { Dev = dev, Off = off }; Regs[key] = rs; }
            rs.Value = v; rs.Access = access; rs.Us = us; rs.Count++;
            if (!g.Addrs.Contains(key)) g.Addrs.Add(key);

            string name = "", mean = "";
            if (dev == DevScdc)
            {
                ScdcNames.TryGetValue(off, out name);
                if (name == null) name = "";
                mean = DecodeScdc(off, v);
                if (access == 'W' && (off == 0x10 || off == 0x11)) mean = "clear: " + mean;   // write 1 to clear
            }
            else if (dev == DevHdcp)
            {
                int s;
                name = HdcpName(off, out s);
                if (name.Length > 0 && off != s) name += "+" + (off - s);
            }
            var item = new RegItem { Dev = dev, Off = off, Value = v, Access = access, Prev = prev, Name = name, Meaning = mean };
            g.Regs.Add(item);
            if (!line) return;
            string l = string.Format("    {0:X2}:{1:X2} {2,-20} = {3:X2}  {4}", dev, off, name, v, mean).TrimEnd();
            Add(g, new Row { Kind = RowKind.Reg, Addr = string.Format("{0:X2}:{1:X2}", dev, off), Name = name,
                             Value = v.ToString("X2"), Text = mean, Changed = item.Changed }, l);
        }

        // ------------------------------------------------------------------
        // FRL link training (HDMI 2.1 LTS3 / LTS4):
        //   source writes FRL_Rate (0x31); sink sets FLT_update (0x10 bit 5) and
        //   per-lane LTP requests (0x41 / 0x42): 1..8 patterns, E = FFE up,
        //   F = lower rate, 0 = lane done; FRL_start (0x10 bit 4) = trained.
        //   Rate 0 written = FRL off (the source stops or falls back to TMDS).
        // ------------------------------------------------------------------
        void EndAttempt(Group g, long us, int result, string why)
        {
            if (cur == null) return;
            cur.Result = result;
            cur.EndUs = us;
            cur.End = why;
            g.TrainEnd = cur;
            g.Training = true;
            g.HasResult = true;
            bool ok = result == 1;
            if (ok) St.TrainOk++; else { St.TrainFail++; g.Error = true; }
            if (ok) { CurSession.TrainOk++; CurSession.OkRate = cur.Rate; } else CurSession.TrainFail++;
            string text = string.Format("== FRL TRAINING {0}: {1}, {2:F1} ms, FFE up {3}x{4} ==",
                ok ? "OK" : "FAILED", Rate(cur.Rate), (us - cur.StartUs) / 1000.0, cur.FfeReq,
                why.Length > 0 ? ", " + why : "");
            Add(g, new Row { Kind = RowKind.Training, Time = FmtTime(us), Us = us, Type = "FRL", Text = text,
                             Color = ok ? COk : CErr, Back = ok ? Color.FromArgb(225, 245, 228) : Color.FromArgb(252, 225, 225) },
                FmtTime(us) + " " + text);
            cur = null;
        }

        void TrackScdcWrite(Group g, int off, int v, long us)
        {
            if (off == 0x20) { CurSession.Tmds = true; CurSession.Scramble = (v & 1) != 0; }
            if (cur != null) cur.LastUs = us;
            if (off != 0x31) return;
            int r = v & 0xF;
            g.Training = true;
            if (cur != null && r == cur.Rate) return;               // same rate written again
            if (cur != null)
                EndAttempt(g, us, 0, cur.SawF ? "sink asked for a lower rate" : r == 0 ? "source stopped (rate 0)" : "source changed the rate");
            frlRate = r;
            ltp = new int[] { -1, -1, -1, -1 };
            if (r == 0) return;
            cur = new FrlAttempt { No = Trainings.Count + 1, GroupId = g.Id, StartUs = us, Rate = r, FfeLevels = v >> 4, LastUs = us };
            Trainings.Add(cur);
            g.TrainStart = cur;
        }

        void TrackScdcRead(Group g, int off, int v, long us)
        {
            if (cur != null) cur.LastUs = us;
            switch (off)
            {
                case 0x51: case 0x53: case 0x55: case 0x58:
                    {
                        // CED: count of lane n (low byte read before), valid bit 7
                        int lo = RegLast(off - 1);
                        int n = ((v & 0x7F) << 8) | (lo < 0 ? 0 : lo);
                        if ((v & 0x80) != 0 && n > 0)
                        {
                            int lane = off == 0x51 ? 0 : off == 0x53 ? 1 : off == 0x55 ? 2 : 3;
                            g.Error = true;
                            St.CedErr++;
                            CedUs = us;
                            string t = string.Format("!! CED: lane {0} character errors {1}", lane, n);
                            Add(g, new Row { Kind = RowKind.Info, Time = FmtTime(us), Us = us, Type = "CED", Text = t, Color = CErr }, FmtTime(us) + " " + t);
                        }
                        break;
                    }
                case 0x10:
                    if ((v & 0x30) != 0) g.Training = true;
                    if ((v & 0x20) != 0 && cur != null) { cur.Updates++; updPend = true; }
                    if ((v & 0x10) != 0 && cur != null) EndAttempt(g, us, 1, "");
                    break;
                case 0x40:
                    if ((v & 0x40) != 0) g.Training = true;
                    break;
                case 0x41: case 0x42:
                    {
                        g.Training = true;
                        int l = 2 * (off - 0x41);
                        ltp[l] = v & 0xF;
                        ltp[l + 1] = v >> 4;
                        if (cur == null) break;
                        int n = Lanes(cur.Rate);
                        if (off == 0x42 || n == 3 && off == 0x41) { }   // set complete after 0x42 (or 0x41 for 3 lanes)
                        else break;
                        var b = new StringBuilder();
                        bool e = false, f = false;
                        for (int i = 0; i < n; i++)
                        {
                            b.Append(ltp[i] < 0 ? "?" : ltp[i].ToString("X"));
                            if (ltp[i] == 0xE) e = true;
                            if (ltp[i] == 0xF) f = true;
                        }
                        string s = b.ToString();
                        int k = cur.Ltp.Count - 1;
                        if (k >= 0 && cur.Ltp[k] == s) cur.LtpRep[k]++;
                        else { cur.Ltp.Add(s); cur.LtpRep.Add(1); }
                        if (updPend && e) cur.FfeReq++;
                        updPend = false;
                        if (f) cur.SawF = true;
                        break;
                    }
            }
        }

        int RegLast(int off)
        {
            RegState rs;
            return Regs.TryGetValue((DevScdc << 8) | off, out rs) ? rs.Value : -1;
        }

        public static string LtpSeq(FrlAttempt t)
        {
            var p = new List<string>();
            for (int i = 0; i < t.Ltp.Count; i++)
                p.Add(t.LtpRep[i] > 1 ? t.Ltp[i] + "×" + t.LtpRep[i] : t.Ltp[i]);
            return p.Count == 0 ? "-" : string.Join(" → ", p);
        }

        // ------------------------------------------------------------------
        // EDID (0x30 segment, 0x50 offset / data)
        // ------------------------------------------------------------------
        string EdidRead(Group g, int seg, List<int> data, long us)
        {
            int off = ptr[DevEdid];
            int start = seg * 256 + off;
            var done = new List<int>();
            for (int i = 0; i < data.Count; i++)
            {
                int a = (seg * 256 + ((off + i) & 0xFF)) & 0x7FFF;
                EdidData[a] = (byte)data[i];
                edHave[a] = true;
                int k = a >> 7;
                if (!done.Contains(k))
                {
                    bool full = true;
                    for (int j = k * 128; j < k * 128 + 128; j++) if (!edHave[j]) { full = false; break; }
                    if (full) done.Add(k);
                }
            }
            ptr[DevEdid] = (off + data.Count) & 0xFF;
            string dump = string.Format("EDID {0:X3}: {1}", start, Hex(data, 0, 16));
            done.Sort();
            foreach (int k in done)
            {
                for (int j = k * 128; j < k * 128 + 128; j++) edHave[j] = false;
                EdidValid[k] = true;
                EdidVersion++;
                string s = string.Format("== EDID block {0}: {1} ==", k,
                    k == 0 ? Edid.Block0Summary(EdidData, 0) : Edid.ExtSummary(EdidData, k * 128));
                g.EdidLines.Add(s);
                Add(g, new Row { Kind = RowKind.Edid, Time = FmtTime(us), Us = us, Type = "EDID", Text = s,
                                 Color = CEdid, Back = Color.FromArgb(222, 244, 240) }, FmtTime(us) + " " + s);
            }
            return dump;
        }

        static string Hex(List<int> b, int from, int max)
        {
            var l = new List<string>();
            for (int i = from; i < b.Count && i < from + max; i++) l.Add(b[i].ToString("X2"));
            string s = string.Join(" ", l);
            if (b.Count - from > max) s += string.Format(" … ({0} bytes)", b.Count - from);
            return s;
        }

        // ------------------------------------------------------------------
        // transfers
        // ------------------------------------------------------------------
        class Seg
        {
            public int Dev;
            public bool Read, AddrNack;
            public List<int> Data = new List<int>();
            public List<bool> Nack = new List<bool>();
        }

        // tokens of a transfer line, parsed by hand (a regular expression per byte made the I2C lines the
        // slowest part of opening a log: an HDCP 2.2 certificate read is one line of 539 tokens)
        static int HexDigit(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

        // "54W", "3AR-" (address not acknowledged)
        static bool ParseAddr(string t, out int dev, out bool read, out bool nack)
        {
            dev = 0; read = false; nack = false;
            if (t.Length != 3 && !(t.Length == 4 && t[3] == '-')) return false;
            int h = HexDigit(t[0]), l = HexDigit(t[1]);
            if (h < 0 || l < 0 || (t[2] != 'R' && t[2] != 'W')) return false;
            dev = h * 16 + l; read = t[2] == 'R'; nack = t.Length == 4;
            return true;
        }

        // "41", "E5-" (byte not acknowledged)
        static bool ParseByte(string t, out int v, out bool nack)
        {
            v = 0; nack = false;
            if (t.Length != 2 && !(t.Length == 3 && t[2] == '-')) return false;
            int h = HexDigit(t[0]), l = HexDigit(t[1]);
            if (h < 0 || l < 0) return false;
            v = h * 16 + l; nack = t.Length == 3;
            return true;
        }

        // the time stamp: 8 hex digits
        static bool IsTime(string t)
        {
            if (t.Length != 8) return false;
            for (int i = 0; i < 8; i++) if (HexDigit(t[i]) < 0) return false;
            return true;
        }

        void OnXfer(long us, string[] f, string sig, string line)
        {
            var segs = new List<Seg>();
            bool cut = false, stop = false, bad = false, lost = false;
            Seg s = null;
            for (int i = 2; i < f.Length; i++)
            {
                string t = f[i];
                int dev, v;
                bool rd, nk;
                if (t == "P") { stop = true; break; }
                if (t == "!") { cut = true; break; }
                if (t == "~") { lost = true; break; }
                if (t == "S") { s = null; continue; }
                if (s == null && ParseAddr(t, out dev, out rd, out nk))
                {
                    s = new Seg { Dev = dev, Read = rd, AddrNack = nk };
                    segs.Add(s);
                    continue;
                }
                if (s != null && ParseByte(t, out v, out nk))
                {
                    s.Data.Add(v);
                    s.Nack.Add(nk);
                    continue;
                }
                bad = true;
            }
            if (segs.Count == 0 || bad || !(stop || cut || lost))
            {
                var g0 = NewGroup(us);
                Add(g0, new Row { Kind = RowKind.Info, Time = FmtTime(us), Us = us, Type = "??", Text = "?? " + line, Color = CErr },
                    "?? " + line);
                g0.Error = true;
                St.Unknown++;
                return;
            }

            var g = NewGroup(us);
            g.IsTransaction = true;
            g.Sig = sig;
            foreach (var x in segs) if (!g.Devs.Contains(x.Dev)) g.Devs.Add(x.Dev);
            // the device that carries the data: EDID reads start with the 0x30 segment pointer
            int main = segs[0].Dev;
            foreach (var x in segs) if (x.Dev != DevSeg) { main = x.Dev; break; }
            g.Dev = main;
            g.DevLabel = DevName(main);
            g.Cat = main == DevScdc ? CatScdc : main == DevHdcp ? CatHdcp : main == DevEdid || main == DevSeg ? CatEdid : CatI2c;
            CurSession.Xfers++;
            CatCount[CatEvent]--;                            // NewGroup counted it as an event / other line
            CatCount[g.Cat]++;
            bool regDev = main == DevScdc || main == DevHdcp;
            g.Cut = cut || lost;
            St.Xfers++;
            int n;
            St.Devs.TryGetValue(main, out n);
            St.Devs[main] = n + 1;

            // rows added while decoding (registers, EDID blocks, training) come
            // after the request / reply / data rows inserted at the end
            int rowsAt = g.Rows.Count, linesAt = Lines.Count;
            string status = "ACK";
            bool addrNack = false, anyRead = false;
            int seg = 0, rdOff = -1, wrOff = -1, ptrOff = -1, rdLen = 0, wrLen = 0;
            var info = new List<string>();                       // I2C: "seg 01", "off 80"
            var dump = new List<string>();                       // I2C: data lines
            foreach (var x in segs)
            {
                if (x.AddrNack) { addrNack = true; status = "NACK"; St.AddrNack++; g.Error = true; if (x.Read) anyRead = true; continue; }
                if (x.Read) anyRead = true;
                if (!x.Read)
                {
                    for (int i = 0; i < x.Nack.Count; i++)
                        if (x.Nack[i]) { status = "NACK"; St.DataNack++; g.Error = true; break; }
                    if (x.Data.Count == 0) continue;
                    if (x.Dev == DevSeg) { seg = x.Data[0]; info.Add(string.Format("seg {0:X2}", seg)); continue; }
                    if (x.Dev == DevEdid || (regDev && x.Dev == main))
                    {
                        int off0 = x.Data[0], cnt = x.Data.Count - 1;
                        ptr[x.Dev] = off0;
                        if (ptrOff < 0) ptrOff = off0;
                        St.BytesWr += cnt;
                        if (x.Dev == DevEdid)
                        {
                            info.Add(string.Format("off {0:X2}", off0));
                            if (cnt > 0) { wrLen += cnt; dump.Add("write: " + Hex(x.Data, 1, 16)); }
                            continue;
                        }
                        if (cnt > 0 && wrOff < 0) wrOff = off0;
                        wrLen += cnt;
                        bool fifo = x.Dev == DevHdcp && off0 == HdcpWrMsg;   // offset stays
                        for (int i = 1; i < x.Data.Count; i++)
                        {
                            int off = fifo ? off0 : (off0 + i - 1) & 0xFF, v = x.Data[i];
                            EmitReg(g, x.Dev, off, v, 'W', us, x.Dev == DevScdc);
                            if (x.Dev == DevScdc) TrackScdcWrite(g, off, v, us);
                        }
                        ptr[x.Dev] = fifo ? off0 : (off0 + cnt) & 0xFF;
                        if (cnt == 0 && !g.Addrs.Contains((x.Dev << 8) | off0)) g.Addrs.Add((x.Dev << 8) | off0);
                    }
                    else
                    {
                        wrLen += x.Data.Count;
                        St.BytesWr += x.Data.Count;
                        dump.Add(string.Format("write {0:X2}: {1}", x.Dev, Hex(x.Data, 0, 16)));
                    }
                }
                else
                {
                    if (x.Data.Count == 0) continue;
                    St.BytesRd += x.Data.Count;
                    if (x.Dev == DevEdid)
                    {
                        if (rdOff < 0) rdOff = seg * 256 + ptr[DevEdid];
                        rdLen += x.Data.Count;
                        dump.Add(EdidRead(g, seg, x.Data, us));
                    }
                    else if (regDev && x.Dev == main)
                    {
                        if (rdOff < 0) rdOff = ptr[x.Dev];
                        rdLen += x.Data.Count;
                        bool fifo = x.Dev == DevHdcp && ptr[x.Dev] == HdcpRdMsg;   // offset stays
                        for (int i = 0; i < x.Data.Count; i++)
                        {
                            int off = fifo ? ptr[x.Dev] : (ptr[x.Dev] + i) & 0xFF;
                            EmitReg(g, x.Dev, off, x.Data[i], 'R', us, x.Dev == DevScdc);
                            if (x.Dev == DevScdc) TrackScdcRead(g, off, x.Data[i], us);
                        }
                        if (!fifo) ptr[x.Dev] = (ptr[x.Dev] + x.Data.Count) & 0xFF;
                    }
                    else
                    {
                        rdLen += x.Data.Count;
                        dump.Add(string.Format("read {0:X2}: {1}", x.Dev, Hex(x.Data, 0, 16)));
                    }
                }
            }
            if (lost) { status = "bytes lost (FPGA log FIFO full)"; St.Lost++; g.Error = true; }
            else if (cut) { status = "cut off"; St.Cut++; g.Error = true; }
            else if (status == "ACK") St.Ack++;
            else St.Nack++;
            g.Status = status;

            // as DpcdLog: register transactions RD / WR, everything else I2C
            string head, label = g.DevLabel.Length > 0 ? "  (" + g.DevLabel + ")" : "";
            if (regDev)
            {
                g.Kind = anyRead ? "RD" : "WR";
                int off = anyRead ? rdOff : wrOff >= 0 ? wrOff : ptrOff;
                g.Addr = off >= 0 ? (main << 8) | off : -1;
                g.Len = anyRead ? rdLen : wrLen;
                if (anyRead) St.Reads++; else St.Writes++;
                head = string.Format("{0} {1} x{2}{3}", g.Kind, AddrText(g), g.Len, label);
            }
            else
            {
                g.Kind = "I2C";
                g.Len = rdLen + wrLen;
                St.I2c++;
                g.I2cText = string.Format("{0} {1:X2}{2}{3} x{4}", anyRead ? "RD" : "WR", main, label.Length > 0 ? " (" + g.DevLabel + ")" : "",
                                          info.Count > 0 ? " " + string.Join(" ", info) : "", g.Len);
                head = "I2C " + g.I2cText;
                g.Text = string.Join("  ", dump);
            }
            string reply = addrNack ? "NACK (no device)" : status;
            var front = new List<Row>();
            var frontLines = new List<string>();
            front.Add(new Row { Kind = RowKind.Xfer, Time = FmtTime(us), Us = us, Type = g.Kind, Addr = AddrText(g),
                                Text = regDev ? string.Format("x{0}{1}", g.Len, label) : g.I2cText,   // address has its column
                                Color = g.Error ? CErr : regDev ? CScdc : CEdid });
            frontLines.Add(FmtTime(us) + " " + head);
            front.Add(new Row { Kind = RowKind.Reply, Type = "->", Text = reply, Color = status == "ACK" ? Color.Black : CErr });
            frontLines.Add("    -> " + reply);
            foreach (string d in dump)
            {
                front.Add(new Row { Kind = RowKind.Dump, Text = d, Color = CGray });
                frontLines.Add("    " + d);
            }
            if (main == DevHdcp) { HdcpLines(g, front, frontLines); if (!addrNack) TrackHdcp(g, us); }
            g.Rows.InsertRange(rowsAt, front);
            Lines.InsertRange(linesAt, frontLines);
        }

        public class HdcpBlock
        {
            public string Name;
            public int Start;
            public List<int> Bytes = new List<int>();
            public string Meaning { get { return HdcpMeaning(Name, Bytes); } }
        }

        // HDCP registers of a transfer as blocks (Bksv 5 bytes, a whole message
        // of the Write_Message / Read_Message FIFO, ...)
        public static List<HdcpBlock> HdcpBlocks(Group g)
        {
            var r = new List<HdcpBlock>();
            HdcpBlock cur = null;
            foreach (var x in g.Regs)
            {
                if (x.Dev != DevHdcp) continue;
                int s0;
                string bn = HdcpName(x.Off, out s0);
                if (bn.Length == 0) bn = "(hex)";
                bool fifo = x.Off == HdcpWrMsg || x.Off == HdcpRdMsg;
                if (cur == null || bn != cur.Name || (!fifo && x.Off != cur.Start + cur.Bytes.Count))
                {
                    cur = new HdcpBlock { Name = bn, Start = x.Off };
                    r.Add(cur);
                }
                cur.Bytes.Add(x.Value);
            }
            return r;
        }

        // hex lines of HexPerLine bytes
        public static List<string> HexLines(List<int> b)
        {
            var r = new List<string>();
            for (int i = 0; i < b.Count; i += HexPerLine)
            {
                var l = new List<string>();
                for (int k = i; k < b.Count && k < i + HexPerLine; k++) l.Add(b[k].ToString("X2"));
                r.Add(string.Join(" ", l));
            }
            return r;
        }

        // HDCP: one block per line, meaning and data in their own columns; long
        // messages go on as more lines of 16 bytes under the data column
        //   3A:60 Write_Message        = AKE_Init             02 A1 B2 ...
        static void HdcpLines(Group g, List<Row> rows, List<string> lines)
        {
            foreach (var b in HdcpBlocks(g))
            {
                var hex = HexLines(b.Bytes);
                string mean = b.Meaning;
                string head = string.Format("    {0:X2}:{1:X2} {2,-20} = {3,-" + HdcpMeanW + "} ", DevHdcp, b.Start, b.Name, mean);
                rows.Add(new Row { Kind = RowKind.Reg, Addr = string.Format("{0:X2}:{1:X2}", DevHdcp, b.Start), Name = b.Name,
                                   Value = (mean.Length > 0 ? mean.PadRight(HdcpMeanW) + " " : "") + hex[0] });
                lines.Add(head + hex[0]);
                for (int k = 1; k < hex.Count; k++)
                {
                    string more = string.Format("+{0:X2}", k * HexPerLine);
                    rows.Add(new Row { Kind = RowKind.Dump, Text = new string(' ', HdcpMeanW + 24) + hex[k], Color = CGray });
                    lines.Add(new string(' ', head.Length - more.Length - 1) + more + " " + hex[k]);
                }
            }
        }

        // ------------------------------------------------------------------
        // HDCP authentication steps
        //   2.x: messages through Write_Message (source) / Read_Message (sink),
        //        AKE_Init starts, SKE_Send_Eks = authenticated, REAUTH_REQ fails
        //   1.4: Bcaps / Bksv read, An / Aksv written, first Ri' read (R0') = done
        // ------------------------------------------------------------------
        void StartHdcp(Group g, long us, string ver)
        {
            if (hdcp != null && hdcp.Result < 0) { hdcp.Result = 0; hdcp.End = "restarted"; }
            hdcp = new HdcpAttempt { No = HdcpTries.Count + 1, Session = g.Session, GroupId = g.Id, Us = us, Ver = ver, LastUs = us };
            HdcpTries.Add(hdcp);
            CurSession.Hdcp = hdcp;
        }

        void TrackHdcp(Group g, long us)
        {
            bool rd = g.Kind == "RD";
            if (hdcp != null) hdcp.LastUs = us;
            foreach (var b in HdcpBlocks(g))
            {
                switch (b.Name)
                {
                    case "Write_Message":
                        if (b.Bytes[0] == 2 || hdcp == null || hdcp.Ver != "2.x") StartHdcp(g, us, "2.x");
                        hdcp.Step(HdcpMeaning(b.Name, b.Bytes), false);
                        if (b.Bytes[0] == 11 && hdcp.Result < 0) hdcp.Result = 1;      // SKE_Send_Eks
                        break;
                    case "Read_Message":
                        if (hdcp != null) hdcp.Step(HdcpMeaning(b.Name, b.Bytes), true);
                        break;
                    case "RxStatus":
                        if (b.Bytes.Count >= 2 && (b.Bytes[1] & 8) != 0 && hdcp != null)
                        {
                            hdcp.Step("REAUTH_REQ", true);
                            if (hdcp.Result < 0) hdcp.Result = 0;
                            hdcp.End = "REAUTH_REQ";
                        }
                        break;
                    case "HDCP2Version":
                        Hdcp2Cap = (b.Bytes[0] & 4) != 0 ? 1 : 0;
                        break;
                    case "Bcaps": case "Bksv": case "An":
                        if (hdcp != null && hdcp.Ver == "1.4" && hdcp.Result < 0) hdcp.Step(b.Name, rd);
                        else if (!hdcpPre.Contains(b.Name)) hdcpPre.Add(b.Name);
                        break;
                    case "Aksv":
                        {
                            var pre = new List<string>(hdcpPre);
                            hdcpPre.Clear();
                            StartHdcp(g, us, "1.4");
                            foreach (string p in pre) hdcp.Step(p, p != "An");
                            hdcp.Step("Aksv", false);
                            break;
                        }
                    case "Ri'": case "V'": case "KSV FIFO": case "Bstatus":
                        if (hdcp != null && hdcp.Ver == "1.4")
                        {
                            bool r0 = b.Name == "Ri'" && hdcp.Result < 0;
                            hdcp.Step(r0 ? "R0'" : b.Name, true);
                            if (r0) hdcp.Result = 1;
                        }
                        break;
                }
            }
        }

        // bit fields of a register for the bit view (null: plain byte)
        public static List<BitField> BitFields(int dev, int off)
        {
            var f = new List<BitField>();
            if (dev == DevScdc)
                switch (off)
                {
                    case 0x10: return Bits("Status_Update", "CED_Update", "RR_Test", "Src_Test_Upd", "FRL_start", "FLT_update", "RSED_Update", "");
                    case 0x11: return Bits("Src_Test_Upd", "", "", "", "", "", "", "");
                    case 0x20: return Bits("Scrambling", "Clock_1/40", "", "", "", "", "", "");
                    case 0x21: return Bits("Scrambled", "", "", "", "", "", "", "");
                    case 0x30: return Bits("RR_Enable", "FLT_no_retrain", "", "", "", "", "", "");
                    case 0x31: f.Add(new BitField(7, 4, "FFE_Levels")); f.Add(new BitField(3, 0, "FRL_Rate")); return f;
                    case 0x40: return Bits("Clock_Det", "Ln0_Lock", "Ln1_Lock", "Ln2_Lock", "Ln3_Lock", "DSC_Fail", "FLT_ready", "");
                    case 0x41: f.Add(new BitField(7, 4, "Ln1 LTP")); f.Add(new BitField(3, 0, "Ln0 LTP")); return f;
                    case 0x42: f.Add(new BitField(7, 4, "Ln3 LTP")); f.Add(new BitField(3, 0, "Ln2 LTP")); return f;
                    case 0x51: case 0x53: case 0x55: case 0x58: case 0x5A:
                        f.Add(new BitField(7, 7, "Valid")); f.Add(new BitField(6, 0, "Count[14:8]")); return f;
                    case 0x50: case 0x52: case 0x54: case 0x57: case 0x59:
                        f.Add(new BitField(7, 0, "Count[7:0]")); return f;
                }
            if (dev == DevHdcp)
                switch (off)
                {
                    case 0x40: return Bits("FAST_REAUTH", "1.1_FEATURES", "", "", "FAST", "READY", "REPEATER", "HDMI");
                    case 0x50: return Bits("", "", "HDCP2.2", "", "", "", "", "");
                    case 0x70: f.Add(new BitField(7, 0, "Message_Size[7:0]")); return f;
                    case 0x71: f.Add(new BitField(7, 4, "")); f.Add(new BitField(3, 3, "REAUTH_REQ"));
                               f.Add(new BitField(2, 2, "READY")); f.Add(new BitField(1, 0, "Size[9:8]")); return f;
                }
            return null;
        }

        // single-bit fields, names bit 0 first ("" = unused), returned bit 7 first
        static List<BitField> Bits(params string[] names)
        {
            var f = new List<BitField>();
            for (int b = 7; b >= 0; b--) f.Add(new BitField(b, b, names[b]));
            return f;
        }

        // value of a field as text (LTP request nibbles and the rate by name)
        public static string FieldText(int dev, int off, BitField b, int v)
        {
            int x = b.Of(v);
            if (dev == DevScdc && (off == 0x41 || off == 0x42)) return x.ToString("X") + " " + LtpName(x);
            if (dev == DevScdc && off == 0x31 && b.Lo == 0) return x + " " + Rate(x);
            if (b.Hi == b.Lo) return x.ToString();
            return b.Hi - b.Lo >= 4 ? "0x" + x.ToString("X2") : x.ToString();
        }

        // "54:40" for register transactions, else the device
        public static string AddrText(Group g)
        {
            if (g.Addr >= 0) return string.Format("{0:X2}:{1:X2}", g.Addr >> 8, g.Addr & 0xFF);
            return g.Dev >= 0 ? g.Dev.ToString("X2") : "";
        }

        void OnEvent(long us, int code, string line)
        {
            if (code == 0x09) { St.Beats++; StatPc = FeedPc; return; }       // heartbeat: no row
            // an edge whose time is not confirmed (a broken record in a burst of UART errors) would land anywhere
            // in the waveforms and break the CEC decoding: left out
            if (timeSuspect && (code == 0x0A || code == 0x0B || code == 0x0C || code == 0x0D)) { SuspectEdges++; return; }
            if (code == 0x0A || code == 0x0B)                // CEC line edge: only the waveform of the WAVE tab, no row
            {
                AddEdge(CecEdges, us, code == 0x0B ? 1 : 0);
                EdgeVer++;
                // the frames go into the log as rows (one per CEC message), from the same bit decoder as the WAVE tab
                if (code == 0x0A) cecFall = us;
                else if (cecFall >= 0)
                {
                    if (cecStream == null)
                        cecStream = new CecStream { OnFrame = delegate (CecFrame fr)
                        {
                            // records lost (E FF) while this frame went on: its edges are not complete. The mark comes
                            // before the events that waited in the queue, so it is known here.
                            foreach (long m in CecLostMarks)
                                if (fr.A < m && (fr.B >= m || fr.B + 5000 >= m && (fr.Error || fr.Partial || !fr.Eom)))
                                { fr.Lost = true; fr.Level = 2; fr.Text = CecDecoder.FrameText(fr); break; }
                            // CecFrame.Level: 3 error (red, marked), 2 warning, 1 NACK that is no problem (grey)
                            Info(fr.A, "CEC " + fr.Text, fr.Level >= 3 ? CErr : fr.Level == 2 ? CWarn : fr.Level == 1 ? CGray : CEvt, fr.Level >= 3);
                            var cg = Groups[Groups.Count - 1];                    // the row just made: its own kind (Show: CEC)
                            CatCount[cg.Cat]--;
                            cg.Cat = CatCec;
                            CatCount[CatCec]++;
                        } };
                    cecStream.Feed(cecFall, us);
                    cecFall = -1;
                }
                return;
            }
            if (code == 0x0C || code == 0x0D)                // this board pulls the CEC line low / lets it go: waveform only, no row
            {
                AddEdge(DrvEdges, us, code == 0x0C ? 1 : 0);
                DrvSeen = true;
                EdgeVer++;
                return;
            }
            if ((code == 0x01 || code == 0x02) && !timeSuspect) { AddEdge(HpdEdges, us, code == 0x02 ? 1 : 0); EdgeVer++; }
            else if (code == 0x01 || code == 0x02) SuspectEdges++;
            if (code == 0)                                   // FPGA reset: time restarts
            {
                wrap = 0;
                pendT = -1;
                tlast = long.Parse(line.Substring(0, 8), NumberStyles.HexNumber);
                CecEdges.Clear(); HpdEdges.Clear(); DrvEdges.Clear(); CecLostMarks.Clear(); LastUs = 0; EdgeVer++; cecStream = null; cecFall = -1;
                if (cur != null) { cur.End = "log restarted"; cur.EndUs = us; cur = null; }
                for (int i = 0; i < ptr.Length; i++) ptr[i] = 0;
            }
            var g = NewGroup(us);
            g.EventCode = code;
            g.IsEvent = true;
            g.Kind = "EVENT";
            // a new session: log start, or sink HPD high after transfers (a
            // session of events only, as log start + HPD high, goes on)
            var cs = Sessions[Sessions.Count - 1];
            if (cs.GroupId != g.Id && (code == 0x00 || code == 0x02 && cs.Xfers > 0))
            {
                NewSession(g);
                hdcpPre.Clear();
            }
            if (code == 0x00) Hpd = Power = Link = -1;
            if (code == 0x01 || code == 0x02) Hpd = code == 0x02 ? 1 : 0;
            if (code == 0x03 || code == 0x04) Power = code == 0x04 ? 1 : 0;
            if (code == 0x05 || code == 0x06) Link = code == 0x06 ? 1 : 0;
            if (code == 0x05) ClearSink();
            if (code == 0x07 || code == 0x08) BusStuck = code == 0x07;
            if (code == 0x00) { BusStuck = false; StatSeq = -1; linkErr0 = -1; tiaErr0 = -1; }
            string[] e;
            string kind = "err";
            if (EventText.TryGetValue(code, out e)) kind = e[1];
            string text = EventName(code);
            if (code == 0xFF && line.Split(Ws, StringSplitOptions.RemoveEmptyEntries).Length >= 4)
            {
                int lost;                                    // E FF llll: records lost so far
                if (int.TryParse(line.Split(Ws, StringSplitOptions.RemoveEmptyEntries)[3], NumberStyles.HexNumber, null, out lost))
                {
                    St.FifoLost = lost;
                    if (CecEdges.Count > 0 || DrvSeen) { CecLostMarks.Add(us); EdgeVer++; }   // a hole in the CEC record
                    text += string.Format(" (records lost so far: {0})", lost);
                }
            }
            g.EventText = text;
            Color c = kind == "err" ? CErr : kind == "evt" ? CEvt : CGray;
            if (kind == "err") g.Error = true;
            Add(g, new Row { Kind = RowKind.Event, Time = FmtTime(us), Us = us, Type = "EVENT", Text = text, Color = c,
                             Back = kind == "err" ? Color.FromArgb(252, 232, 232) : Color.Empty },
                FmtTime(us) + " " + text);
            // sink unplugged / link lost: a running training ends
            if ((code == 0x01 || code == 0x05) && cur != null) EndAttempt(g, us, 0, code == 0x01 ? "sink HPD low" : "link down");
            if (code == 0x00 || code == 0x01 || code == 0x05)
            {
                string why = code == 0x00 ? "log restarted" : code == 0x01 ? "sink HPD low" : "link down";
                if (hdcp != null)
                {
                    if (hdcp.Result < 0) hdcp.Result = 0;
                    if (hdcp.End.Length == 0) hdcp.End = why;     // REAUTH_REQ stays the reason
                    hdcp = null;
                }
                if (code != 0x00 && Sessions[Sessions.Count - 1].End.Length == 0) Sessions[Sessions.Count - 1].End = why;
            }
            int n;
            St.Events.TryGetValue(code, out n);
            St.Events[code] = n + 1;
        }

        static readonly char[] Ws = new[] { ' ', '\t' };

        public void Feed(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            Version++;
            if (line[0] == '#') { OnNote(line); return; }
            St.Lines++;
            try { FeedLine(line); }
            catch (Exception ex)
            {
                // a line the decoder cannot handle: shown, the rest goes on
                St.DecodeErr++;
                var g = NewGroup(-1);
                Add(g, new Row { Kind = RowKind.Info, Type = "??", Text = "?? decode error (" + ex.Message + "): " + line, Color = CErr },
                    "?? decode error (" + ex.Message + "): " + line);
                g.Error = true;
            }
        }

        // PC notes in the saved log: "# pc <time>" (PC time of the FPGA time
        // before it), "# connect / reconnect / partial / uart error ..."
        void OnNote(string line)
        {
            string t = line.Substring(1).Trim();
            if (t.StartsWith("pc "))
            {
                DateTime d;
                if (DateTime.TryParseExact(t.Substring(3).Trim(), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                {
                    pcRef = d;
                    pcRefUs = wrap + Math.Max(0, tlast);
                }
                return;
            }
            St.PcNotes++;
            bool err = t.StartsWith("uart error");
            if (err) St.UartErr++;
            var g = NewGroup(-1);
            g.IsEvent = true;
            g.Kind = "EVENT";
            g.EventText = "PC: " + t;
            g.Error = err;
            Add(g, new Row { Kind = RowKind.Info, Type = "PC", Text = "PC: " + t, Color = err ? CErr : CGray }, "# " + t);
        }

        // FPGA status line: board, sequence, build (4), levels, lost (2),
        // counter 1 (2), counter 2 as hex bytes
        void OnStatus(long us, string[] f)
        {
            var b = new List<int>();
            for (int i = 2; i < f.Length; i++)
            {
                int v;
                if (!int.TryParse(f[i], NumberStyles.HexNumber, null, out v) || f[i].Length != 2) throw new FormatException("status byte " + f[i]);
                b.Add(v);
            }
            if (b.Count < 12) throw new FormatException("status line too short");
            St.StatusLines++;
            StatUs = us;
            StatPc = FeedPc;
            string build = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", b[2], b[3], b[4], b[5]);
            bool first = Board != b[0] || Build != build;
            Board = b[0];
            Build = build;
            if (first)
                Info(us, string.Format("FPGA: {0}, build {1}", BoardName(Board), Build), CGray, false);
            if (StatSeq >= 0 && b[1] != ((StatSeq + 1) & 0xFF))
            {
                St.StatusGaps++;
                Info(us, string.Format("!! FPGA status lines missing ({0})", (b[1] - StatSeq - 1) & 0xFF), CErr, true);
            }
            StatSeq = b[1];
            int bits = b[6];
            // levels 7..4: HM RX (22) TRC_SD LOS, TIA ok, TIA error, link inverted;
            // other boards (THCS 23: TRC_SD, TIA_SD as read) only shown
            if (Board == 0x22 && StatBits >= 0 && ((bits ^ StatBits) & 0x10) != 0)
                Info(us, (bits & 0x10) != 0 ? "!! optical signal lost (TRC_SD)" : "optical signal back (TRC_SD)", (bits & 0x10) != 0 ? CErr : CEvt, (bits & 0x10) != 0);
            if (Board == 0x22 && StatBits >= 0 && ((bits ^ StatBits) & 0x40) != 0 && (bits & 0x40) != 0)
                Info(us, "!! TIA configuration error", CErr, true);
            StatBits = bits;
            // levels 0..2 (HPD, 5 V, link) are in every status line: they fill the cards of what was
            // before the connect (events only tell the changes); an event keeps the last word
            int hp = bits & 1, pw = (bits >> 1) & 1, lk = (bits >> 2) & 1;
            if (Hpd < 0 || Power < 0 || Link < 0)
            {
                if (Hpd < 0) Hpd = hp;
                if (Power < 0) Power = pw;
                if (Link < 0) Link = lk;
                Version++;
            }
            St.FifoLost = Math.Max(St.FifoLost, (b[7] << 8) | b[8]);
            int c1 = (b[9] << 8) | b[10];
            if (Board == 0x23)                                  // counter 2: uart rate (2) wave state (2) | commands (4)
            {
                UartSel = (b[11] >> 6) & 3;
                WaveState = (b[11] >> 4) & 3;
            }
            if ((CtrlBoardTx(Board) || CtrlBoardRx(Board)) && (c1 & 0x8000) != 0)
            {
                OnCtrl(us, CtrlBoardTx(Board) ? 1 : 2, c1, b[11]);      // counters carry the control echo, not errors
                return;
            }
            int le = (b[9] << 8) | b[10], te = b[11];
            if (linkErr0 < 0) linkErr0 = le;
            if (LinkErr >= 0 && le > LinkErr)
            {
                LinkErrUs = us;
                Info(us, string.Format("!! optical link frame errors +{0} (total {1})", le - LinkErr, le), CErr, true);
            }
            if (tiaErr0 < 0) tiaErr0 = te;
            if (TiaErr >= 0 && te > TiaErr)
            {
                TiaErrUs = us;
                Info(us, string.Format("!! TIA I2C sequence failed +{0} (total {1})", te - TiaErr, te), CErr, true);
            }
            TiaErrNew = te - tiaErr0;
            LinkErr = le;
            LinkErrNew = le - linkErr0;
            TiaErr = te;
            // error rate over the last LinkRateS seconds (status lines are 1 s apart)
            linkHist.Add(new KeyValuePair<long, int>(us, LinkErrNew));
            while (linkHist.Count > 2 && us - linkHist[1].Key >= LinkRateS * 1000000L) linkHist.RemoveAt(0);
            double dt = (us - linkHist[0].Key) / 1e6;
            LinkErrRate = dt >= 1 ? (LinkErrNew - linkHist[0].Value) / dt : 0;
        }

        void OnCtrl(long us, int kind, int c1, int c2)
        {
            int[] v = new int[3];
            if (kind == 1) { v[0] = (c1 >> 9) & 7; v[1] = (c1 >> 5) & 15; v[2] = (c1 >> 2) & 7; }
            else { v[0] = (c1 >> 10) & 3; v[1] = (c1 >> 5) & 31; }
            int err = (c1 >> 13) & 1;
            // override = values differ from the build defaults (not the ovr bit: the logger reads the
            // counter nibble by nibble, an apply during a record can tear it; the values decide)
            int[] dv = CtrlDefault(kind);
            int ovr = (v[0] != dv[0] || v[1] != dv[1] || v[2] != dv[2]) ? 1 : 0;
            bool same = CtrlKind == kind && v[0] == CtrlVal[0] && v[1] == CtrlVal[1] && v[2] == CtrlVal[2];
            if (CtrlKind == kind && !same)
                Info(us, "CTRL: " + CtrlText(kind, CtrlVal) + "  ->  " + CtrlText(kind, v), CEvt, false);
            else if (CtrlKind != kind)
                Info(us, "CTRL: " + CtrlText(kind, v), CGray, false);
            if (CtrlKind == kind && err == 1 && CtrlErr == 0)
                Info(us, "!! CTRL: the FPGA rejected a command (bad register / value)", CErr, true);
            CtrlKind = kind;
            CtrlOvr = ovr;
            CtrlErr = err;
            CtrlCnt = c2 & 15;
            if (kind == 2)                                   // THCS RX: the HPD to the source (sent over the link) can be forced as well
            {
                int ho2 = (c2 >> 4) & 3;
                if (ho2 != HpdOvr) Info(us, "HPD to the source override: " + (ho2 == 1 ? "forced low" : ho2 == 2 ? "forced high" : "auto (follows the sink)"), CEvt, false);
                HpdOvr = ho2;
            }
            if (kind == 1)
            {
                PsAbsent = ((c1 >> 12) & 1) == 1;
                int ho = (c2 >> 4) & 3;
                if (ho != HpdOvr) Info(us, "HPD output override: " + (ho == 1 ? "forced low" : ho == 2 ? "forced high" : "auto (follows the sink)"), CEvt, false);
                HpdOvr = ho;
            }
            CtrlVal[0] = v[0]; CtrlVal[1] = v[1]; CtrlVal[2] = v[2];
            CtrlUs = us;
            CtrlPc = FeedPc;
            CtrlVer++;
        }

        // build defaults of the control values (the V3 constants)
        public static int[] CtrlDefault(int kind)
        {
            return kind == 1 ? new[] { 1, 12, 0 } : new[] { 3, 6, 0 };
        }

        public static readonly string[] SwingText = { "385", "520", "650", "770" };

        // SL82721 Imean code -> mA (bond pad table: 011 3.86 ... 000 5.14 ... 100 6.86, 0.43 mA steps)
        static readonly double[] ImeanTab = { 5.14, 4.71, 4.29, 3.86, 6.86, 6.43, 6.00, 5.57 };   // code 0..7
        public static double ImeanMa(int c) { return ImeanTab[c & 7]; }

        // SL82721 Imod: only the four bond pad values are documented (code -> mA); others ""
        public static string ImodMa(int c)
        {
            switch (c)
            {
                case 4: return "5.7";
                case 0: return "6.7";
                case 12: return "7.7";
                case 8: return "8.6";
                default: return "";
            }
        }

        public static double TiaBiasMa(int c) { return 15.0 + 0.3 * c; }

        public static string CtrlText(int kind, int[] v)
        {
            if (kind == 1)
            {
                string m = ImodMa(v[1]);
                return "EQ " + v[0] + " dB · Imod " + v[1] + (m.Length > 0 ? " (" + m + " mA)" : "") +
                       " · Imean " + v[2] + " (" + ImeanMa(v[2]).ToString("F2") + " mA)";
            }
            return string.Format("Vswing {0} mVpp · TIAbias {1} ({2:F1} mA)", SwingText[v[0] & 3], v[1], TiaBiasMa(v[1]));
        }

        // PS8419 read-only monitor of the THCS TX (ps8419_mon, G_REC_OUT): index
        // 0 P4.D6h status, 1..7 P12 SCDC snoop copies (10 20 30 31 40 41 42),
        // 8..11 P6.D4h..D7h lane 3..0 EQ status; bit 7 = not read (NACK);
        // 0x40 = EN (PS8419 out of power down)
        static readonly int[] PsScdc = { 0, 0x10, 0x20, 0x30, 0x31, 0x40, 0x41, 0x42 };

        // PS8419 input equalizer (Register Table Ver0.1, page 6). Index 12..16 of the monitor =
        // P6.3Dh [7:4] FRL12G [3:0] FRL10G, P6.3Eh [7:4] FRL8G [3:0] FRL6G / TMDS 3.4-6G,
        // P6.3Fh [7:4] FRL3G / TMDS 25M-3.4G, P6.40h [7:3] FRL12G..3G EQ training enable,
        // P6.41h [1:0] EQ source (0 pin, 1 register, 1X training result) [5] 1 = auto EQ off.
        // Written from the RETIMER tab (the chip returns to its defaults after a power cycle).
        public static readonly string[] PsEqName = { "FRL 12G", "FRL 10G", "FRL 8G", "FRL 6G", "FRL 3G" };
        public static readonly int[] PsEqDefault = { 0x23, 0x46, 0x66, 0xF3, 0x0D };   // chip defaults of P6.3Dh..41h
        static readonly int[] PsDbHigh = { 5, 7, 10, 13, 15, 16, 17, 18, 19 };         // FRL12G / 10G / 8G, code 8 = 1XXX
        static readonly int[] PsDbMid = { 3, 5, 7, 9, 11, 12, 13, 15, 16 };            // FRL6G / TMDS 3.4-6G
        static readonly int[] PsDbLow = { 1, 3, 4, 6, 8, 9, 11, 12, 14 };              // FRL3G / TMDS 25M-3.4G

        // EQ setting n (0 12G, 1 10G, 2 8G, 3 6G, 4 3G): register 0..2 of PsEq and the high nibble or not
        public static int PsEqReg(int n) { return n / 2; }
        public static bool PsEqHigh(int n) { return n % 2 == 0; }
        public static int PsEqCode(int n, int regVal)
        {
            int c = PsEqHigh(n) ? (regVal >> 4) & 0xF : regVal & 0xF;
            return c > 8 ? 8 : c;                              // 1XXX = the top step
        }
        public static int PsEqDbOf(int n, int code)
        {
            int[] t = n == 3 ? PsDbMid : n == 4 ? PsDbLow : PsDbHigh;
            return t[Math.Max(0, Math.Min(8, code))];
        }

        // code and gain together: 0x2/10dB
        public static string PsEqText(int n, int code) { return "0x" + code.ToString("X") + "/" + PsEqDbOf(n, code) + "dB"; }

        public static string PsEqMean(int r, int val)
        {
            if (r <= 2)
            {
                var sb = new System.Text.StringBuilder();
                for (int n = r * 2; n <= r * 2 + 1 && n <= 4; n++)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(PsEqName[n]).Append(' ').Append(PsEqText(n, PsEqCode(n, val)));
                }
                return sb.ToString();
            }
            if (r == 3)
            {
                string[] g = { "12G", "10G", "8G", "6G", "3G" };
                var sb = new System.Text.StringBuilder("EQ training ");
                for (int k = 0; k < 5; k++) sb.Append(g[k]).Append(((val >> (7 - k)) & 1) != 0 ? "+" : "-").Append(k < 4 ? " " : "");
                return sb.ToString();
            }
            string[] src = { "pin control", "register control", "EQ training result", "EQ training result" };
            return "EQ source " + src[val & 3] + ", auto EQ " + (((val >> 5) & 1) != 0 ? "off" : "on");
        }

        // P4.D6h: bit 5 FRL_MODE, bit 4 HDMI_2.0 (TMDS clock ratio 1/40), low nibble = FRL rate.
        // FRL mode is HDMI 2.1 (the HDMI_2.0 bit is 0 then); TMDS with the 2.0 bit is HDMI 2.0, else 1.4
        public static string PsMode(int val)
        {
            bool frl = ((val >> 5) & 1) != 0, h20 = ((val >> 4) & 1) != 0;
            string rate = PsRate(val & 0xF);
            if (frl) return "HDMI 2.1 (FRL, " + rate + ")" + (h20 ? " [HDMI_2.0 bit also set]" : "");
            if (h20) return "HDMI 2.0 (TMDS)";
            return "HDMI 1.4 (TMDS)";
        }

        public static string PsRate(int code)
        {
            switch (code)
            {
                case 0: return "0G";
                case 2: return "3G";
                case 3: return "6G";
                case 4: return "8G";
                case 5: return "10G";
                case 6: return "12G";
                default: return "code " + code;
            }
        }

        // TIA monitor of the HM RX (tia_kx, index 0x50..0x58, a sweep about once
        // a second and at once when SignalDetected or the readback changes):
        // 0 = 000 RB SD[3:0], 1/2 .. 7/8 = PD ADC code of lane 1..4 (high 2
        // bits, low 8 bits). One line per sweep, not repeated while no code
        // moves by more than TiaQuiet; a lane that is low, high or without
        // signal makes it an error line (F8).
        // PD current from the code: approximate, from the SL82711 codes used so
        // far (74 ~ 150 uA, 310 ~ 600 uA: about 1.94 uA per code).
        public const double TiaUaPerCode = 1.94;
        public const int TiaLowCode = 45;                  // ~ 92 uA: 1 m weakest lane (191 uA) - 3 dB (200 m)
        public const int TiaHighCode = 310;                // ~ 600 uA: TIA input overload
        const int TiaQuiet = 2;                            // codes
        readonly int[] tia = new int[9];
        public readonly int[] TiaPd = new int[4];          // last sweep: PD codes, lane 1..4
        public int TiaSd = -1, TiaRb = -1;                 // -1 = no sweep yet
        public long TiaUs = -1;                            // FPGA time of the last sweep
        public DateTime TiaPc = DateTime.MinValue;         // PC time of the last sweep (live)
        readonly int[] tiaShown = { -100, -100, -100, -100 };
        int tiaShownSd = -1, tiaShownRb = -1;

        public static double PdUa(int code) { return code * TiaUaPerCode; }

        // 0 ok, 1 high (overload), 2 low or no signal
        public int TiaLaneState(int lane)
        {
            if (TiaSd < 0) return 0;
            if (((TiaSd >> lane) & 1) == 0 || TiaPd[lane] < TiaLowCode) return 2;
            return TiaPd[lane] > TiaHighCode ? 1 : 0;
        }

        void OnTia(long us, int k, int val)
        {
            tia[k] = val;
            if (k != 8) return;                                   // end of the sweep
            for (int l = 0; l < 4; l++) TiaPd[l] = (tia[1 + 2 * l] & 3) << 8 | tia[2 + 2 * l];
            TiaSd = tia[0] & 15;
            TiaRb = (tia[0] >> 4) & 1;
            TiaUs = us;
            TiaPc = FeedPc;
            bool moved = TiaSd != tiaShownSd || TiaRb != tiaShownRb;
            for (int l = 0; l < 4; l++) if (Math.Abs(TiaPd[l] - tiaShown[l]) > TiaQuiet) moved = true;
            if (!moved) return;
            for (int l = 0; l < 4; l++) tiaShown[l] = TiaPd[l];
            tiaShownSd = TiaSd;
            tiaShownRb = TiaRb;
            var bad = new List<string>();
            for (int l = 0; l < 4; l++)
            {
                int st = TiaLaneState(l);
                if (((TiaSd >> l) & 1) == 0) bad.Add(string.Format("lane {0} no signal", l + 1));
                else if (st == 2) bad.Add(string.Format("lane {0} low", l + 1));
                else if (st == 1) bad.Add(string.Format("lane {0} high", l + 1));
            }
            if (TiaRb == 0) bad.Add("TIA readback differs");
            bool err = bad.Count > 0;
            string text = string.Format("TIA PD {0:F0} {1:F0} {2:F0} {3:F0} uA (code {4:X3} {5:X3} {6:X3} {7:X3}) SD={8:X} RB={9}{10}",
                                        PdUa(TiaPd[0]), PdUa(TiaPd[1]), PdUa(TiaPd[2]), PdUa(TiaPd[3]),
                                        TiaPd[0], TiaPd[1], TiaPd[2], TiaPd[3], TiaSd, TiaRb,
                                        err ? "  !! " + string.Join(", ", bad) : "");
            var g = NewGroup(us);
            g.IsEvent = true;
            g.Kind = "EVENT";
            g.EventText = text;
            g.Error = err;
            Add(g, new Row { Kind = RowKind.Event, Time = FmtTime(us), Us = us, Type = "TIA", Text = text,
                             Color = err ? CErr : CEdid }, FmtTime(us) + " " + text);
        }

        void OnAux(long us, string[] f)
        {
            int idx, val;
            if (f.Length < 4 || !int.TryParse(f[2], NumberStyles.HexNumber, null, out idx) ||
                !int.TryParse(f[3], NumberStyles.HexNumber, null, out val))
                throw new FormatException("value record");
            if (idx >= 0x50 && idx <= 0x58) { OnTia(us, idx - 0x50, val); return; }
            if (idx >= 0x20 && idx <= 0x2B && Board == 0x21)
            {
                // VCSEL loop values of an older HM TX build (vcsel_kx, removed)
                string vt = string.Format("VCSEL (old TX build) {0:X2} = {1:X2}", idx, val);
                var vg = NewGroup(us);
                vg.IsEvent = true;
                vg.Kind = "EVENT";
                vg.EventText = vt;
                Add(vg, new Row { Kind = RowKind.Event, Time = FmtTime(us), Us = us, Type = "VCSEL", Text = vt,
                                  Color = CGray }, FmtTime(us) + " " + vt);
                return;
            }
            string name, mean = "";
            bool err = false;
            int i = idx & 0x7F;
            if (i == 0x41)
            {
                // a register read request of the PS8419 Regs tab: its own result, not a log line
                PsRdVal = val; PsRdNack = (idx & 0x80) != 0; PsRdVer++;
                return;
            }
            if (idx == 0x40)
            {
                name = "EN"; mean = val != 0 ? "PS8419 on (out of power down)" : "PS8419 off";
                if (val != 0) Ps8419En = 1;                    // EN is treated as always on: an EN = 0 record only stays in the log
            }
            else if (i == 0)
            {
                name = "P4:D6 status"; mean = PsMode(val);
                PsLink = -1; PsLinkName = "";
                if ((idx & 0x80) == 0)
                {
                    bool frl = ((val >> 5) & 1) != 0, h20 = ((val >> 4) & 1) != 0;
                    int rc = val & 0xF;
                    if (frl) { int[] row = { -1, -1, 4, 3, 2, 1, 0 }; if (rc < row.Length) PsLink = row[rc]; if (PsLink >= 0) PsLinkName = "FRL " + PsRate(rc); }
                    else if (h20) { PsLink = 3; PsLinkName = "TMDS 6G"; }
                    else { PsLink = 4; PsLinkName = "TMDS 3G"; }
                }
            }
            else if (i >= 1 && i <= 7)
            {
                string sn;
                ScdcNames.TryGetValue(PsScdc[i], out sn);
                name = string.Format("P12 SCDC {0:X2} {1}", PsScdc[i], sn ?? "");
                mean = DecodeScdc(PsScdc[i], val);
            }
            else if (i >= 8 && i <= 11) { name = string.Format("P6:{0:X2} lane {1} EQ", 0xD4 + i - 8, 11 - i); mean = "status " + (val >> 4); }
            else if (i >= 12 && i <= 16 && idx != 0x40)
            {
                name = string.Format("P6:{0:X2} EQ", 0x3D + i - 12);
                if ((idx & 0x80) == 0) mean = PsEqMean(i - 12, val);
                PsEq[i - 12] = (idx & 0x80) != 0 ? -1 : val;
                PsEqVer++;
            }
            else name = "index " + idx.ToString("X2");
            if ((idx & 0x80) != 0 && idx != 0x40) { mean = "not read (NACK)"; err = true; }
            if (i == 0 && (idx & 0x80) == 0) Ps8419 = mean;
            string text = string.Format("PS8419 {0} = {1:X2}{2}", name, val, mean.Length > 0 ? "  " + mean : "");
            var g = NewGroup(us);
            g.IsEvent = true;
            g.Kind = "EVENT";
            g.EventText = text;
            g.Error = err;
            Add(g, new Row { Kind = RowKind.Event, Time = FmtTime(us), Us = us, Type = "PS8419", Text = text,
                             Color = err ? CErr : CEdid }, FmtTime(us) + " " + text);
        }

        public static string BoardName(int b)
        {
            switch (b)
            {
                case 0x22: return "HM RX";
                case 0x23: return "HM THCS RX V0.12";
                case 0x24: return "HM THCS TX V0.12";
                case 0x25: return "HM THCS TX V0.13";   // TX_IT66313 (IT66313, no PS8419)
                case 0x26: return "HM THCS RX V0.13";   // RX_PS8419 (PS8419 added)
                case 0x21: return "HM TX";
                case 0x11: return "DP TX";
                case 0x12: return "DP RX";
                default: return b < 0 ? "-" : "board " + b.ToString("X2");
            }
        }

        void Info(long us, string text, Color c, bool err)
        {
            var g = NewGroup(us);
            g.IsEvent = true;
            g.Kind = "EVENT";
            g.EventText = text;
            g.Error = err;
            Add(g, new Row { Kind = RowKind.Event, Time = FmtTime(us), Us = us, Type = "FPGA", Text = text, Color = c,
                             Back = err ? Color.FromArgb(252, 232, 232) : Color.Empty },
                FmtTime(us) + " " + text);
        }

        // a running HDCP authentication / FRL training without progress for a
        // while has stopped: failed (the source gave up or the log lost it)
        void CheckStall(long us)
        {
            if (us < 0) return;
            if (hdcp != null && hdcp.Result < 0 && us - hdcp.LastUs > HdcpStallUs)
            {
                hdcp.Result = 0;
                hdcp.End = string.Format("no progress for {0} s", HdcpStallUs / 1000000);
                St.Stalls++;
                Info(hdcp.LastUs + HdcpStallUs, string.Format("!! HDCP {0} authentication stopped: {1} after {2}", hdcp.Ver, hdcp.End,
                     hdcp.Steps.Count > 0 ? hdcp.Steps[hdcp.Steps.Count - 1] : "-"), CErr, true);
                hdcp = null;
            }
            if (cur != null && us - cur.LastUs > FrlStallUs)
            {
                var g = NewGroup(cur.LastUs + FrlStallUs);
                g.IsEvent = true;
                g.Kind = "EVENT";
                St.Stalls++;
                EndAttempt(g, cur.LastUs + FrlStallUs, 0, string.Format("no progress for {0} s", FrlStallUs / 1000000));
            }
        }

        void FeedLine(string line)
        {
            string[] f = line.Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || !IsTime(f[0]))
            {
                var g = NewGroup(-1);
                Add(g, new Row { Kind = RowKind.Info, Type = "??", Text = line, Color = CGray }, "?? " + line);
                g.Error = true;
                St.Unknown++;
                return;
            }
            long us = ToUs(f[0], f[1] == "S" || f[1] == "E" && f[2] == "09");
            CheckStall(us);
            string sig = string.Join(" ", f, 1, f.Length - 1);
            switch (f[1])
            {
                case "I": OnXfer(us, f, sig, line); break;
                case "S": OnStatus(us, f); break;
                case "X": OnAux(us, f); break;
                case "W": OnWave(us, f); break;
                case "E":
                    {
                        int code;
                        if (int.TryParse(f[2], NumberStyles.HexNumber, null, out code)) { OnEvent(us, code, line); break; }
                        goto default;
                    }
                default:
                    {
                        var g = NewGroup(us);
                        Add(g, new Row { Kind = RowKind.Info, Type = "??", Text = line, Color = CGray }, "?? " + line);
                        g.Error = true;
                        St.Unknown++;
                        break;
                    }
            }
        }

        // logic analyzer lines: W H cause / W N valid trig / W D entries
        void OnWave(long us, string[] f)
        {
            if (f.Length < 3) throw new FormatException("wave line too short");
            int v;
            switch (f[2])
            {
                case "H":
                    if (f.Length < 4 || !int.TryParse(f[3], NumberStyles.HexNumber, null, out v)) throw new FormatException("wave header");
                    if (waveCur != null) waveCur.Open = false;
                    waveCur = new WaveCap { Us = us, Pc = FeedPc, Cause = v, Open = true };
                    Waves.Add(waveCur);
                    break;
                case "N":
                    {
                        int tr;
                        if (waveCur == null || f.Length < 5 || !int.TryParse(f[3], NumberStyles.HexNumber, null, out v) ||
                            !int.TryParse(f[4], NumberStyles.HexNumber, null, out tr)) throw new FormatException("wave header 2");
                        waveCur.Valid = v;
                        waveCur.Trig = tr;
                        break;
                    }
                case "D":
                    if (waveCur == null) break;
                    for (int i = 3; i < f.Length; i++)
                        if (int.TryParse(f[i], NumberStyles.HexNumber, null, out v)) waveCur.Ent.Add(v);
                    if (waveCur.Complete)
                    {
                        waveCur.Build();
                        waveCur.Open = false;
                        double span = waveCur.EndPos * WaveCap.SampleUs / 1000.0;
                        string cec = "";
                        Info(waveCur.Us, string.Format("WAVE #{0}: {1}, {2} entries, {3:F1} ms", Waves.Count, WaveCap.CauseText(waveCur.Cause), waveCur.Valid, span) + (cec.Length > 0 ? "  CEC: " + cec : ""), CEvt, false);
                        waveCur = null;
                    }
                    break;
                default:
                    throw new FormatException("wave line " + f[2]);
            }
            WaveVer++;
            Version++;
        }

        // end of input (file)
        public void Finish()
        {
            Version++;
        }
    }
}
