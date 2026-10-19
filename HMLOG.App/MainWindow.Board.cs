// BOARD tab, OPTICAL page: runtime control of the VCSEL driver (Tx 0x24 / 0x25: EQ / Imod / Imean) and the TIA
// (Rx 0x23 / 0x26: Vswing / TIAbias) of THCS boards built with G_RT_CTRL. Port of MainForm.Ctrl.cs.
// Commands are 2 bytes on the same serial port the log comes from (the board listens on PH2_HPD / K14):
//   byte 0 = 1 bbbb rrr  (board low nibble, register)   byte 1 = 0 vvvvvvv
//   register 0..2 = staged value, 6 = command (value 1 apply, 2 revert)
// The applied values come back in the status record (Decoder.OnCtrl). Values are volatile.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HMLOG.App
{
    // one value of a card: name + hint, slider, value text, reset arrow, and a line that only shows
    // when the chip does not hold what was sent
    class CtrlRow
    {
        public string Name, Hint;
        public int Reg, Min, Max, Def;
        public Func<int, string> Fmt;
        public TextBlock NameL, HintL, ValL, Mism;
        public Slider Tb;
        public Button Reset;
        public int Value
        {
            get { return (int)Math.Round(Tb.Value); }
            set { Tb.Value = Math.Max(Min, Math.Min(Max, value)); }
        }
    }

    partial class MainWindow
    {
        const double CardMaxW = 1040;

        // a page of cards: all cards of one width (the window width up to CardMaxW), left aligned
        Control PageScroll(Control content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Left;
            var sv = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            sv.SizeChanged += (s, e) => content.Width = Math.Max(480, Math.Min(CardMaxW, sv.Bounds.Width - 44));
            return sv;
        }
        Border ctrlChipBoard, ctrlChipLive, ctrlChipWarn;
        Control[] ctrlCardBox = new Control[2];
        Control ctrlStatBox;
        readonly List<CtrlRow>[] ctrlRows = { new List<CtrlRow>(), new List<CtrlRow>() };
        Button btnCtrlApply, btnCtrlDiscard, btnCtrlReset, btnCtrlCopy;
        ToggleButton chkCtrlLive;
        TextBlock ctrlSum, ctrlMsg;
        TiaView tiaView;
        readonly DispatcherTimer ctrlLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bool ctrlBuilding, ctrlTimerHooked;
        int ctrlKindShown;                                          // group shown (1 VCSEL, 2 TIA)
        int ctrlSyncVer = -1;                                       // echo version the sliders were set from
        bool ctrlPending;                                           // sent, waiting for the echo
        DateTime ctrlSentAt = DateTime.MinValue;
        public bool CtrlTestNoConfirm;

        static string FmtEq(int v) { return v + " dB"; }
        static string FmtImod(int v) { string m = Decoder.ImodMa(v); return m.Length > 0 ? m + " mA" : "code " + v; }
        static string FmtImean(int v) { return Decoder.ImeanMa(v).ToString("F2") + " mA"; }
        static string FmtSwing(int v) { return Decoder.SwingText[v & 3] + " mVpp"; }
        static string FmtBias(int v) { return Decoder.TiaBiasMa(v).ToString("F1") + " mA"; }

        // a small rounded label (status strip)
        Border NewChipBox()
        {
            return new Border
            {
                Background = Pal.Br(Pal.Header), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 8, 6),
                Child = new TextBlock(), IsVisible = false
            };
        }

        static void SetChipBox(Border b, string text, Color fore)
        {
            var t = (TextBlock)b.Child;
            bool vis = text.Length > 0;
            if (b.IsVisible != vis) b.IsVisible = vis;
            if (t.Text != text) t.Text = text;
            t.Foreground = Pal.Br(fore);
        }

        // card with the page's usual width limit
        Control WideCard(string title, Control body)
        {
            var c = Card(title, body);
            return c;
        }

        // name | slider | value | reset, with the hint below the name
        CtrlRow NewRow(StackPanel host, string name, string hint, int min, int max, int def, Func<int, string> fmt, int reg, Action<CtrlRow> onChanged)
        {
            var cr = new CtrlRow { Name = name, Hint = hint, Min = min, Max = max, Def = def, Fmt = fmt, Reg = reg };
            var g = new Grid { Margin = new Thickness(12, 2, 12, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition(150, GridUnitType.Pixel));
            g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            g.ColumnDefinitions.Add(new ColumnDefinition(200, GridUnitType.Pixel));
            g.ColumnDefinitions.Add(new ColumnDefinition(40, GridUnitType.Pixel));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            cr.NameL = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Foreground = Pal.Br(Pal.Text) };
            cr.HintL = new TextBlock { Text = hint, FontSize = 12.5, Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 0, 0, 4) };
            cr.Tb = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, def)), TickFrequency = 1, IsSnapToTickEnabled = true,
                                 SmallChange = 1, LargeChange = 1, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            cr.ValL = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = Table.Mono };
            cr.Mism = new TextBlock { FontSize = 12.5, Foreground = Pal.Br(Pal.Red), HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
            cr.Reset = new Button { Content = "↶", Padding = new Thickness(6, 0), MinHeight = 22, IsVisible = false, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(cr.Reset, "Back to the default (" + fmt(def) + ")");
            var c = cr;
            cr.Tb.PropertyChanged += (s, e) => { if (e.Property == RangeBase.ValueProperty) onChanged(c); };
            cr.Reset.Click += (s, e) => c.Value = c.Def;
            Grid.SetColumn(cr.Tb, 1); Grid.SetColumn(cr.ValL, 2); Grid.SetColumn(cr.Reset, 3);
            Grid.SetRow(cr.HintL, 1); Grid.SetColumn(cr.Mism, 2); Grid.SetRow(cr.Mism, 1);
            Grid.SetColumnSpan(cr.HintL, 2);
            g.Children.Add(cr.NameL); g.Children.Add(cr.Tb); g.Children.Add(cr.ValL); g.Children.Add(cr.Reset); g.Children.Add(cr.HintL); g.Children.Add(cr.Mism);
            host.Children.Add(g);
            return cr;
        }

        Control BuildOpticalPage()
        {
            foreach (var l in ctrlRows) l.Clear();
            ctrlKindShown = 0; ctrlSyncVer = -1; ctrlPending = false;

            ctrlChipBoard = NewChipBox(); ctrlChipLive = NewChipBox(); ctrlChipWarn = NewChipBox();
            chkCtrlLive = Chip("Apply on change", false, v => { }, "Every change is applied about a third of a second after the last move (Imod / Imean above the default are not asked about then)");
            chkCtrlLive.Margin = new Thickness(0, 0, 0, 6);
            var strip = new WrapPanel();
            strip.Children.Add(ctrlChipBoard); strip.Children.Add(ctrlChipLive); strip.Children.Add(ctrlChipWarn); strip.Children.Add(chkCtrlLive);

            var b0 = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            ctrlRows[0].Add(NewRow(b0, "EQ", "pre-emphasis · default " + FmtEq(1), 0, 7, 1, FmtEq, 0, CtrlRowChanged));
            ctrlRows[0].Add(NewRow(b0, "Imod", "modulation current · default " + FmtImod(12), 0, 15, 12, FmtImod, 1, CtrlRowChanged));
            ctrlRows[0].Add(NewRow(b0, "Imean", "bias current · default " + FmtImean(0), 0, 7, 0, FmtImean, 2, CtrlRowChanged));
            var b1 = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            ctrlRows[1].Add(NewRow(b1, "Vswing", "output swing · default " + FmtSwing(3), 0, 3, 3, FmtSwing, 0, CtrlRowChanged));
            ctrlRows[1].Add(NewRow(b1, "TIA bias", "bias current · default " + FmtBias(6), 0, 31, 6, FmtBias, 1, CtrlRowChanged));
            ctrlCardBox[0] = WideCard("VCSEL driver (SL82721)", b0);
            ctrlCardBox[1] = WideCard("TIA (SL82711)", b1);
            ctrlCardBox[0].IsVisible = ctrlCardBox[1].IsVisible = false;

            tiaView = new TiaView { Margin = new Thickness(12, 2, 12, 8) };
            ctrlStatBox = WideCard("STATUS · TIA lanes", tiaView);
            ctrlStatBox.IsVisible = false;

            // one apply bar for everything on the page
            ctrlSum = new TextBlock { Text = "No pending changes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
            btnCtrlDiscard = Btn("Discard", CtrlDiscard, "Back to what the board holds now");
            btnCtrlReset = Btn("Reset all to defaults", CtrlResetAll, "Put every slider on its default; Apply sends it");
            btnCtrlApply = Btn("Apply", async () => await CtrlApplyAll(true), "Send every changed value and apply it (the I2C write is repeated, VCSEL_EN stays on)");
            btnCtrlCopy = Btn("Copy as VHDL generic", async () => await CtrlCopyGeneric(), "The applied values as the G_RT_* generics of the TOP file: set them there and rebuild to make them permanent");
            var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            bar.Children.Add(ctrlSum);
            foreach (var b in new[] { btnCtrlDiscard, btnCtrlReset, btnCtrlApply, btnCtrlCopy }) { b.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(b); }
            ctrlMsg = new TextBlock { Text = "Values are volatile: a reset or power cycle returns the defaults.", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

            if (!ctrlTimerHooked)
            {
                ctrlTimerHooked = true;
                ctrlLiveTimer.Tick += async (s, e) => { ctrlLiveTimer.Stop(); if (chkCtrlLive != null && chkCtrlLive.IsChecked == true) await CtrlApplyAll(false); };
            }

            var sp = new StackPanel { Margin = new Thickness(16) };
            foreach (var c in new Control[] { strip, ctrlStatBox, ctrlCardBox[0], ctrlCardBox[1], bar, ctrlMsg }) sp.Children.Add(c);
            CtrlSetEnabled(false);
            return PageScroll(sp);
        }

        List<CtrlRow> CtrlCurrentRows()
        {
            return ctrlKindShown == 1 ? ctrlRows[0] : ctrlKindShown == 2 ? ctrlRows[1] : new List<CtrlRow>();
        }

        void CtrlSetEnabled(bool on)
        {
            foreach (var g in ctrlRows)
                foreach (var r in g) { r.Tb.IsEnabled = on; r.Reset.IsEnabled = on; }
            btnCtrlApply.IsEnabled = btnCtrlDiscard.IsEnabled = btnCtrlReset.IsEnabled = chkCtrlLive.IsEnabled = on;
            btnCtrlCopy.IsVisible = ctrlKindShown != 0;
        }

        // a slider moved or was reset
        void CtrlRowChanged(CtrlRow r)
        {
            CtrlShowRow(r, -1);
            CtrlUpdateSummary();
            if (!ctrlBuilding && chkCtrlLive.IsChecked == true) CtrlLiveSoon();
        }

        void CtrlLiveSoon() { ctrlLiveTimer.Stop(); ctrlLiveTimer.Start(); }

        // value text of a row: amber and * when it differs from the default, amber while it differs from what the board holds
        void CtrlShowRow(CtrlRow r, int held)
        {
            int v = r.Value;
            bool chg = v != r.Def;
            string t = r.Fmt(v) + (chg ? " *" : "");
            if (r.ValL.Text != t) r.ValL.Text = t;
            r.ValL.Foreground = Pal.Br(chg || (held >= 0 && held != v) ? Pal.Warn : Pal.Text);
            if (r.Reset.IsVisible != chg) r.Reset.IsVisible = chg;
        }

        void CtrlDiscard()
        {
            var rows = CtrlCurrentRows();
            ctrlBuilding = true;
            for (int i = 0; i < rows.Count; i++) if (dec.CtrlKind == ctrlKindShown) rows[i].Value = dec.CtrlVal[i];
            ctrlBuilding = false;
            ctrlLiveTimer.Stop();
            CtrlUpdateSummary();
        }

        void CtrlResetAll()
        {
            ctrlBuilding = true;
            foreach (var r in CtrlCurrentRows()) r.Value = r.Def;
            ctrlBuilding = false;
            foreach (var r in CtrlCurrentRows()) CtrlShowRow(r, -1);
            CtrlUpdateSummary();
            if (chkCtrlLive.IsChecked == true) CtrlLiveSoon();
        }

        async System.Threading.Tasks.Task CtrlApplyAll(bool confirm)
        {
            var rows = CtrlCurrentRows();
            if (CtrlPendingCount() > 0) await CtrlSend(rows, confirm);
        }

        int CtrlPendingCount()
        {
            int n = 0;
            var rows = CtrlCurrentRows();
            for (int i = 0; i < rows.Count; i++) if (dec.CtrlKind == ctrlKindShown && rows[i].Value != dec.CtrlVal[i]) n++;
            return n;
        }

        // 2 bytes of a frame to the board
        byte[] CtrlFrame(int reg, int val)
        {
            return new[] { (byte)(0x80 | ((dec.Board & 0xF) << 3) | (reg & 7)), (byte)(val & 0x7F) };
        }

        // the frames go out as one write; false (and a red message) when the port is closed
        bool CtrlWrite(List<byte[]> frames)
        {
            var all = new List<byte>();
            foreach (var f in frames) all.AddRange(f);
            string err = rx.Write(all.ToArray());
            if (err == null) return true;
            if (ctrlMsg != null) { ctrlMsg.Text = err.StartsWith("The port") ? err : "Send failed: " + err; ctrlMsg.Foreground = Pal.Br(Pal.Red); }
            return false;
        }

        async System.Threading.Tasks.Task CtrlSend(IList<CtrlRow> rows, bool confirm)
        {
            if (rows.Count == 0 || ctrlKindShown == 0) return;
            if (confirm && ctrlKindShown == 1 && !CtrlTestNoConfirm)
            {
                // raising the laser currents above the build default: ask first
                foreach (var r in rows)
                    if ((r.Reg == 1 || r.Reg == 2) && r.Value > r.Def &&
                        !await Confirm(r.Name + " " + r.Value + " is above the build default (" + r.Def + "). Raise the laser current?"))
                        return;
            }
            var frames = new List<byte[]>();
            foreach (var r in rows) frames.Add(CtrlFrame(r.Reg, r.Value));
            frames.Add(CtrlFrame(6, 1));                     // apply
            if (!CtrlWrite(frames)) return;
            ctrlPending = true;
            ctrlSentAt = DateTime.Now;
            ctrlMsg.Text = "Sent, waiting for the echo (status record, about once a second)…";
            ctrlMsg.Foreground = Pal.Br(Pal.Muted);
            // a note in the raw log: what the PC sent (the FPGA echo follows as a CTRL line)
            Note("ctrl sent " + string.Join(" ", rows.Select(r => r.Name.Replace(' ', '_') + "=" + r.Value)));
        }

        async System.Threading.Tasks.Task<bool> Confirm(string text)
        {
            var yes = new Button { Content = "Yes", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            var no = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            var w = new Window { Title = "OPTICAL", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = Pal.Br(Pal.Canvas) };
            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            row.Children.Add(yes); row.Children.Add(no);
            sp.Children.Add(row);
            w.Content = sp;
            yes.Click += (s, e) => w.Close(true);
            no.Click += (s, e) => w.Close(false);
            return await w.ShowDialog<bool>(this);
        }

        async System.Threading.Tasks.Task CtrlCopyGeneric()
        {
            if (ctrlKindShown == 0) return;
            int[] v = dec.CtrlKind == ctrlKindShown ? dec.CtrlVal : null;
            string s;
            if (ctrlKindShown == 1)
            {
                int[] a = v ?? new[] { ctrlRows[0][0].Value, ctrlRows[0][1].Value, ctrlRows[0][2].Value };
                s = string.Format("G_RT_EQ => {0}, G_RT_IMOD => {1}, G_RT_IMEAN => {2}", a[0], a[1], a[2]);
            }
            else
            {
                int[] a = v ?? new[] { ctrlRows[1][0].Value, ctrlRows[1][1].Value };
                s = string.Format("G_RT_VSWING => {0}, G_RT_TIABIAS => {1}", a[0], a[1]);
            }
            try
            {
                await Clipboard.SetTextAsync(s);
                ctrlMsg.Text = "Copied: " + s; ctrlMsg.Foreground = Pal.Br(Pal.Muted);
            }
            catch (Exception ex) { ctrlMsg.Text = "Copy failed: " + ex.Message; ctrlMsg.Foreground = Pal.Br(Pal.Red); }
        }

        // the row text: values with units; with mark = true a value that differs from the build default gets a *
        static string CtrlShort(int kind, int[] v, bool mark)
        {
            int[] d = Decoder.CtrlDefault(kind);
            Func<int, string> st = i => mark && v[i] != d[i] ? "*" : "";
            if (kind == 1)
            {
                string m = Decoder.ImodMa(v[1]);
                return "EQ " + v[0] + " dB" + st(0) + " · Imod " + (m.Length > 0 ? m + " mA" : v[1].ToString()) + st(1) +
                       " · Imean " + Decoder.ImeanMa(v[2]).ToString("F2") + " mA" + st(2);
            }
            return "Swing " + Decoder.SwingText[v[0] & 3] + " mVpp" + st(0) + " · Bias " + Decoder.TiaBiasMa(v[1]).ToString("F1") + " mA" + st(1);
        }

        void CtrlUpdateSummary()
        {
            if (ctrlSum == null) return;
            int n = CtrlPendingCount();
            string st = n == 0 ? "No pending changes" : n + " change" + (n > 1 ? "s" : "") + " not applied yet";
            if (ctrlSum.Text != st) ctrlSum.Text = st;
            ctrlSum.Foreground = Pal.Br(n == 0 ? Pal.Muted : Pal.Warn);
            string ab = n == 0 ? "Apply" : "Apply " + n;
            if ((string)btnCtrlApply.Content != ab) btnCtrlApply.Content = ab;
        }

        // every tick: status strip, cards, apply bar
        void UpdateOptical()
        {
            int kind = dec.CtrlKind;
            bool live = rx.CanWrite;
            bool stale = live && kind != 0 && (DateTime.Now - dec.CtrlPc).TotalSeconds > 3.5;
            bool anyStat = dec.TiaSd >= 0;
            if (ctrlStatBox.IsVisible != anyStat) ctrlStatBox.IsVisible = anyStat;
            if (anyStat) UpdateTia();

            int show = kind;
            if (show != ctrlKindShown)
            {
                ctrlKindShown = show;
                ctrlCardBox[0].IsVisible = show == 1;
                ctrlCardBox[1].IsVisible = show == 2;
                ctrlSyncVer = -1;
            }
            SetChipBox(ctrlChipBoard, dec.Board >= 0 ? Decoder.BoardName(dec.Board) + " · build " + dec.Build : "No board yet", Pal.Text);
            string lt;
            Color lc;
            if (show == 0)
            {
                lt = Decoder.CtrlBoardTx(dec.Board) || Decoder.CtrlBoardRx(dec.Board) ? "No runtime control in this build (G_RT_CTRL = false)"
                     : live ? "Waiting for the first status record of a THCS board…" : "Connect to a THCS board built with G_RT_CTRL";
                lc = Pal.Muted;
            }
            else if (!live) { lt = "Not connected"; lc = Pal.Muted; }
            else if (stale) { lt = "No status for more than 3 s"; lc = Pal.Warn; }
            else { lt = "Board answering · commands accepted: " + dec.CtrlCnt; lc = Pal.Good; }
            SetChipBox(ctrlChipLive, lt, lc);
            SetChipBox(ctrlChipWarn, show != 0 && dec.CtrlErr == 1 ? "the FPGA rejected a command" : "", Pal.Red);
            CtrlSetEnabled(show != 0 && live && !stale);

            if (show != 0)
            {
                var rows = ctrlRows[show - 1];
                // first echo (or a group change): take the applied values as the requested ones
                if (ctrlSyncVer < 0)
                {
                    ctrlBuilding = true;
                    for (int i = 0; i < rows.Count; i++) rows[i].Value = dec.CtrlVal[i];
                    ctrlBuilding = false;
                    ctrlSyncVer = dec.CtrlVer;
                }
                bool allMatch = true;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (dec.CtrlVal[i] != rows[i].Value) allMatch = false;
                    CtrlShowRow(rows[i], dec.CtrlVal[i]);
                }
                if (ctrlPending)
                {
                    // an echo that came after the command and matches what was sent
                    if (dec.CtrlUs >= 0 && dec.CtrlPc > ctrlSentAt && allMatch)
                    {
                        ctrlPending = false;
                        ctrlMsg.Text = "Applied (echo matches).";
                        ctrlMsg.Foreground = Pal.Br(Pal.Good);
                    }
                    else if ((DateTime.Now - ctrlSentAt).TotalSeconds > 3.0)
                    {
                        ctrlPending = false;
                        ctrlMsg.Text = dec.CtrlErr == 1 ? "The FPGA rejected a command." : "No matching echo within 3 s: the board did not take the command (K14 wiring / baud / board?).";
                        ctrlMsg.Foreground = Pal.Br(Pal.Red);
                    }
                }
                else if (stale)
                {
                    ctrlMsg.Text = "No status record for more than 3 s: the values shown may be old.";
                    ctrlMsg.Foreground = Pal.Br(Pal.Muted);
                }
            }
            CtrlUpdateSummary();
        }

        void UpdateTia()
        {
            bool stale = rx.CanWrite && (DateTime.Now - dec.CtrlPc).TotalSeconds > 3.5;
            tiaView.Set(dec.TiaSd, dec.TiaPd, "Readback " + (dec.TiaRb == 1 ? "ok" : "mismatch") + " · " +
                (stale ? "no new sweep for over 3 s" : "last sweep " + ShortTime(dec.TiaUs)));
        }

        Control BuildBoardTab()
        {
            var opt = new TabItem { Header = "OPTICAL", Content = BuildOpticalPage() };
            var ret = new TabItem { Header = "RETIMER", Content = BuildRetimerPage() };
            return new TabControl { Items = { opt, ret }, Margin = new Thickness(0, 4, 0, 0) };
        }

        // every tick: the BOARD pages (also while another tab is shown: the echo of a command is checked here)
        void UpdateBoard()
        {
            if (ctrlSum == null || prRead == null) return;
            UpdateOptical();
            UpdatePsEq();
            UpdatePsRegs();
        }

        // development aid (HMLOG_DEMO=tx|rx): a board that answers, without hardware
        string demoKind;
        int demoTick;

        void DemoStart()
        {
            demoKind = (Environment.GetEnvironmentVariable("HMLOG_DEMO") ?? "").ToLowerInvariant();
            if (demoKind != "tx" && demoKind != "rx") { demoKind = null; return; }
            rx.TestSink = b => true;
            foreach (var l in Demo.Once(demoKind == "tx")) rx.Q.Enqueue(new RxLine(DateTime.Now, l));
            if (demoKind == "rx") { dec.TiaSd = 0xB; dec.TiaRb = 1; dec.TiaUs = 4000000; dec.TiaPd[0] = 310; dec.TiaPd[1] = 296; dec.TiaPd[2] = 0; dec.TiaPd[3] = 301; }
        }

        void DemoTick()
        {
            if (demoKind == null || ++demoTick % 5 != 1) return;
            rx.Q.Enqueue(new RxLine(DateTime.Now, Demo.Status(demoKind == "tx")));
        }

        // development aid: the board's echo as a status line, the way the FPGA sends it
        public static string CtrlTestLine(bool tx, int[] v, bool ovr, int err, int cnt, int hpd = 0, bool psAbsent = false)
        {
            int c1 = (psAbsent ? 0x1000 : 0) | 0x8000 | (ovr ? 0x4000 : 0) | (err != 0 ? 0x2000 : 0);
            if (tx) c1 |= (v[0] << 9) | (v[1] << 5) | (v[2] << 2); else c1 |= (v[0] << 10) | (v[1] << 5);
            return string.Format("00000001 S {0} 00 26 10 09 01 00 00 00 {1:X2} {2:X2} {3:X2}", tx ? "24" : "23", c1 >> 8, c1 & 0xFF, cnt | (hpd << 4));
        }
    }

    // TIA lane table: lane, signal, PD current, code and a gauge with the low / high marks. The color follows the
    // signal strength (low or no signal red, high = overload amber, else green); the circle and the gauge say it too.
    class TiaView : Control
    {
        public readonly int[] Pd = new int[4];
        public int Sd = -1;
        public string Foot = "";
        const int Span = 400;                                 // gauge full scale, codes (about 775 uA)
        const double RH = 28;

        public TiaView() { Height = RH * 6; }

        public void Set(int sd, int[] pd, string foot)
        {
            bool same = Sd == sd && Foot == foot;
            for (int l = 0; l < 4; l++) { if (Pd[l] != pd[l]) same = false; Pd[l] = pd[l]; }
            Sd = sd; Foot = foot;
            if (!same) InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(Pal.Br(Pal.Surface), new Rect(Bounds.Size));
            double xs = 72, xp = xs + 112, xc = xp + 92, xg = xc + 78;
            double wg = Math.Max(80, Bounds.Width - xg - 8);
            double sz = 12.5;
            Gfx.Text(dc, "Lane", new Rect(0, 0, xs, RH), Pal.Muted, sz, false, false);
            Gfx.Text(dc, "Signal", new Rect(xs, 0, xp - xs, RH), Pal.Muted, sz, false, false);
            Gfx.Text(dc, "PD current", new Rect(xp, 0, xc - xp - 10, RH), Pal.Muted, sz, false, false, 2);
            Gfx.Text(dc, "Code", new Rect(xc, 0, xg - xc - 14, RH), Pal.Muted, sz, false, false, 2);
            Gfx.Text(dc, "0 – " + Math.Round(Decoder.PdUa(Span)).ToString("F0") + " µA  (marks: low " + Math.Round(Decoder.PdUa(Decoder.TiaLowCode)).ToString("F0") +
                     ", high " + Math.Round(Decoder.PdUa(Decoder.TiaHighCode)).ToString("F0") + ")", new Rect(xg, 0, wg, RH), Pal.Muted, sz, false, false);
            var line = new Pen(Pal.Br(Pal.Line), 1);
            for (int i = 0; i <= 4; i++) dc.DrawLine(line, new Point(0, (i + 1) * RH - 1), new Point(Bounds.Width, (i + 1) * RH - 1));
            for (int l = 0; l < 4; l++)
            {
                double y = (l + 1) * RH;
                bool sig = Sd >= 0 && ((Sd >> l) & 1) != 0;
                Color sc = !sig || Pd[l] < Decoder.TiaLowCode ? Pal.Red : Pd[l] > Decoder.TiaHighCode ? Pal.Warn : Pal.Good;
                Gfx.Text(dc, "Lane " + (l + 1), new Rect(0, y, xs, RH), Pal.Text, 14, true, false);
                double d = 10;
                var circle = new Rect(xs, y + (RH - d) / 2, d, d);
                if (sig) dc.DrawEllipse(Pal.Br(sc), null, circle.Center, d / 2, d / 2);
                else dc.DrawEllipse(null, new Pen(Pal.Br(sc), 1.5), circle.Center, d / 2 - 0.75, d / 2 - 0.75);
                Gfx.Text(dc, sig ? "Signal" : "No signal", new Rect(xs + d + 6, y, xp - xs - d, RH), sig ? Pal.Text : sc, 14, false, false);
                Gfx.Text(dc, sig ? Math.Round(Decoder.PdUa(Pd[l])).ToString("F0").PadLeft(3) + " µA" : "—", new Rect(xp, y, xc - xp - 10, RH), sc, 14, true, true, 2);
                Gfx.Text(dc, sig ? "0x" + Pd[l].ToString("X3") : "—", new Rect(xc, y, xg - xc - 14, RH), Pal.Muted, 14, false, true, 2);
                var gr = new Rect(xg, y + RH / 2 - 4, wg, 8);
                dc.FillRectangle(Pal.Br(Pal.Canvas), gr);
                if (sig) dc.FillRectangle(Pal.Br(sc), new Rect(gr.Left, gr.Top, gr.Width * Math.Min(Pd[l], Span) / (double)Span, gr.Height));
                dc.DrawRectangle(null, line, gr);
                var mk = new Pen(Pal.Br(Pal.Muted), 1);
                foreach (int mark in new[] { Decoder.TiaLowCode, Decoder.TiaHighCode })
                {
                    double mx = gr.Left + gr.Width * mark / (double)Span;
                    dc.DrawLine(mk, new Point(mx, gr.Top - 3), new Point(mx, gr.Bottom + 3));
                }
            }
            if (Foot.Length > 0) Gfx.Text(dc, Foot, new Rect(0, 5 * RH, Bounds.Width, RH), Pal.Muted, sz, false, false);
        }
    }
}

namespace HMLOG.App
{
    partial class MainWindow
    {
        // TARGET card: board / build, status line age, records lost, link errors (every tick: the age changes without new lines)
        void UpdateHealth()
        {
            if (stVal == null || stVal.Length <= StFpga + 3) return;
            var gray = Pal.Muted;
            if (dec.Board < 0) SetState(StFpga, dec.St.Beats > 0 ? "level 1–2 (no status line)" : "- (no status line: older FPGA)", gray);
            else SetState(StFpga, Decoder.BoardName(dec.Board) + "\nbuild " + dec.Build, Pal.Text);
            if (dec.StatPc == DateTime.MinValue || !rx.IsOpen)
                SetState(StFpga + 1, dec.St.StatusLines > 0 ? string.Format("{0} lines · missing {1}", dec.St.StatusLines, dec.St.StatusGaps) :
                                     dec.St.Beats > 0 ? string.Format("heartbeat {0}", dec.St.Beats) : "-",
                         dec.St.StatusGaps > 0 ? Pal.Red : gray);
            else
            {
                double age = (DateTime.Now - dec.StatPc).TotalSeconds;
                if (age > 2.5) SetState(StFpga + 1, string.Format("no response {0:F0} s", age), Pal.Red);
                else SetState(StFpga + 1, "OK" + (dec.St.StatusGaps > 0 ? string.Format(" · missing {0}", dec.St.StatusGaps) : ""),
                              dec.St.StatusGaps > 0 ? Pal.Warn : Pal.Good);
            }
            int lost = dec.St.FifoLost, lines = dec.St.Lost, uartErr = rx.UartErr;
            SetState(StFpga + 2, lost + lines + uartErr == 0 ? "0" : string.Format("FPGA {0} · lines {1} · UART {2}", lost, lines, uartErr),
                     lost + lines + uartErr > 0 ? Pal.Red : Pal.Good);
            if (dec.LinkErr < 0) SetState(StFpga + 3, "-", gray);
            else
            {
                string rate = dec.LinkErrRate >= 10 ? dec.LinkErrRate.ToString("F0") : dec.LinkErrRate.ToString("0.#");
                SetState(StFpga + 3, dec.LinkErrNew > 0 ? string.Format("{0} · {1}/s (10 s) · {2}", dec.LinkErrNew, rate, ShortTime(dec.LinkErrUs)) : "0",
                         dec.LinkErrNew > 0 ? Pal.Red : Pal.Good);
            }
        }
    }
}

namespace HMLOG.App
{
    partial class MainWindow
    {
        // the icon and text of a header button changed (Connect / Disconnect)
        void SetBtnLabel(Button b, string glyph, string text)
        {
            if (b.Content is StackPanel sp && sp.Children.Count == 2)
            {
                ((TextBlock)sp.Children[0]).Text = glyph;
                ((TextBlock)sp.Children[1]).Text = text;
            }
            ToolTip.SetTip(b, text == "Connect" ? "Open the selected port (Auto looks for the rate of the board: 3 M / 2 M / 921600)" : "Close the port");
        }
    }
}
