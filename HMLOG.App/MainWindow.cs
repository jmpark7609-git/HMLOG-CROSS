// Main window: toolbar, filter bar, status panel, tabs, serial input and the 200 ms tick that feeds the decoder.
// Port of MainForm.cs of the Windows version (WinForms -> Avalonia); the UI is built in code.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HMLOG.App
{
    partial class MainWindow : Window
    {
        static readonly Encoding Latin1 = Encoding.Latin1;
        readonly Settings set = Settings.Load();
        Decoder dec = new Decoder();
        readonly Rx rx = new Rx();
        readonly AutoSave save = new AutoSave();
        readonly List<string> raw = new List<string>();          // lines of the view (a reset clears them)
        readonly List<string> rawAll = new List<string>();       // every line since connect / open
        readonly List<RxLine> recentEv = new List<RxLine>();     // event lines since the last transfer (the resets keep them)
        string source = "";
        string wantPort;                                         // connected (or reconnecting) port
        int tickNo, hpdResets, linkResets;
        bool filterDirty = true;
        int viewVer = -1, shownGroupsLast;

        // controls
        TabControl tabs;
        TabItem tabHdcp, tabEdid, tabScdcTrain;
        ComboBox cbPort, cbBaud, cbTime;
        Button btnConnect;
        TextBlock stLabel, stRight, lblHits, lblCount;
        TextBox tbAddr, tbSearch;
        ChipCtl chkErr, chkTrain, chkFold, chkExpand, chipHpd, chipLink;
        ChipCtl chkOnlyHits;
        ChipCtl[] chipCat = new ChipCtl[6];
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

        // filter state (kept when the window is built again for a theme change)
        readonly bool[] catOn = { true, true, true, true, true, true };
        bool fErr, fTrain, fFold = true, fExpand, fOnlyHits;
        string fAddr = "", fSearch = "";

        public MainWindow()
        {
            Title = "HMLOG";
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = Math.Min(set.Width, 1440); Height = Math.Min(set.Height, 900);
            MinWidth = 900; MinHeight = 520;
            if (Environment.GetEnvironmentVariable("HMLOG_DARK") == "1") set.Dark = true;   // development aid (capture scripts)
            Pal.Dark = set.Dark;
            Application.Current.RequestedThemeVariant = set.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Background = Pal.Br(Pal.Canvas);
            FontSize = 14;
            // compact controls (the Fluent defaults are tall)
            Style Compact<T>(double minH, Thickness pad) where T : Control
            {
                var st = new Style(x => x.OfType<T>());
                st.Setters.Add(new Setter(Layoutable.MinHeightProperty, minH));
                st.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, pad));
                return st;
            }
            Styles.Add(Compact<Button>(28, new Thickness(10, 2)));
            Styles.Add(Compact<ToggleButton>(28, new Thickness(9, 2)));
            Styles.Add(Compact<ComboBox>(28, new Thickness(8, 2)));
            Styles.Add(Compact<TextBox>(28, new Thickness(6, 3)));
            var tabSt = Compact<TabItem>(30, new Thickness(12, 4));
            tabSt.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty, 13.0));
            Styles.Add(tabSt);
            Content = BuildUi();
            timer.Tick += (s, e) => OnTick();
            timer.Start();
            KeyDown += OnKey;
            Closing += (s, e) => { SaveSettings(); Disconnect(); };
            Opened += (s, e) => LoadArgs();
            SizeChanged += (s, e) => UpdateHeaderMode();
        }

        void LoadArgs()
        {
            var a = Environment.GetCommandLineArgs();
            if (a.Length > 1 && File.Exists(a[1])) LoadFile(a[1]);
            // development aids (capture scripts): HMLOG_TAB = tab index, HMLOG_SEL = log row to select
            int n;
            var sz = (Environment.GetEnvironmentVariable("HMLOG_SIZE") ?? "").Split(',');
            if (sz.Length == 2) { Width = double.Parse(sz[0]); Height = double.Parse(sz[1]); }
            ClampToScreen();
            if (int.TryParse(Environment.GetEnvironmentVariable("HMLOG_SEL"), out n) && n < view.Count) { logTable.SelectedIndex = n; logTable.ScrollTo(n); }
            if (Environment.GetEnvironmentVariable("HMLOG_TOP") == "1") { Topmost = true; Activate(); }   // capture scripts: stay in front
            DemoStart();
            if (int.TryParse(Environment.GetEnvironmentVariable("HMLOG_TAB"), out n)) tabs.SelectedIndex = n;
            // HMLOG_SUB = "i,j": the sub tab of the selected tab, then the one inside it (capture scripts)
            var sub = (Environment.GetEnvironmentVariable("HMLOG_SUB") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            Control cur = (tabs.SelectedItem as TabItem)?.Content as Control;
            foreach (var sIdx in sub)
            {
                var tc = cur as TabControl;
                if (tc == null || !int.TryParse(sIdx, out n)) break;
                tc.SelectedIndex = n;
                cur = (tc.SelectedItem as TabItem)?.Content as Control;
            }
        }

        // the window never starts larger than the screen's working area (a saved size from a bigger screen)
        void ClampToScreen()
        {
            var sc = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (sc == null) return;
            double k = sc.Scaling > 0 ? sc.Scaling : 1;
            var wa = sc.WorkingArea;
            double w = wa.Width / k, h = wa.Height / k;
            // never larger than the working area (a saved size from a bigger screen or a maximized window), then inside it
            double nw = Math.Min(Math.Min(Width, w * 0.94), 1440), nh = Math.Min(Math.Min(Height, h * 0.92), 900);
            Width = nw; Height = nh;
            int x = wa.X + (int)((wa.Width - nw * k) / 2), y = wa.Y + (int)((wa.Height - nh * k) / 2);
            Position = new PixelPoint(Math.Max(wa.X, x), Math.Max(wa.Y, y));
        }

        void SaveSettings()
        {
            if (WindowState == WindowState.Normal) { set.Width = Width; set.Height = Height; }
            set.Port = cbPort.SelectedItem as string ?? set.Port;
            int b;
            set.AutoBaud = cbBaud.SelectedItem as string == "Auto";
            if (int.TryParse(cbBaud.SelectedItem as string, out b)) set.Baud = b;
            set.PcTime = cbTime.SelectedIndex == 1;
            set.Save();
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------
        // glyph: an icon in front of the text (the text goes away in a narrow window: header mode 1, 2)
        Button Btn(string text, Action click, string tip = null, string glyph = null)
        {
            var b = new Button { Padding = new Thickness(12, 4), VerticalAlignment = VerticalAlignment.Center };
            if (glyph == null) b.Content = text;
            else SetBtn(b, glyph, text);
            b.Click += (s, e) => click();
            if (tip != null) ToolTip.SetTip(b, tip ?? text);
            else if (glyph != null) ToolTip.SetTip(b, text);
            return b;
        }

        readonly List<TextBlock> hdrLabels = new List<TextBlock>();
        readonly List<Control> hdrNarrow = new List<Control>();
        Button btnMore, btnTheme, btnClearAll, hdrHpdPulse;
        Button[] hdrHpdBtn;
        Control hdrHpdGrp;
        int hdrMode = -1;
        bool hdrReg;

        void SetBtn(Button b, string glyph, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Icons.Glyph(glyph));
            if (text.Length > 0)
            {
                var lbl = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsVisible = !hdrReg || hdrMode < 1 };
                if (hdrReg) hdrLabels.Add(lbl);                     // only the header buttons lose their text in a narrow window
                sp.Children.Add(lbl);
            }
            b.Content = sp;
            b.Padding = new Thickness(text.Length > 0 ? 12 : 0, 0);
            if (text.Length == 0) { b.MinWidth = 28; b.Width = 28; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
        }



        ToggleButton Chip(string text, bool on, Action<bool> changed, string tip = null)
        {
            var c = new ToggleButton { Content = text, IsChecked = on, Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            c.IsCheckedChanged += (s, e) => changed(c.IsChecked == true);
            if (tip != null) ToolTip.SetTip(c, tip);
            return c;
        }

        TextBlock Cap(string text)
        {
            return new TextBlock { Text = text, Foreground = Pal.Br(Pal.Muted), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0), FontSize = 13 };
        }


        Control Todo(string t)
        {
            return new TextBlock { Text = t, Margin = new Thickness(20), Foreground = Pal.Br(Pal.Muted) };
        }

        DateTime msgUntil = DateTime.MinValue;

        // a message in the status bar (the counters come back after a few seconds)
        void Say(string t)
        {
            stLabel.Text = t;
            msgUntil = DateTime.Now.AddSeconds(6);
        }

        // the page of a tab (or sub tab) is on screen: only the selected page is attached to the window
        bool Visible(Control page)
        {
            return page.GetVisualRoot() != null && page.IsEffectivelyVisible;
        }

        void ToggleTheme()
        {
            set.Dark = !set.Dark;
            if (Environment.GetEnvironmentVariable("HMLOG_DARK") == "1") set.Dark = true;   // development aid (capture scripts)
            Pal.Dark = set.Dark;
            Application.Current.RequestedThemeVariant = set.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Background = Pal.Br(Pal.Canvas);
            int sel = tabs.SelectedIndex;
            Content = BuildUi();
            tabs.SelectedIndex = sel;
            set.Save();
        }

        void ResetFilters()
        {
            for (int i = 0; i < 6; i++) chipCat[i].IsChecked = true;
            chkErr.IsChecked = false; chkTrain.IsChecked = false; chkFold.IsChecked = true; chkExpand.IsChecked = false;
            tbAddr.Text = ""; tbSearch.Text = ""; chkOnlyHits.IsChecked = false;
            filterDirty = true;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (ctrl && e.Key == Key.F) { tbSearch.Focus(); tbSearch.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.F3 || (e.Key == Key.Enter && tbSearch.IsFocused)) { NextHit(shift ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.F8) { NextError(shift ? -1 : 1); e.Handled = true; }
        }

        // ------------------------------------------------------------------
        // port
        // ------------------------------------------------------------------
        void RefreshPorts()
        {
            string keep = cbPort.SelectedItem as string ?? set.Port;
            var names = Rx.Ports();
            cbPort.ItemsSource = names;
            int i = names.IndexOf(keep);
            cbPort.SelectedIndex = i >= 0 ? i : names.Count > 0 ? 0 : -1;
        }

        bool wantAuto, probing;
        int lastGoodBaud = 921600;

        async System.Threading.Tasks.Task ToggleConnect()
        {
            if (probing) return;
            if (wantPort != null) { Disconnect(); return; }
            var name = cbPort.SelectedItem as string;
            if (name == null) { Say("No serial port selected"); return; }
            int baud;
            wantAuto = cbBaud.SelectedItem as string == "Auto";
            if (wantAuto)
            {
                probing = true;
                Say("Looking for the baud rate of the board…");
                baud = await System.Threading.Tasks.Task.Run(() => Rx.Probe(name, lastGoodBaud));
                probing = false;
                if (baud == 0) baud = 921600;                    // nothing answered: the default, the log may start later
                else lastGoodBaud = baud;
            }
            else if (!int.TryParse(cbBaud.SelectedItem as string, out baud)) baud = 921600;
            string err = rx.Open(name, baud, !wantAuto);          // a fixed rate: the board is brought to it (Auto found the rate already)
            if (err != null) { Say("Cannot open " + name + ": " + err); return; }
            wantPort = name;
            ResetData();
            rawAll.Clear();
            source = name + " @ " + baud;
            if (set.AutoSave && save.Open(name)) source += "  → " + Path.GetFileName(save.Path);
            Note("connect " + name + " " + baud);
            SetBtnLabel(btnConnect, Icons.UsbOff, "Disconnect");
        }

        void Disconnect()
        {
            wantPort = null;
            rx.Close();
            save.Close();
            if (btnConnect != null) SetBtnLabel(btnConnect, Icons.Usb, "Connect");
        }

        // PC note in the lines (kept in the saved file)
        void Note(string text)
        {
            rx.Q.Enqueue(new RxLine(DateTime.Now, "# " + text));
        }

        async void ReprobeBaud()
        {
            probing = true;
            string name = wantPort;
            int old = rx.Baud;
            rx.Close();
            int baud = await System.Threading.Tasks.Task.Run(() => Rx.Probe(name, lastGoodBaud));
            if (baud == 0) baud = old;
            else lastGoodBaud = baud;
            if (wantPort != null && rx.Open(name, baud) == null && baud != old) Note("baud " + baud);
            probing = false;
        }

        // once a second: the port is gone (adapter unplugged) -> reconnect
        void CheckPort()
        {
            if (wantPort == null || probing) return;
            // baud Auto: a board that was reset is back at 921600 (the log stops): look for the rate again
            if (wantAuto && rx.IsOpen && (DateTime.Now - rx.LastRx).TotalSeconds > 4 && (DateTime.Now - rx.OpenedAt).TotalSeconds > 4)
            {
                ReprobeBaud();
                return;
            }
            if (rx.IsOpen && !rx.Gone()) return;
            if (rx.IsOpen) { rx.Close(); Note("port lost"); }
            if (!set.Reconnect) { Disconnect(); return; }
            if (Array.IndexOf(System.IO.Ports.SerialPort.GetPortNames(), wantPort) < 0) return;
            if (rx.Open(wantPort, rx.Baud > 0 ? rx.Baud : set.Baud) == null) Note("reconnected " + wantPort);
        }

        // ------------------------------------------------------------------
        // data
        // ------------------------------------------------------------------
        void ResetData(bool keepCards = false)
        {
            var old = dec;
            dec = new Decoder();
            if (keepCards && old != null) dec.CarryFrom(old);
            raw.Clear();
            rawShown = 0;
            rawTable.SetRows(new List<TRow>());
            viewVer = trainVer = hdcpVer = regsVer = statsVer = edidVer = stateVer = sessVer = -1;
            filterDirty = true;
            detailGroup = null;
            recentEv.Clear();
        }

        void ClearAll()
        {
            ResetData();
            rawAll.Clear();
            hpdResets = linkResets = 0;
            Interlocked_Reset();
            if (wantPort == null) source = "";
        }

        void Interlocked_Reset()
        {
            rx.UartErr = 0; rx.Partial = 0; rx.Bytes = 0;
        }

        // PC notes and FPGA status lines: no transfer (kept by the resets)
        static bool IsMeta(string l)
        {
            return l.StartsWith("#") || (l.Length > 10 && l[8] == ' ' && l[9] == 'S' && l[10] == ' ');
        }

        // "tttttttt E cc": the event code, else -1
        static int EventCode(string l)
        {
            string[] f = l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int c;
            if (f.Length == 3 && f[0].Length == 8 && f[1] == "E" && int.TryParse(f[2], NumberStyles.HexNumber, null, out c)) return c;
            return -1;
        }

        void LoadFile(string path)
        {
            Disconnect();
            ResetData();
            rawAll.Clear();
            try
            {
                foreach (string l in File.ReadLines(path, Latin1)) { raw.Add(l); rawAll.Add(l); dec.Feed(l); }
                dec.Finish();
            }
            catch (Exception ex) { Say("Cannot read the file: " + ex.Message); return; }
            source = Path.GetFileName(path);
            Title = "HMLOG - " + source;
            waveFitNext = true;
            OnTick();
        }

        async System.Threading.Tasks.Task OpenLog()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = "Open log" });
            if (files.Count > 0) LoadFile(files[0].Path.LocalPath);
        }

        async System.Threading.Tasks.Task SaveDecoded()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "decoded.txt", Title = "Save decoded" });
            if (f == null) return;
            try { File.WriteAllLines(f.Path.LocalPath, dec.Lines); Say("Saved " + f.Name); }
            catch (Exception ex) { Say("Cannot save: " + ex.Message); }
        }

        async System.Threading.Tasks.Task SaveRaw()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "raw.log", Title = "Save raw" });
            if (f == null) return;
            try { File.WriteAllLines(f.Path.LocalPath, rawAll, Latin1); Say("Saved " + f.Name); }
            catch (Exception ex) { Say("Cannot save: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // tick
        // ------------------------------------------------------------------
        void OnTick()
        {
            RxLine l;
            int n = 0;
            while (n < 20000 && rx.Q.TryDequeue(out l))
            {
                rawAll.Add(l.L);                  // saved first: the file has every line even if decoding fails
                save.Write(l.L);
                int ev = EventCode(l.L);
                bool hpdUp = set.HpdReset && ev == 0x02, linkUp = set.LinkReset && ev == 0x06;
                if ((hpdUp || linkUp) && dec.Groups.Count > recentEv.Count)
                {
                    var keep = new List<RxLine>(recentEv);
                    ResetData(true);
                    foreach (var k in keep) { raw.Add(k.L); dec.FeedPc = k.T; dec.Feed(k.L); }
                    recentEv.AddRange(keep);
                    if (hpdUp) hpdResets++; else linkResets++;
                }
                if (ev >= 0 || IsMeta(l.L)) recentEv.Add(l); else recentEv.Clear();
                raw.Add(l.L);
                dec.FeedPc = l.T;
                dec.Feed(l.L);
                n++;
            }
            if (n > 0) save.Flush();
            if (++tickNo % 10 == 0) CheckPort();              // once a second

            if (dec.Version != viewVer || filterDirty) RebuildView();
            UpdatePanels();
            DemoTick();
            UpdateHealth();
            UpdateBoard();
            UpdateCec();
            if (Visible(trainTable) && dec.Version != trainVer) RebuildTrain();
            if (Visible(hdcpTable) && dec.Version != hdcpVer) RebuildHdcp();
            if (Visible(regsTable) && dec.Version != regsVer) RebuildRegs();
            if (Visible(statsTable) && dec.Version != statsVer) RebuildStats();
            if (Visible(errList) && dec.Version != errVer) RebuildErr();
            if (Visible(tbEdid) && dec.EdidVersion != edidVer) RebuildEdid();
            if (Visible(rawTable)) AppendRaw();

            var st = dec.St;
            int fifo = Count(st.Events, 0xFF);
            Action<TextBlock, string, bool> setv = (tb, v, bad) => { if (tb.Text != v) tb.Text = v; tb.Foreground = Pal.Br(bad ? Pal.Red : Pal.Text); };
            setv(stRaw, raw.Count.ToString("N0"), false);
            setv(stTx, (st.Reads + st.Writes + st.I2c).ToString("N0"), false);
            setv(stNack, st.Nack.ToString("N0"), st.Nack > 0);
            setv(stCut, st.Cut.ToString("N0"), st.Cut > 0);
            setv(stFifo, fifo.ToString("N0"), fifo > 0);
            if (DateTime.Now > msgUntil) { string t = source.Length > 0 ? source : "no input"; if (stLabel.Text != t) stLabel.Text = t; }
            string conn = wantPort == null ? "Not connected" : rx.IsOpen ? "● connected" : "○ waiting for " + wantPort;
            if (lblConn.Text != conn) lblConn.Text = conn;
            lblConn.Foreground = Pal.Br(wantPort != null && rx.IsOpen ? Pal.Good : Pal.Muted);
            var extra = new List<string>();
            if (st.Lost > 0) extra.Add(string.Format("lost bytes {0}", st.Lost));
            if (st.FifoLost > 0) extra.Add(string.Format("FPGA records lost {0}", st.FifoLost));
            if (rx.UartErr > 0) extra.Add(string.Format("UART errors {0}", rx.UartErr));
            if (rx.Partial > 0) extra.Add(string.Format("cut at connect {0}", rx.Partial));
            if (st.DecodeErr > 0) extra.Add(string.Format("decode errors {0}", st.DecodeErr));
            if (hpdResets + linkResets > 0) extra.Add(string.Format("resets {0} (Hotplug {1}, OPTICAL {2})", hpdResets + linkResets, hpdResets, linkResets));
            if (wantPort != null) extra.Add(string.Format("received {0:N0} bytes", System.Threading.Interlocked.Read(ref rx.Bytes)));
            string r = string.Join(" · ", extra);
            if (stRight.Text != r) stRight.Text = r;
            bool anyFilter = fErr || fTrain;
            foreach (bool c in catOn) if (!c) anyFilter = true;
            if (btnViewReset.IsVisible != anyFilter) btnViewReset.IsVisible = anyFilter;
            string title = source.Length > 0 ? "HMLOG - " + source : "HMLOG";
            if (Title != title) Title = title;
        }
    }
}
