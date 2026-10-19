// BOARD tab, RETIMER page: the HDMI HPD output of the TX board, the PS8419 input EQ (read-back only) and the PS8419
// registers (single read, page dump). Port of MainForm.PsEq.cs / MainForm.PsRegs.cs / MainForm.PsRegInfo.cs.
//
// PS8419 writes stay OFF on purpose (the retimer runs on pin straps and chip defaults, the FPGA has only a read-only
// monitor): there is no Write button, no EQ apply and no raw write here. Reads are 3 frames to the board
// (register 3 / 4 / 5, see dbg_cmd.vhd): 3 = page(4) off[7:5]  4 = off[4:0] val[7:6]  5 = val[5:0] op (0 read; starts it).
// A read comes back as the PS8419 record idx 41h (value) or C1h (no ACK) (Decoder.PsRdVal / PsRdNack / PsRdVer).
// The FPGA takes one request at a time: the next one is sent after the result.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HMLOG.App
{
    // the cells of a page, 16 x 16 hex: -1 not read, -2 no ACK
    class RegGrid : Control
    {
        public readonly int[] V = new int[256];
        public readonly bool[] Chg = new bool[256];
        public readonly bool[] Doc = new bool[256];          // the register is in the register table
        public int Sel = -1;
        public string Loading;                               // while a dump runs: the text on the box over the grid
        public double LoadFrac;
        public event Action<int> CellClick;
        const double CellH = 26, CellW = 38, HeadW = 38;

        public RegGrid()
        {
            for (int i = 0; i < 256; i++) V[i] = -1;
            Width = HeadW + CellW * 16 + 2;
            Height = CellH * 17 + 2;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(Pal.Br(Pal.Surface), new Rect(Bounds.Size));
            var line = new Pen(Pal.Br(Pal.Line), 1);
            for (int c = 0; c < 16; c++) Gfx.Text(dc, c.ToString("X"), new Rect(HeadW + c * CellW, 0, CellW, CellH), Pal.Muted, align: 1);
            for (int r = 0; r < 16; r++)
            {
                Gfx.Text(dc, (r * 16).ToString("X2"), new Rect(0, (r + 1) * CellH, HeadW, CellH), Pal.Muted, align: 1);
                for (int c = 0; c < 16; c++)
                {
                    int o = r * 16 + c, v = V[o];
                    var rc = new Rect(HeadW + c * CellW, (r + 1) * CellH, CellW, CellH);
                    string t = v == -1 ? "--" : v == -2 ? "xx" : v.ToString("X2");
                    Color fore = v == -1 ? Pal.Muted : v == -2 ? Pal.Red : Chg[o] ? (Pal.Dark ? Pal.Text : Pal.Warn) : Pal.Text;
                    Color back = v == -2 ? Pal.RedBack : Chg[o] ? Pal.ChgBack : Doc[o] ? Pal.Bg(221, 235, 252) : Pal.Surface;
                    dc.DrawRectangle(Pal.Br(back), line, rc);
                    Gfx.Text(dc, t, rc, fore, align: 1);
                    if (Doc[o]) dc.FillRectangle(Pal.Br(Pal.Accent), new Rect(rc.X + 3, rc.Bottom - 4, rc.Width - 5, 3));   // in the table
                }
            }
            if (Sel >= 0)
                dc.DrawRectangle(null, new Pen(Pal.Br(Pal.Accent), 2), new Rect(HeadW + (Sel % 16) * CellW + 1, (Sel / 16 + 1) * CellH + 1, CellW - 2, CellH - 2));
            if (Loading != null)
            {
                // the dump is running: the cells are filled in a moment, say so
                double bw = Math.Max(Gfx.Width(Loading) + CellH * 2, CellH * 9), bh = CellH * 2 + 8;
                var box = new Rect((Bounds.Width - bw) / 2, (Bounds.Height - bh) / 2, bw, bh);
                dc.DrawRectangle(Pal.Br(Pal.Surface), new Pen(Pal.Br(Pal.Accent), 2), box);
                Gfx.Text(dc, Loading, new Rect(box.X, box.Y + 4, box.Width, CellH), Pal.Text, align: 1);
                var bar = new Rect(box.X + CellH / 2, box.Y + CellH + 10, box.Width - CellH, Math.Max(6, CellH / 3));
                dc.FillRectangle(Pal.Br(Pal.Header), bar);
                dc.FillRectangle(Pal.Br(Pal.Accent), new Rect(bar.X, bar.Y, bar.Width * Math.Max(0, Math.Min(1, LoadFrac)), bar.Height));
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(this);
            int c = (int)Math.Floor((p.X - HeadW) / CellW), r = (int)Math.Floor(p.Y / CellH) - 1;
            if (p.X >= HeadW && c >= 0 && c < 16 && r >= 0 && r < 16 && CellClick != null) CellClick(r * 16 + c);
        }
    }

    class PrReq
    {
        public int Page, Off;
        public bool Dump;
    }

    partial class MainWindow
    {
        static readonly int[] PrPages = { 4, 6, 12 };
        static readonly string[] PrPageName = { "4 · status", "6 · EQ", "12 · SCDC" };

        Border prChipBoard, prChipLive, prChipAddr;
        Button[] prPageBtn;
        int prPageSel = 1;
        TextBox prOff, prFrom, prTo, prHpdMs;
        Button prRead, prDump, prStop, prHpdPulse;
        Button[] prHpdBtn;
        TextBlock prRes, prProg, prHpdState, prPsState;
        RegGrid prGrid;
        StackPanel prInfo;
        Table prLog;
        readonly DispatcherTimer prTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        readonly DispatcherTimer prHpdTimer = new DispatcherTimer();
        bool prTimersHooked;
        readonly int[][] prData = new int[3][];
        readonly int[][] prPrev = new int[3][];
        readonly Queue<PrReq> prQ = new Queue<PrReq>();
        PrReq prInf;
        DateTime prSent;
        int prVerBase;
        bool prDumping;
        int prDumpNext, prDumpEnd, prDumpFrom, prDumpPage;
        int prSelPage = -1;
        TabControl prSubTabs;
        TabItem prTabEq, prTabReg;
        // input EQ card (read-back only)
        readonly List<CtrlRow> psRows = new List<CtrlRow>();
        TextBlock psModeL;
        Border psLinkChip;
        readonly TextBlock[] psTile = new TextBlock[5];
        static readonly string[] PsRegName = { "P6.3Dh", "P6.3Eh", "P6.3Fh", "P6.40h", "P6.41h" };

        static int PsDefCode(int n) { return Decoder.PsEqCode(n, Decoder.PsEqDefault[Decoder.PsEqReg(n)]); }

        // three frames of one read access
        List<byte[]> PsReadFrames(int page, int off)
        {
            return new List<byte[]> { CtrlFrame(3, (page << 3) | (off >> 5)), CtrlFrame(4, ((off & 0x1F) << 2)), CtrlFrame(5, 0) };
        }

        static bool PrHex(string s, out int v)
        {
            v = 0;
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return s.Length > 0 && s.Length <= 2 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        int PrPageIdx { get { return prPageSel; } }

        TextBox Field(string text, double w)
        {
            return new TextBox { Text = text, Width = w, FontFamily = Table.Mono, MaxLength = 4, VerticalAlignment = VerticalAlignment.Center };
        }

        Control BuildRetimerPage()
        {
            for (int i = 0; i < 3; i++)
            {
                prData[i] = new int[256]; prPrev[i] = new int[256];
                for (int k = 0; k < 256; k++) { prData[i][k] = -1; prPrev[i][k] = -1; }
            }
            prQ.Clear(); prInf = null; prDumping = false; prSelPage = -1;
            psRows.Clear();
            if (!prTimersHooked)
            {
                prTimersHooked = true;
                prTimer.Tick += (s, e) => UpdatePsRegs();
                prHpdTimer.Tick += (s, e) => { prHpdTimer.Stop(); PrHpdSet(0); };
            }

            prChipBoard = NewChipBox(); prChipLive = NewChipBox(); prChipAddr = NewChipBox();
            var strip = new WrapPanel();
            strip.Children.Add(prChipBoard); strip.Children.Add(prChipLive); strip.Children.Add(prChipAddr);

            // the HDMI HPD output (B12, to the source): follow the sink or force it low / high
            prHpdBtn = new Button[3];
            var hseg = SegButtons(new[] { "Auto (follows the sink)", "Force low", "Force high" }, k => PrHpdSet(k), out prHpdBtn);
            string[] ht = { "HDMI_HPD_OUT is the sink HPD, as built", "The source sees no sink (HPD low)", "The source sees a sink (HPD high)" };
            for (int i = 0; i < 3; i++) ToolTip.SetTip(prHpdBtn[i], ht[i]);
            prHpdMs = Field("1000", 70);
            prHpdPulse = Btn("Pulse", PrHpdPulse, "HPD low for the time given, then back to Auto (a sink unplug and plug in again)");
            prHpdState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var hrow = new WrapPanel { Margin = new Thickness(12, 4, 12, 8) };
            hseg.Margin = new Thickness(0, 2, 18, 2);
            hrow.Children.Add(hseg);
            hrow.Children.Add(Cap("Pulse low for"));
            hrow.Children.Add(prHpdMs);
            hrow.Children.Add(Cap("ms"));
            hrow.Children.Add(prHpdPulse);
            hrow.Children.Add(prHpdState);
            var hpdCard = WideCard("HDMI HPD output (B12, to the source)", hrow);
            ToolTip.SetTip(hpdCard, "Volatile: after a power cycle the output follows the sink again. The PS8419 HPD input (B11) is not affected.");

            prPsState = new TextBlock { Text = "-", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 3, 0, 3) };
            var psStat = KvCard("PS8419", "State", prPsState);

            prTabEq = new TabItem { Header = "INPUT EQ", Content = BuildPsEqPage() };
            prTabReg = new TabItem { Header = "REGISTERS", Content = BuildPsRegsPage() };
            prSubTabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };
            prSubTabs.Items.Add(prTabEq);
            prSubTabs.Items.Add(prTabReg);

            var sp = new StackPanel { Margin = new Thickness(16) };
            foreach (var c in new Control[] { strip, psStat, hpdCard, prSubTabs }) sp.Children.Add(c);
            PrPageChanged();
            PrEnable(false, false);
            return PageScroll(sp);
        }

        // ------------------------------------------------------------------
        // INPUT EQ page: the registers as the chip reports them (no writes)
        // ------------------------------------------------------------------
        Control BuildPsEqPage()
        {
            psModeL = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            psLinkChip = NewChipBox();
            psLinkChip.Background = Pal.Br(Pal.BandBack);
            ToolTip.SetTip(psLinkChip, "The link rate the PS8419 reports now (P4.D6h): the row of this rate is the EQ in use");
            psLinkChip.Margin = new Thickness(0);
            var head = new WrapPanel { Margin = new Thickness(12, 6, 12, 6) };
            head.Children.Add(Cap("EQ mode (read from the chip)"));
            head.Children.Add(psModeL);
            head.Children.Add(psLinkChip);

            var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            for (int n = 0; n < 5; n++)
            {
                int nn = n, dc = PsDefCode(n);
                var r = NewRow(rows, Decoder.PsEqName[n], "default " + Decoder.PsEqText(n, dc), 0, 8, dc, v => Decoder.PsEqText(nn, v), n, x => { });
                r.Tb.IsEnabled = false;                       // read-back only: the sliders show the chip, they do not set it
                r.Reset.IsEnabled = false;
                psRows.Add(r);
            }
            var tiles = new WrapPanel { Margin = new Thickness(12, 4, 12, 4) };
            for (int r = 0; r < 5; r++)
            {
                psTile[r] = new TextBlock { Text = PsRegName[r] + "\n-", FontFamily = Table.Mono, TextAlignment = TextAlignment.Center };
                tiles.Children.Add(new Border { Child = psTile[r], Background = Pal.Br(Pal.Header), CornerRadius = new CornerRadius(4), Width = 86, Padding = new Thickness(0, 5), Margin = new Thickness(0, 0, 6, 6) });
            }
            var note = new TextBlock
            {
                Text = "Read-only: PS8419 register writes are off on purpose (pin straps and chip defaults). Values come from the FPGA monitor read-back.",
                Foreground = Pal.Br(Pal.Muted), FontSize = 12.5, Margin = new Thickness(12, 2, 12, 8), TextWrapping = TextWrapping.Wrap
            };
            var body = new StackPanel();
            body.Children.Add(head); body.Children.Add(rows); body.Children.Add(tiles); body.Children.Add(note);
            var card = WideCard("PS8419 input EQ", body);
            card.IsVisible = true;
            card.Margin = new Thickness(0, 8, 0, 0);
            return card;
        }

        // the register as read back; before the first read-back the chip default
        int PsCur(int r) { return dec.PsEq[r] >= 0 ? dec.PsEq[r] : Decoder.PsEqDefault[r]; }

        // the mode the chip is in: 0 chip default (control bits and all EQ values at default), 1 manual, 2 control bits at default, -1 other
        int PsModeNow()
        {
            int v40 = PsCur(3), v41 = PsCur(4);
            int d40 = Decoder.PsEqDefault[3], d41 = Decoder.PsEqDefault[4];
            if ((v41 & 3) == 1 && (v41 & 0x20) != 0 && (v40 & 0xF8) == 0) return 1;
            if ((v40 & 0xF8) == (d40 & 0xF8) && (v41 & 0x23) == (d41 & 0x23))
            {
                for (int n = 0; n < 5; n++) if (Decoder.PsEqCode(n, PsCur(Decoder.PsEqReg(n))) != PsDefCode(n)) return 2;
                return 0;
            }
            return -1;
        }

        void UpdatePsEq()
        {
            bool known = true;
            for (int r = 0; r < 5; r++) if (dec.PsEq[r] < 0) known = false;
            int mode = known ? PsModeNow() : -2;
            string[] mn = { "Chip default", "Manual", "Auto (training)" };
            Put(psModeL, mode == -2 ? "no read-back yet" : mode >= 0 ? mn[mode] : "other", mode >= 0 ? Pal.Text : Pal.Muted);
            string lt = dec.PsLink >= 0 && dec.Link == 1 && dec.Ps8419En != 0 ? "in use: " + dec.PsLinkName : "";
            SetChipBox(psLinkChip, lt, Pal.BandText);
            ctrlBuilding = true;
            for (int n = 0; n < 5; n++)
            {
                var row = psRows[n];
                int code = Decoder.PsEqCode(n, PsCur(Decoder.PsEqReg(n)));
                row.Value = code;
                CtrlShowRow(row, -1);
                row.Reset.IsVisible = false;
                row.NameL.Foreground = Pal.Br(n == dec.PsLink && lt.Length > 0 ? Pal.BandText : Pal.Text);
                row.NameL.FontWeight = n == dec.PsLink && lt.Length > 0 ? FontWeight.SemiBold : FontWeight.Normal;
            }
            ctrlBuilding = false;
            for (int r = 0; r < 5; r++)
            {
                string t = PsRegName[r] + "\n" + (dec.PsEq[r] < 0 ? "-" : dec.PsEq[r].ToString("X2"));
                if (psTile[r].Text != t) psTile[r].Text = t;
            }
        }

        // ------------------------------------------------------------------
        // REGISTERS page: read one register, dump a page (no write)
        // ------------------------------------------------------------------
        Control BuildPsRegsPage()
        {
            prPageBtn = null;
            var pseg = SegButtons(PrPageName, k => { if (!prDumping) { prPageSel = k; PrPageChanged(); } }, out prPageBtn);
            prOff = Field("3D", 56);
            prRead = Btn("Read", PrReadClick, "Read one register of the page");
            prRes = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var one = new WrapPanel { Margin = new Thickness(12, 4, 12, 8) };
            one.Children.Add(Cap("Page")); one.Children.Add(pseg);
            one.Children.Add(Cap("Offset (hex)")); one.Children.Add(prOff);
            one.Children.Add(prRead); one.Children.Add(prRes);
            foreach (Control c in one.Children) c.Margin = new Thickness(c.Margin.Left, 3, 6, 3);
            var oneCard = WideCard("Single register · read only", one);

            prFrom = Field("00", 48); prTo = Field("FF", 48);
            prDump = Btn("Dump page", PrDumpClick, "Read the registers from..to of the page one after the other");
            prStop = Btn("Stop", () => PrStop("Stopped"));
            prProg = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(10, 0, 0, 0) };
            var d = new WrapPanel { Margin = new Thickness(12, 4, 12, 4) };
            d.Children.Add(Cap("from")); d.Children.Add(prFrom); d.Children.Add(Cap("to")); d.Children.Add(prTo);
            d.Children.Add(prDump); d.Children.Add(prStop); d.Children.Add(prProg);
            foreach (Control c in d.Children) c.Margin = new Thickness(c.Margin.Left, 3, 6, 3);

            prGrid = new RegGrid();
            prGrid.CellClick += PrCellClick;
            prInfo = new StackPanel { Margin = new Thickness(10, 6) };
            var infoBox = new Border { Child = prInfo, BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(1), MinWidth = 300, Height = prGrid.Height, ClipToBounds = true, Margin = new Thickness(14, 0, 0, 0) };
            var gh = new DockPanel { Margin = new Thickness(12, 4, 12, 4) };
            DockPanel.SetDock(prGrid, Dock.Left);
            gh.Children.Add(prGrid);
            gh.Children.Add(infoBox);
            var legend = new TextBlock
            {
                Text = "-- not read · xx no ACK · shaded with a bar = in the register table · amber = changed since the previous dump · click a cell: its meaning on the right",
                Foreground = Pal.Br(Pal.Muted), FontSize = 12.5, Margin = new Thickness(12, 0, 12, 8), TextWrapping = TextWrapping.Wrap
            };
            var dumpBody = new StackPanel();
            dumpBody.Children.Add(d); dumpBody.Children.Add(gh); dumpBody.Children.Add(legend);
            var dumpCard = WideCard("Page dump", dumpBody);

            prLog = new Table("Time", 120, "Access", 150, "Value", 70, "Result", 380);
            prLog.Height = 170;
            var logCard = WideCard("Access log", prLog);

            var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in new[] { oneCard, dumpCard, logCard }) sp.Children.Add(c);
            return sp;
        }

        void PrEnable(bool on, bool ps)
        {
            ps = on && ps;
            prRead.IsEnabled = prDump.IsEnabled = prOff.IsEnabled = prFrom.IsEnabled = prTo.IsEnabled = ps;
            foreach (var b in prPageBtn) b.IsEnabled = ps && !prDumping;
            prStop.IsEnabled = prDumping;
            foreach (var b in prHpdBtn) b.IsEnabled = on;
            prHpdPulse.IsEnabled = prHpdMs.IsEnabled = on;
            if (hdrHpdBtn != null) { foreach (var b in hdrHpdBtn) b.IsEnabled = on; hdrHpdPulse.IsEnabled = on; }
            PrHpdShow(on);
        }

        // the HPD output override (dbg_cmd register 6: value 4 auto, 5 force low, 6 force high)
        void PrHpdSet(int mode)
        {
            prHpdTimer.Stop();
            if (!CtrlWrite(new List<byte[]> { CtrlFrame(6, 4 + mode) })) return;
            Note("ps8419 hpd output " + (mode == 0 ? "auto" : mode == 1 ? "force low" : "force high"));
        }

        void PrHpdPulse()
        {
            int ms;
            if (!int.TryParse((prHpdMs.Text ?? "").Trim(), out ms) || ms < 10 || ms > 60000) { Put(prHpdState, "Pulse: 10..60000 ms", Pal.Red); return; }
            if (!CtrlWrite(new List<byte[]> { CtrlFrame(6, 5) })) return;
            Note("ps8419 hpd output pulse low " + ms + " ms");
            prHpdTimer.Interval = TimeSpan.FromMilliseconds(ms);
            prHpdTimer.Start();
        }

        // what the board reports (status record), not what was last clicked
        void PrHpdShow(bool live)
        {
            int m = live ? dec.HpdOvr : -1;
            for (int i = 0; i < 3; i++)
                { var bg = i == m ? (i == 0 ? Pal.Br(Pal.Sel) : Pal.Br(Pal.ChgBack)) : null; prHpdBtn[i].Background = bg; if (hdrHpdBtn != null) hdrHpdBtn[i].Background = bg; }
            string t = m < 0 ? "" : m == 0 ? "Board: auto" : m == 1 ? "Board: forced low" : "Board: forced high";
            if (prHpdTimer.IsEnabled) t += " · pulse running";
            Put(prHpdState, t, m > 0 ? Pal.Warn : Pal.Muted);
        }

        void PrPageChanged()
        {
            int p = PrPageIdx;
            Array.Copy(prData[p], prGrid.V, 256);
            for (int i = 0; i < 256; i++) prGrid.Chg[i] = prData[p][i] >= 0 && prPrev[p][i] >= 0 && prData[p][i] != prPrev[p][i];
            for (int i = 0; i < 256; i++) prGrid.Doc[i] = PsRegTable.Has(PrPages[p], i);
            if (prSelPage != p) { prGrid.Sel = -1; prSelPage = p; }
            prGrid.InvalidateVisual();
            for (int i = 0; i < prPageBtn.Length; i++) prPageBtn[i].Background = i == p ? Pal.Br(Pal.Sel) : null;
            PrInfoRefresh();
            int a = 8 + PrPages[p];
            SetChipBox(prChipAddr, "I2C 7-bit 0x" + a.ToString("X2") + " (wire W/R 0x" + (a * 2).ToString("X2") + " / 0x" + (a * 2 + 1).ToString("X2") + ")", Pal.Muted);
        }

        void PrCellClick(int o)
        {
            prGrid.Sel = o;
            prOff.Text = o.ToString("X2");
            prGrid.InvalidateVisual();
            PrInfoRefresh();
        }

        // the panel right of the grid: what the register table says about the selected cell
        void PrInfoRefresh()
        {
            if (prInfo == null) return;
            prInfo.Children.Clear();
            int sel = prGrid.Sel;
            if (sel < 0)
            {
                prInfo.Children.Add(new TextBlock { Text = "Click a cell to see what the register table says about it.", Foreground = Pal.Br(Pal.Muted), TextWrapping = TextWrapping.Wrap });
                return;
            }
            int page = PrPages[PrPageIdx], val = prGrid.V[sel] >= 0 ? prGrid.V[sel] : -1;
            var d = PsRegTable.Get(page, sel);
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = "P" + page + "." + sel.ToString("X2") + "h" + (d != null ? "   " + d.Access : ""), FontWeight = FontWeight.SemiBold });
            head.Children.Add(new TextBlock { Text = val >= 0 ? "0x" + val.ToString("X2") : "not read", FontFamily = Table.Mono, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Pal.Br(val >= 0 ? Pal.Text : Pal.Muted) });
            prInfo.Children.Add(head);
            if (d == null)
            {
                prInfo.Children.Add(new TextBlock { Text = "Not in the register table (Ver 0.1).", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 2, 0, 0) });
                if (val >= 0)
                    prInfo.Children.Add(new TextBlock { Text = "bits " + Convert.ToString(val, 2).PadLeft(8, '0').Insert(4, " "), FontFamily = Table.Mono, Foreground = Pal.Br(Pal.Muted) });
                return;
            }
            prInfo.Children.Add(new TextBlock { Text = d.Name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var sub = new TextBlock { Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 0, 0, 4), FontSize = 13 };
            sub.Inlines.Add(new Avalonia.Controls.Documents.Run(d.Group + " · initial 0x" + d.Init.ToString("X2")));
            if (val >= 0 && val != d.Init) sub.Inlines.Add(new Avalonia.Controls.Documents.Run(" · changed") { Foreground = Pal.Br(Pal.Warn) });
            prInfo.Children.Add(sub);
            prInfo.Children.Add(new Border { Height = 1, Background = Pal.Br(Pal.Line), Margin = new Thickness(0, 0, 0, 4) });
            foreach (var f in d.Fields)
            {
                string bits = f.Hi == f.Lo ? "[" + f.Hi + "]" : "[" + f.Hi + ":" + f.Lo + "]";
                string mean = val >= 0 ? f.Mean((val >> f.Lo) & ((1 << (f.Hi - f.Lo + 1)) - 1)) : "";
                var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition(52, GridUnitType.Pixel));
                g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                var bt = new TextBlock { Text = bits, FontFamily = Table.Mono, Foreground = Pal.Br(Pal.Muted), FontSize = 13 };
                var tx = new StackPanel();
                tx.Children.Add(new TextBlock { Text = f.Name, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
                if (mean.Length > 0) tx.Children.Add(new TextBlock { Text = mean, TextWrapping = TextWrapping.Wrap, Foreground = Pal.Br(Pal.Accent), FontSize = 13 });
                Grid.SetColumn(tx, 1);
                g.Children.Add(bt); g.Children.Add(tx);
                prInfo.Children.Add(g);
            }
            if (d.Note != null) prInfo.Children.Add(new TextBlock { Text = d.Note, Foreground = Pal.Br(Pal.Muted), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        void PrSetRes(string s, bool good)
        {
            prRes.Text = s;
            prRes.Foreground = Pal.Br(good ? Pal.Good : Pal.Red);
        }

        void PrReadClick()
        {
            int o;
            if (!PrHex(prOff.Text, out o)) { PrSetRes("Offset: two hex digits 00..FF", false); return; }
            prQ.Enqueue(new PrReq { Page = PrPages[PrPageIdx], Off = o });
            PrKick();
        }

        void PrDumpClick()
        {
            int a, b;
            if (!PrHex(prFrom.Text, out a) || !PrHex(prTo.Text, out b) || a > b) { prProg.Text = "from / to: hex 00..FF, from <= to"; return; }
            int p = PrPageIdx;
            prDumpPage = p;
            Array.Copy(prData[p], prPrev[p], 256);
            for (int i = a; i <= b; i++) prData[p][i] = -1;
            prDumping = true;
            prDumpFrom = prDumpNext = a;
            prDumpEnd = b;
            PrPageChanged();
            prGrid.Loading = "Loading page " + PrPages[p] + "…  0 / " + (b - a + 1);
            prGrid.LoadFrac = 0;
            prGrid.InvalidateVisual();
            prProg.Text = "Dumping page " + PrPages[p] + ": 0 / " + (b - a + 1);
            PrKick();
        }

        void PrStop(string why)
        {
            prQ.Clear();
            prDumping = false;
            prGrid.Loading = null;
            prGrid.InvalidateVisual();
            prProg.Text = why;
        }

        void PrKick() { if (!prTimer.IsEnabled) prTimer.Start(); UpdatePsRegs(); }

        void PrLogAdd(PrReq r, string value, string result, bool good)
        {
            var row = new TRow { Cells = new[] { DateTime.Now.ToString("HH:mm:ss.f"), "R P" + r.Page + "." + r.Off.ToString("X2"), value, result } };
            if (!good) row.Fore = Color.FromRgb(200, 30, 30);
            var list = prLog.Rows.ToList();
            list.Insert(0, row);
            if (list.Count > 400) list.RemoveRange(400, list.Count - 400);
            prLog.SetRows(list);
        }

        // the result of a read (val >= 0), no ACK (-2) or no reply (-3)
        void PrGotResult(PrReq r, int val)
        {
            int pi = Array.IndexOf(PrPages, r.Page);
            string key = "P" + r.Page + "." + r.Off.ToString("X2");
            if (val >= 0)
            {
                prData[pi][r.Off] = val;
                if (!r.Dump) { PrSetRes(key + "h = " + val.ToString("X2"), true); PrLogAdd(r, val.ToString("X2"), "ok", true); }
                Note("ps8419 R " + key + " = " + val.ToString("X2"));
            }
            else
            {
                prData[pi][r.Off] = -2;
                string res = val == -2 ? "no ACK" : "no reply";
                if (!r.Dump) { PrSetRes(key + "h: " + res, false); PrLogAdd(r, "", res, false); }
                Note("ps8419 R " + key + " " + res);
            }
            if (pi == PrPageIdx) PrPageChanged();
        }

        // every tick (from UpdateRetimer) and every 25 ms while a request is open: send the next frames, take the result
        void UpdatePsRegs()
        {
            if (prRead == null) return;
            bool tx = dec.Board == 0x24 && dec.CtrlKind == 1;
            bool rxHpd = dec.Board == 0x23 && dec.CtrlKind == 2;                // THCS RX: the HPD to the source (over the link)
            bool hpdBoard = tx || rxHpd;
            bool live = rx.CanWrite;
            bool stale = live && (DateTime.Now - dec.CtrlPc).TotalSeconds > 3.5;
            string lt = !hpdBoard ? "Needs a THCS TX (0x24) or RX (0x23) build with the runtime control" : !live ? "Not connected" : stale ? "No status for more than 3 s" : "Board answering";
            SetChipBox(prChipBoard, dec.Board >= 0 ? Decoder.BoardName(dec.Board) + " · build " + dec.Build : "No board yet", Pal.Text);
            SetChipBox(prChipLive, lt, hpdBoard && live && !stale ? Pal.Good : Pal.Warn);
            bool can = hpdBoard && live && !stale;
            bool hasPs = tx && !dec.PsAbsent;                       // a build without the PS8419 monitor still has the HPD output
            if (hdrHpdGrp != null && hdrHpdGrp.IsVisible != hpdBoard) { hdrHpdGrp.IsVisible = hpdBoard; hdrHpdLbl.IsVisible = hpdBoard; }     // any board with the HPD output, with or without PS8419
            bool psOk = can && hasPs;
            PrEnable(can, psOk);
            if (prSubTabs.IsVisible != hasPs) prSubTabs.IsVisible = hasPs;
            Put(prPsState, dec.Ps8419En == 0 ? "off" : dec.Ps8419.Length > 0 ? dec.Ps8419 : hasPs ? "-" : "not in this build", dec.Ps8419.Length > 0 && dec.Ps8419En != 0 ? Pal.Text : Pal.Muted);

            for (;;)
            {
                if (prInf != null)
                {
                    if (dec.PsRdVer != prVerBase)
                    {
                        var r = prInf;
                        prInf = null;
                        PrGotResult(r, dec.PsRdNack ? -2 : dec.PsRdVal);
                    }
                    else if ((DateTime.Now - prSent).TotalMilliseconds > 700)
                    {
                        var r = prInf;
                        prInf = null;
                        PrGotResult(r, -3);
                    }
                    else return;
                }
                if (prQ.Count == 0 && prDumping)
                {
                    if (prDumpNext <= prDumpEnd) prQ.Enqueue(new PrReq { Page = PrPages[prDumpPage], Off = prDumpNext++, Dump = true });
                    else
                    {
                        prDumping = false;
                        prGrid.Loading = null;
                        prGrid.InvalidateVisual();
                        int nk = 0, p = prDumpPage;
                        for (int i = prDumpFrom; i <= prDumpEnd; i++) if (prData[p][i] == -2) nk++;
                        prProg.Text = "Page " + PrPages[p] + " dumped: " + (prDumpEnd - prDumpFrom + 1) + " registers" + (nk > 0 ? ", " + nk + " without ACK" : "");
                        PrLogAdd(new PrReq { Page = PrPages[p], Off = prDumpFrom, Dump = true }, "", "dump " + prDumpFrom.ToString("X2") + ".." + prDumpEnd.ToString("X2") + (nk > 0 ? " (" + nk + " no ACK)" : ""), true);
                    }
                }
                if (prQ.Count == 0) { if (prTimer.IsEnabled) prTimer.Stop(); return; }
                if (!psOk) { PrStop(hasPs ? "Stopped: no connection to the board" : "No PS8419 in this build"); if (prTimer.IsEnabled) prTimer.Stop(); return; }
                var q = prQ.Dequeue();
                if (!CtrlWrite(PsReadFrames(q.Page, q.Off))) { PrStop("Send failed"); return; }
                prInf = q;
                prSent = DateTime.Now;
                prVerBase = dec.PsRdVer;
                if (prDumping)
                {
                    int tot = prDumpEnd - prDumpFrom + 1, done = prDumpNext - prDumpFrom;
                    prGrid.Loading = "Loading page " + q.Page + "…  " + done + " / " + tot;
                    prGrid.LoadFrac = (double)done / tot;
                    prGrid.InvalidateVisual();
                    prProg.Text = "Dumping page " + q.Page + ": " + done + " / " + tot;
                }
                return;
            }
        }
    }
}
