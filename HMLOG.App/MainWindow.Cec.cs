// CEC tab: Events page (one row per CEC message of the log, the bytes of the selected one below) and
// Waveform page (CEC line and sink HPD, WaveView). Port of MainForm.Wave.cs of the Windows version.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HMLOG.App
{
    partial class MainWindow
    {
        WaveView waveView;
        TabControl cecSub;
        TabItem cecTabEv, cecTabWave;
        Table cecTable;
        ToggleButton chkWaveLive, chkWaveHpd, chkEvMsg, chkEvPoll, chkEvBc, chkEvBad, chkEvLive;
        Button[] waveSpan;
        ComboBox cbWave;
        TextBox tbEvFind;
        TextBlock wvHpd, wvUart, wvCursor, wvDelta, wvCecN, wvCecMsg, evBytes, evTime, evHdr, evRes;
        int waveListCount = -1, waveListVer = -1, cecEvVer = -1;
        bool waveBuilding, cecEvDirty = true, waveFitNext;
        Avalonia.Threading.DispatcherTimer waveAnim;
        double waveHpdSeen = double.NaN;
        DateTime waveListAt = DateTime.MinValue, cecEvAt = DateTime.MinValue;
        static readonly double[] WaveSpanUs = { 1e6, 10e6, 60e6 };
        static readonly string[] WaveSpanName = { "1 s", "10 s", "60 s" };

        static string WaveTimeText(long us)
        {
            return (us / 1e6).ToString("0.000000", CultureInfo.InvariantCulture) + " s";
        }

        // a card with name / value rows (the values are the given TextBlocks)
        Control KvCard(string title, params object[] nv)
        {
            var g = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (int r = 0; r < nv.Length / 2; r++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var n = new TextBlock { Text = (string)nv[2 * r], Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 3, 16, 3) };
                var v = (TextBlock)nv[2 * r + 1];
                Grid.SetRow(n, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                g.Children.Add(n); g.Children.Add(v);
            }
            return Card(title, g);
        }

        TextBlock Val(bool mono = false)
        {
            var t = new TextBlock { Text = "-", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            if (mono) t.FontFamily = Table.Mono;
            return t;
        }

        static void Put(TextBlock t, string text, Color c)
        {
            if (t.Text != text) t.Text = text;
            t.Foreground = Pal.Br(c);
        }
        // buttons side by side as one choice: one outline, thin dividers, every button as wide as the widest, the active one is tinted
        Border SegButtons(string[] names, Action<int> click, out Button[] btns)
        {
            var grid = new UniformGrid { Rows = 1 };
            btns = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                var b = new Button { Content = names[i], Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), BorderBrush = Pal.Br(Pal.Line), Background = Brushes.Transparent, MinHeight = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                b.Click += (s, e) => click(k);
                btns[i] = b;
                grid.Children.Add(b);
            }
            return new Border { Child = grid, BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Background = Pal.Br(Pal.Surface) };
        }

        Control Cec_Bar(params Control[] items)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var c in items) { c.Margin = new Thickness(0, 2, 6, 2); wp.Children.Add(c); }
            return wp;
        }

        Control BuildCecTab()
        {
            if (waveAnim == null)
            {
                // the live view scrolls smoothly (25 fps) while the waveform page is shown and follows the newest time
                waveAnim = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                waveAnim.Tick += (s, e) =>
                {
                    if (waveView == null || !waveView.Live || !Visible(waveView)) return;
                    waveView.Dec = dec;
                    waveView.Running = rx.IsOpen;
                    waveView.Refresh2();
                };
                waveAnim.Start();
            }
            // state shared with the old window is built again
            waveListCount = waveListVer = cecEvVer = -1;
            cecEvDirty = true;
            waveHpdSeen = double.NaN;

            cecTabEv = new TabItem { Header = "EVENTS", Content = BuildCecEvents() };
            cecTabWave = new TabItem { Header = "WAVEFORM", Content = BuildCecWave() };
            cecSub = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
            cecSub.Items.Add(cecTabEv);
            cecSub.Items.Add(cecTabWave);
            return cecSub;
        }

        // ------------------------------------------------------------------
        // Waveform page
        // ------------------------------------------------------------------
        Control BuildCecWave()
        {
            waveView = new WaveView { Dec = dec };
            waveView.CursorsChanged += WaveShowCursor;
            waveView.ViewChanged += WaveViewChanged;
            waveView.DoubleTapped += (s, e) => { chkWaveLive.IsChecked = true; waveView.OnDoubleClick(); };
            ToolTip.SetTip(waveView, "Wheel: zoom (0.5 ms .. 60 s). Drag: move. Click: cursor A. Shift+click or right click: cursor B. Double click: fit. Middle click: clear the cursors");

            chkWaveLive = Chip("Live", true, v => { waveView.Live = v; if (v) waveView.Refresh2(); },
                               "Live: the right edge of the view follows the newest time (off: the view stays where it is)");
            chkWaveHpd = Chip("HPD trigger", true, v => { },
                              "HPD trigger: when the sink HPD changes, the view jumps to it (CEC before and after); Live is switched off. Press Live to follow again");
            var span = SegButtons(WaveSpanName, k => waveView.SetSpan(WaveSpanUs[k]), out waveSpan);
            var bar1 = Cec_Bar(IconCap(Icons.Usb, "Follow: the right edge of the view and the HPD trigger"), chkWaveLive, chkWaveHpd, IconCap(Icons.View, "Time axis of the view"), span,
                Btn("Zoom in", () => waveView.Zoom(1.6, waveView.V0 + waveView.VL / 2), "Zoom in (mouse wheel)"),
                Btn("Zoom out", () => waveView.Zoom(0.625, waveView.V0 + waveView.VL / 2), "Zoom out (mouse wheel)"),
                Btn("Fit", () => { chkWaveLive.IsChecked = true; waveView.FitAll(); }, "Show everything that is held (double click)"),
                Btn("PNG", async () => await WaveSavePng(), "Save the waveform as a PNG image", Icons.Save));

            cbWave = new ComboBox { Width = 520, VerticalAlignment = VerticalAlignment.Center };
            cbWave.SelectionChanged += (s, e) => WaveGoFrame(cbWave.SelectedIndex);
            ToolTip.SetTip(cbWave, "CEC frames of the log (newest last): pick one to show it");
            var bar2 = Cec_Bar(IconCap(Icons.Doc, "CEC frames of the log"), cbWave,
                Btn("", () => { if (cbWave.SelectedIndex > 0) cbWave.SelectedIndex--; }, "Older CEC frame", Icons.Up),
                Btn("", () => { if (cbWave.SelectedIndex < cbWave.ItemCount - 1) cbWave.SelectedIndex++; }, "Newer CEC frame", Icons.Down),
                IconCap(Icons.Find, "Go to an HPD edge"),
                Btn("HPD", () => WaveGoHpd(-1), "Previous HPD edge", Icons.Up),
                Btn("HPD", () => WaveGoHpd(1), "Next HPD edge", Icons.Down));

            wvHpd = Val(); wvUart = Val(); wvCursor = Val(true); wvDelta = Val(true); wvCecN = Val(); wvCecMsg = Val();
            var cards = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            cards.ColumnDefinitions.Add(new ColumnDefinition(22, GridUnitType.Star));
            cards.ColumnDefinitions.Add(new ColumnDefinition(24, GridUnitType.Star));
            cards.ColumnDefinitions.Add(new ColumnDefinition(54, GridUnitType.Star));
            var c1 = KvCard("STATE", "HPD", wvHpd, "UART", wvUart);
            var c2 = KvCard("CURSOR", "A / B", wvCursor, "Delta", wvDelta);
            var c3 = KvCard("CEC", "Frames", wvCecN, "Message", wvCecMsg);
            Grid.SetColumn(c2, 1); Grid.SetColumn(c3, 2);
            c1.Margin = c2.Margin = new Thickness(0, 0, 8, 8);
            c3.Margin = new Thickness(0, 0, 0, 8);
            cards.Children.Add(c1); cards.Children.Add(c2); cards.Children.Add(c3);

            var top = new StackPanel();
            top.Children.Add(bar1); top.Children.Add(bar2); top.Children.Add(cards);
            var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(top, Dock.Top);
            dp.Children.Add(top);
            dp.Children.Add(new Border { Child = waveView, BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(1) });
            WaveViewChanged();
            return dp;
        }

        void WaveShowCursor()
        {
            if (waveView == null) return;
            Put(wvCursor, waveView.CursorText(), Pal.Text);
            string d = waveView.DeltaText();
            Put(wvDelta, d, d == "-" ? Pal.Muted : Pal.Text);
            WaveCecMessage();
        }

        // the frame at cursor A, else the one picked in the list, else the newest
        void WaveCecMessage()
        {
            var fr = waveView.Frames;
            if (fr.Count == 0) { Put(wvCecMsg, "-", Pal.Muted); return; }
            CecFrame pick = cbWave.SelectedIndex >= 0 && cbWave.SelectedIndex < fr.Count ? fr[cbWave.SelectedIndex] : fr[fr.Count - 1];
            if (!double.IsNaN(waveView.CurA))
                foreach (var f in fr) if (waveView.CurA >= f.A && waveView.CurA <= (f.B > f.A ? f.B : f.A + 1)) pick = f;
            Put(wvCecMsg, pick.Text, pick.Error || !pick.Acked ? Pal.Red : Pal.Text);
        }

        void WaveViewChanged()
        {
            if (waveView == null || chkWaveLive == null) return;
            if (waveView.Live != (chkWaveLive.IsChecked == true)) chkWaveLive.IsChecked = waveView.Live;
            for (int i = 0; i < waveSpan.Length; i++)
            {
                bool on = Math.Abs(waveView.VL - WaveSpanUs[i]) < WaveSpanUs[i] * 0.03;
                waveSpan[i].Background = on ? Pal.Br(Pal.Sel) : null;
            }
        }

        void WaveGoFrame(int i)
        {
            if (waveBuilding || waveView == null || i < 0 || i >= waveView.Frames.Count) return;
            var f = waveView.Frames[i];
            double len = Math.Max(f.B - f.A, 1000) * 2.2;
            waveView.CenterOn((f.A + f.B) / 2.0, Math.Max(len, 120000));
            chkWaveLive.IsChecked = false;
            WaveCecMessage();
        }

        void WaveGoHpd(int dir)
        {
            var ed = dec.HpdEdges;
            if (ed.Count == 0) { Say("CEC: no HPD edge in the log yet"); return; }
            double mid = waveView.V0 + waveView.VL / 2;
            KeyValuePair<long, int>? pick = null;
            if (dir < 0) { for (int i = ed.Count - 1; i >= 0; i--) if (ed[i].Key < mid - waveView.VL * 0.01) { pick = ed[i]; break; } }
            else { for (int i = 0; i < ed.Count; i++) if (ed[i].Key > mid + waveView.VL * 0.01) { pick = ed[i]; break; } }
            if (pick == null) return;
            chkWaveLive.IsChecked = false;
            waveView.CenterOn(pick.Value.Key + Math.Max(waveView.VL, 1e6) * 0.3, Math.Max(waveView.VL, 1e6));
        }

        async System.Threading.Tasks.Task WaveSavePng()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "wave_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", Title = "Save waveform" });
            if (f == null) return;
            try { waveView.SavePng(f.Path.LocalPath); Say("CEC: saved " + f.Name); }
            catch (Exception ex) { Say("Cannot save: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Events page
        // ------------------------------------------------------------------
        TextBlock EvVal() { return Val(true); }

        Control BuildCecEvents()
        {
            chkEvMsg = Chip("Messages", true, v => cecEvDirty = true, "Messages to one device (directed, with an opcode)");
            chkEvPoll = Chip("Polls", true, v => cecEvDirty = true, "Polls: a header only (is the device there?)");
            chkEvBc = Chip("Broadcast", true, v => cecEvDirty = true, "Broadcast messages (destination All)");
            chkEvBad = Chip("NACK / errors", false, v => cecEvDirty = true, "Only the messages with a NACK, a bit error, a cut or something odd (red / amber rows)");
            chkEvLive = Chip("Live", true, v => { }, "Live: the list scrolls to the newest message");
            tbEvFind = new TextBox { Width = 240, Watermark = "device, opcode, operands, result", VerticalAlignment = VerticalAlignment.Center };
            tbEvFind.TextChanged += (s, e) => cecEvDirty = true;
            var bar = Cec_Bar(IconCap(Icons.Filter, "Show: the kinds of messages in the list"), chkEvMsg, chkEvPoll, chkEvBc, chkEvBad, IconCap(Icons.Find, "Search the listed messages"), tbEvFind, chkEvLive,
                              Btn("Export", async () => await CecExport(), "Save the listed messages as a text file", Icons.Export));

            cecTable = new Table("#", 52, "Time", 100, "+Delta", 96, "From", 76, "To", 76, "Message", 230, "Operands", 300, "Result", 250, "Len", 70);
            cecTable.SelectionChanged += CecEvDetail;
            cecTable.List.DoubleTapped += (s, e) => CecEvOpen();

            evBytes = EvVal(); evTime = EvVal(); evHdr = EvVal(); evRes = EvVal();
            var g = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition(i % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var vv = new[] { evBytes, evTime, evHdr, evRes };
            string[] nm = { "Bytes", "Time", "Header", "Result" };
            for (int i = 0; i < 4; i++)
            {
                int col = (i % 2) * 2, row = i / 2;
                var n = new TextBlock { Text = nm[i], Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 3, 14, 3) };
                Grid.SetRow(n, row); Grid.SetColumn(n, col);
                Grid.SetRow(vv[i], row); Grid.SetColumn(vv[i], col + 1);
                vv[i].Margin = new Thickness(0, 3, 24, 3);
                g.Children.Add(n); g.Children.Add(vv[i]);
            }
            var card = Card("MESSAGE", g);
            card.Margin = new Thickness(0, 6, 0, 0);

            var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(card, Dock.Bottom);
            dp.Children.Add(bar);
            dp.Children.Add(card);
            dp.Children.Add(cecTable);
            return dp;
        }

        static string CecDelta(double us)
        {
            return us >= 1e6 ? "+" + (us / 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " s" : "+" + (us / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        }

        // the list again when frames or filters changed (at most twice a second while edges come in)
        void CecEvUpdate()
        {
            if (cecTable == null || waveView == null) return;
            var fr = waveView.Frames;
            bool changed = cecEvVer != dec.EdgeVer;
            if (!cecEvDirty && !(changed && (DateTime.Now - cecEvAt).TotalMilliseconds > 500)) return;
            cecEvDirty = false;
            cecEvAt = DateTime.Now;
            cecEvVer = dec.EdgeVer;
            tabCecHeader(fr.Count);
            int selFrame = cecTable.Selected != null ? (int)cecTable.Selected.Tag : -1;
            string find = (tbEvFind.Text ?? "").Trim().ToLowerInvariant();
            var rows = new List<TRow>();
            int keepIdx = -1;
            for (int i = 0; i < fr.Count; i++)
            {
                var f = fr[i];
                bool poll = f.Bytes.Count == 1, bc = f.Dest == 15 && f.Bytes.Count > 1;
                if (poll ? chkEvPoll.IsChecked != true : bc ? chkEvBc.IsChecked != true : chkEvMsg.IsChecked != true) continue;
                int kind;
                string res = CecDecoder.ResultText(f, out kind);
                if (chkEvBad.IsChecked == true && kind == 0) continue;
                string from = f.Bytes.Count > 0 ? CecDecoder.DevName(f.Init) : "", to = f.Bytes.Count > 0 ? CecDecoder.DevName(f.Dest) : "";
                string msg = CecDecoder.Message(f), ops = CecDecoder.OperandText(f);
                if (find.Length > 0 && (from + " " + to + " " + msg + " " + ops + " " + res).ToLowerInvariant().IndexOf(find, StringComparison.Ordinal) < 0) continue;
                var row = new TRow
                {
                    Cells = new[] { (i + 1).ToString(), WaveTimeText((long)f.A), i > 0 ? CecDelta(f.A - fr[i - 1].A) : "", from, to, msg, ops, res,
                                    ((f.B - f.A) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " ms" },
                    Tag = i
                };
                if (kind == 2) row.Fore = Color.FromRgb(200, 30, 30);
                else if (kind == 1) row.Fore = Color.FromRgb(190, 120, 0);
                else if (poll) row.Fore = Color.FromRgb(140, 140, 140);
                rows.Add(row);
                if (i == selFrame) keepIdx = rows.Count - 1;
            }
            cecTable.SetRows(rows);
            if (keepIdx >= 0) cecTable.SelectedIndex = keepIdx;
            else if (rows.Count > 0 && chkEvLive.IsChecked == true) cecTable.SelectedIndex = rows.Count - 1;
            if (chkEvLive.IsChecked == true && rows.Count > 0) cecTable.ScrollTo(rows.Count - 1);
            CecEvDetail();
        }

        void tabCecHeader(int n)
        {
            cecTabEv.Header = n > 0 ? "EVENTS (" + n + ")" : "EVENTS";
        }

        void CecEvDetail()
        {
            if (evBytes == null || waveView == null) return;
            var sel = cecTable.Selected;
            if (sel == null)
            {
                foreach (var l in new[] { evBytes, evTime, evHdr, evRes }) Put(l, "-", Pal.Muted);
                return;
            }
            int i = (int)sel.Tag;
            if (i < 0 || i >= waveView.Frames.Count) return;
            var f = waveView.Frames[i];
            int kind;
            string res = CecDecoder.ResultText(f, out kind);
            Put(evBytes, f.Bytes.Count > 0 ? CecDecoder.HexBytes(f) : "-", Pal.Text);
            Put(evTime, WaveTimeText((long)f.A) + ", " + ((f.B - f.A) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " ms", Pal.Text);
            Put(evHdr, f.Bytes.Count > 0 ? CecDecoder.DevName(f.Init) + " (" + f.Init + ") > " + CecDecoder.DevName(f.Dest) + " (" + f.Dest + "), " + (f.Dest == 15 ? "broadcast" : "directed") : "-", Pal.Text);
            Put(evRes, res, kind == 2 ? Pal.Red : kind == 1 ? Pal.Warn : Pal.Text);
        }

        // the selected message on the Waveform page
        void CecEvOpen()
        {
            var sel = cecTable.Selected;
            if (sel == null) return;
            int i = (int)sel.Tag;
            cecSub.SelectedIndex = 1;
            if (i >= 0 && i < waveView.Frames.Count)
            {
                waveBuilding = true;
                if (i < cbWave.ItemCount) cbWave.SelectedIndex = i;
                waveBuilding = false;
                WaveGoFrame(i);
            }
        }

        async System.Threading.Tasks.Task CecExport()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "cec.txt", Title = "Save CEC messages" });
            if (f == null) return;
            var lines = cecTable.Rows.Select(r => string.Join("\t", r.Cells.Skip(1))).ToList();
            try { File.WriteAllLines(f.Path.LocalPath, lines); Say("Saved " + f.Name); }
            catch (Exception ex) { Say("Cannot save: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // every tick
        // ------------------------------------------------------------------
        void UpdateCec()
        {
            if (waveView == null) return;
            waveView.Dec = dec;                                  // the decoder is replaced by Clear / a new file
            bool onWave = Visible(waveView), onEv = Visible(cecTable);
            if (!onWave && !onEv) return;
            if (onEv) { waveView.Refresh2(); CecEvUpdate(); }
            if (!onWave) return;
            if (waveFitNext)
            {
                // a log just opened: show the last hot plug (HPD edge) with the CEC around it, else everything held
                waveFitNext = false;
                waveView.Refresh2();
                if (dec.HpdEdges.Count > 0)
                {
                    double span = Math.Max(waveView.VL, 1e6);
                    chkWaveLive.IsChecked = false;
                    waveView.CenterOn(dec.HpdEdges[dec.HpdEdges.Count - 1].Key + span * 0.3, span);
                }
                else { chkWaveLive.IsChecked = true; waveView.FitAll(); }
            }

            string[] baud = { "921600", "2 M", "3 M", "?" };
            Put(wvUart, dec.UartSel >= 0 ? baud[Math.Min(dec.UartSel, 3)] : "-", dec.UartSel >= 0 ? Pal.Text : Pal.Muted);
            Put(wvHpd, dec.Hpd == 1 ? "High" : dec.Hpd == 0 ? "Low" : "-", dec.Hpd == 1 ? Pal.Good : dec.Hpd == 0 ? Pal.Warn : Pal.Muted);
            if (dec.EdgeVer == waveListVer && waveView.Live == (chkWaveLive.IsChecked == true)) { if (waveView.Live) waveView.Refresh2(); return; }
            // the HPD trigger: a new HPD edge brings the view to it
            if (chkWaveHpd.IsChecked == true && dec.HpdEdges.Count > 0)
            {
                double last = dec.HpdEdges[dec.HpdEdges.Count - 1].Key;
                if (double.IsNaN(waveHpdSeen)) waveHpdSeen = last;                      // edges from before are not triggers
                else if (last > waveHpdSeen && rx.IsOpen)
                {
                    waveHpdSeen = last;
                    double span = Math.Max(waveView.VL, 1e6);
                    chkWaveLive.IsChecked = false;
                    waveView.CenterOn(last + span * 0.3, span);
                }
            }
            else if (dec.HpdEdges.Count == 0) waveHpdSeen = double.NaN;
            waveView.Refresh2();
            if ((DateTime.Now - waveListAt).TotalMilliseconds < 500 && waveListCount >= 0) return;
            waveListAt = DateTime.Now;
            waveListVer = dec.EdgeVer;
            var fr = waveView.Frames;
            if (fr.Count != waveListCount)
            {
                int old = cbWave.SelectedIndex;
                waveBuilding = true;
                var items = new List<string>();
                for (int i = 0; i < fr.Count; i++)
                    items.Add(string.Format(CultureInfo.InvariantCulture, "#{0}   {1}   {2}   ({3:0.#} ms)", i + 1, WaveTimeText((long)fr[i].A), fr[i].Text, (fr[i].B - fr[i].A) / 1000.0));
                cbWave.ItemsSource = items;
                if (fr.Count > 0) cbWave.SelectedIndex = old >= 0 && old < fr.Count && !waveView.Live ? old : fr.Count - 1;
                waveBuilding = false;
                waveListCount = fr.Count;
                int bad = 0;
                foreach (var f in fr) if (f.Error || !f.Acked || f.Partial) bad++;
                Put(wvCecN, fr.Count == 0 ? "-" : fr.Count + (bad > 0 ? "  (" + bad + " NACK / error / cut)" : "  all acknowledged"),
                    fr.Count == 0 ? Pal.Muted : bad > 0 ? Pal.Warn : Pal.Text);
                WaveCecMessage();
            }
        }
    }
}
