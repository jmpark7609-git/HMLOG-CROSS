using System;
using System.Collections.Generic;

namespace HMLOG.App
{
    // PS8419 registers: what the register table (PS8419 Register Table Ver0.1, 2020-06-18) says about a cell of the page
    // dump: name, access, initial value and the meaning of every bit field for the value read. Only the registers of that
    // table are known (P4: 0B D6; P6: 3D..41 D4..D7; P12: the SCDC snoop copies). The P6.3D..41 bit positions are taken
    // from the (garbled) table text and are not verified on a chip. Port of MainForm.PsRegInfo.cs.
    class RegField
    {
        public int Hi, Lo;
        public string Name;
        public Func<int, string> Mean;
    }

    class RegDef
    {
        public string Name, Group, Access, Note;
        public int Init;
        public RegField[] Fields;
    }

    static class PsRegTable
    {
        static RegField Fld(int hi, int lo, string name, Func<int, string> mean) { return new RegField { Hi = hi, Lo = lo, Name = name, Mean = mean }; }
        static string OnOff(int v) { return v != 0 ? "enabled" : "disabled"; }
        static string EqDb(int n, int v) { return Decoder.PsEqText(n, v > 8 ? 8 : v); }
        static string Rsv(int v) { return v == 0 ? "" : "0x" + v.ToString("X"); }

        public static readonly Dictionary<int, RegDef> Defs = Build();

        public static bool Has(int page, int off) { return Defs.ContainsKey(page << 8 | off); }

        public static RegDef Get(int page, int off)
        {
            RegDef d;
            Defs.TryGetValue(page << 8 | off, out d);
            return d;
        }

        static Dictionary<int, RegDef> Build()
        {
            var t = new Dictionary<int, RegDef>();
            const string eq = "Input equalization", rt = "Retimer / redriver", st = "Status", sc = "SCDC snooping result";
            const string inferred = "Bit positions inferred from the table text (not verified on a chip).";
            Func<int, string> jc = v => v != 0 ? "redriver" : "jitter cleaning";

            t[4 << 8 | 0x0B] = new RegDef { Name = "Jitter cleaning / redriver per rate", Group = rt, Access = "R/W", Init = 0x01, Fields = new[] {
                Fld(7, 7, "FRL12G", jc), Fld(6, 6, "FRL10G", jc), Fld(5, 5, "FRL8G", jc), Fld(4, 4, "FRL6G", jc), Fld(3, 3, "FRL3G", jc),
                Fld(2, 2, "HDMI2.0", jc), Fld(1, 1, "HDMI1.4 (>1.2Gbps)", jc), Fld(0, 0, "HDMI1.4 (<1.2Gbps)", jc) } };
            t[4 << 8 | 0xD6] = new RegDef { Name = "Link status", Group = st, Access = "R/O", Init = 0x00, Fields = new[] {
                Fld(7, 6, "Reserved", Rsv),
                Fld(5, 5, "FRL_MODE", v => v != 0 ? "FRL mode" : "not FRL"),
                Fld(4, 4, "HDMI_2.0", v => v != 0 ? "HDMI 2.0 mode" : "no"),
                Fld(3, 0, "FRL_DATA_RATE", v => Decoder.PsRate(v)) } };

            t[6 << 8 | 0x3D] = new RegDef { Name = "EQ code FRL 12G / 10G", Group = eq, Access = "R/W", Init = 0x23, Note = inferred, Fields = new[] {
                Fld(7, 4, "Register_EQ_FRL12G", v => EqDb(0, v)),
                Fld(3, 0, "Register_EQ_FRL10G", v => EqDb(1, v)) } };
            t[6 << 8 | 0x3E] = new RegDef { Name = "EQ code FRL 8G / 6G", Group = eq, Access = "R/W", Init = 0x46, Note = inferred, Fields = new[] {
                Fld(7, 4, "Register_EQ_FRL8G", v => EqDb(2, v)),
                Fld(3, 0, "Register_EQ_FRL6G / TMDS 3.4G~6G", v => EqDb(3, v)) } };
            t[6 << 8 | 0x3F] = new RegDef { Name = "EQ code FRL 3G", Group = eq, Access = "R/W", Init = 0x66, Note = inferred, Fields = new[] {
                Fld(7, 4, "Register_EQ_FRL3G / TMDS 25M~3.4G", v => EqDb(4, v)),
                Fld(3, 0, "not described", Rsv) } };
            t[6 << 8 | 0x40] = new RegDef { Name = "EQ training enable", Group = eq, Access = "R/W", Init = 0xF3, Note = inferred, Fields = new[] {
                Fld(7, 7, "FRL12G EQ training", OnOff), Fld(6, 6, "FRL10G EQ training", OnOff), Fld(5, 5, "FRL8G EQ training", OnOff),
                Fld(4, 4, "FRL6G EQ training", OnOff), Fld(3, 3, "FRL3G EQ training", OnOff), Fld(2, 0, "not described", Rsv) } };
            t[6 << 8 | 0x41] = new RegDef { Name = "EQ source and auto EQ", Group = eq, Access = "R/W", Init = 0x0D, Note = inferred, Fields = new[] {
                Fld(7, 6, "not described", Rsv),
                Fld(5, 5, "Auto EQ", v => v != 0 ? "disabled" : "enabled"),
                Fld(4, 2, "not described", Rsv),
                Fld(1, 0, "EQ band select (FRL)", v => v == 0 ? "from pin control" : v == 1 ? "from register control" : "from EQ training result") } };
            for (int k = 0; k < 4; k++)
            {
                int lane = 3 - k;
                t[6 << 8 | (0xD4 + k)] = new RegDef { Name = "Lane " + lane + " EQ status", Group = st, Access = "R", Init = 0x00, Note = "Meaning of the value is not described in the table.", Fields = new[] {
                    Fld(7, 4, "Lane" + lane + " EQ status", v => "0x" + v.ToString("X")), Fld(3, 0, "not described", Rsv) } };
            }

            string[,] scdc = {
                { "01", "Sink version" }, { "02", "Source version" }, { "10", "Update flags" }, { "11", "Update flags 1" },
                { "20", "TMDS configuration" }, { "21", "TMDS scrambler status" }, { "30", "Sink configuration 0" }, { "31", "Sink configuration 1" },
                { "35", "Source test configuration" }, { "40", "Status flags 0" }, { "41", "Status flags 1 (lane 0, 1)" }, { "42", "Status flags 2 (lane 2, 3)" } };
            for (int i = 0; i < scdc.GetLength(0); i++)
            {
                int o = Convert.ToInt32(scdc[i, 0], 16);
                t[12 << 8 | o] = new RegDef { Name = scdc[i, 1], Group = sc, Access = "R", Init = 0x00, Note = "Copy of the HDMI SCDC register, defined in the HDMI 2.1 specification.",
                                              Fields = new[] { Fld(7, 0, "SCDC " + scdc[i, 0] + "h", v => Decoder.DecodeScdc(o, v)) } };
            }
            return t;
        }
    }
}
