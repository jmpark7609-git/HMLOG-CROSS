// Window layout: header in three rows (Port / View / Find: a caption, the bar, the buttons on the right), status panel
// (TARGET, LINK, SINK, SESSION), boxed main tabs, status bar. Same structure as MainForm.cs of the Windows version, with
// quieter controls: one accent color, joined toggle groups, a settings menu for the rarely used switches.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System;
using System.Collections.Generic;

namespace HMLOG.App
{
    partial class MainWindow
    {
        TextBlock lblConn, stShown;
        TextBlock stRaw, stTx, stNack, stCut, stFifo;
        StackPanel stItems;
        Button btnViewReset;
        TextBlock hdrHpdLbl;

        void UpdateHeaderMode() { }

        static Control Sep()
        {
            return new Border { Width = 1, Height = 22, Background = Pal.Br(Pal.Line), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }

        ChipCtl HChip(string text, Color? dot, bool on, Action<bool> changed, string tip)
        {
            var c = new ChipCtl(text, dot);
            c.IsChecked = on;
            c.Changed += changed;
            if (tip != null) ToolTip.SetTip(c, tip);
            return c;
        }

        // a short caption in front of a group
        TextBlock Cap2(string t)
        {
            return new TextBlock { Text = t, Foreground = Pal.Br(Pal.Faint), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }

        // the settings menu: Reconnect and Auto save (toggles) and the theme
        ContextMenu BuildSettingsMenu()
        {
            var m = new ContextMenu();
            MenuItem Toggle(string text, Func<bool> get, Action<bool> setv)
            {
                var mi = new MenuItem { Header = (get() ? "✓  " : "     ") + text };
                mi.Click += (s, e) => { setv(!get()); mi.Header = (get() ? "✓  " : "     ") + text; };
                return mi;
            }
            m.Items.Add(Toggle("Reconnect when the port comes back", () => set.Reconnect, v => set.Reconnect = v));
            m.Items.Add(Toggle("Auto save the raw lines", () => set.AutoSave, v => set.AutoSave = v));
            var th = new MenuItem { Header = set.Dark ? "Light theme" : "Dark theme" };
            th.Click += (s, e) => ToggleTheme();
            m.Items.Add(new Separator());
            m.Items.Add(th);
            return m;
        }

        // a header row: caption column, the bar that takes the width, buttons on the right
        void HeaderRow(Grid g, int row, string caption, string tip, Panel left, Control right)
        {
            var ic = new TextBlock { Text = caption, Foreground = Pal.Br(Pal.Faint), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(ic, tip);
            Grid.SetRow(ic, row);
            Grid.SetRow(left, row); Grid.SetColumn(left, 1);
            g.Children.Add(ic); g.Children.Add(left);
            if (right != null) { Grid.SetRow(right, row); Grid.SetColumn(right, 2); g.Children.Add(right); }
        }

        static WrapPanel Bar()
        {
            return new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        }

        void AddTo(Panel p, Control c, double right = 6)
        {
            c.Margin = new Thickness(c.Margin.Left, 3, right, 3);
            p.Children.Add(c);
        }

        // value text of a status bar item: icon, caption, value
        StackPanel StatItem(string glyph, string caption, out TextBlock value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Icons.Glyph(glyph, 17); ic.Foreground = Pal.Br(Pal.Faint); ic.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(ic);
            if (caption.Length > 0) sp.Children.Add(new TextBlock { Text = caption, Foreground = Pal.Br(Pal.Muted), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            value = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
            sp.Children.Add(value);
            return sp;
        }

        // a status bar cell: the item with a divider on its right
        Control StatCell(Control item, bool last = false)
        {
            return new Border { Child = item, Padding = new Thickness(12, 6), BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(0, 0, last ? 0 : 1, 0) };
        }

        Control BuildUi()
        {
            var root = new DockPanel();
            hdrReg = false;
            hdrLabels.Clear(); hdrNarrow.Clear();
            AddTabStyles();

            // ================= header =================
            cbPort = new ComboBox { Width = 220, VerticalAlignment = VerticalAlignment.Center };
            cbBaud = new ComboBox { Width = 100, ItemsSource = new[] { "Auto", "3000000", "2000000", "921600" }, VerticalAlignment = VerticalAlignment.Center };
            cbBaud.SelectedItem = set.AutoBaud ? "Auto" : set.Baud.ToString();
            if (cbBaud.SelectedIndex < 0) cbBaud.SelectedIndex = 3;
            ToolTip.SetTip(cbBaud, "Baud rate: Auto finds it; 921600 / 2000000 / 3000000 are the rates the board follows");
            // the Time choice is shown as two toggles (row 3); cbTime is the value the rest of the window reads
            cbTime = new ComboBox { ItemsSource = new[] { "FPGA time", "PC time" }, SelectedIndex = set.PcTime ? 1 : 0 };
            cbTime.SelectionChanged += (s, e) => { filterDirty = true; stateVer = -1; };
            RefreshPorts();

            // ---- row 1 (Port): connection | files and the settings menu
            var bar1 = Bar();
            AddTo(bar1, cbPort);
            AddTo(bar1, Btn("", RefreshPorts, "Refresh the port list", Icons.Refresh));
            AddTo(bar1, cbBaud);
            btnConnect = Btn("Connect", async () => await ToggleConnect(), "Open the selected port (Auto looks for the rate of the board: 3 M / 2 M / 921600)", Icons.Usb);
            btnConnect.MinWidth = 100;
            btnConnect.Background = Pal.Br(Pal.Accent);
            btnConnect.BorderBrush = Pal.Br(Pal.Accent);
            btnConnect.Foreground = Brushes.White;
            AddTo(bar1, btnConnect);
            lblConn = new TextBlock { Text = "Not connected", Foreground = Pal.Br(Pal.Muted), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            AddTo(bar1, lblConn, 4);

            var files = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            files.Children.Add(Btn("", async () => await OpenLog(), "Open a log file (a raw log saved before)", Icons.Open));
            files.Children.Add(Btn("", async () => await SaveRaw(), "Save the raw log: every line received and the PC notes", Icons.Save));
            files.Children.Add(Btn("", async () => await SaveDecoded(), "Save the decoded log: the lines as shown in the Log tab", Icons.Export));
            btnClearAll = Btn("", ClearAll, "Clear the view (the auto-saved file keeps the lines)", Icons.Delete);
            files.Children.Add(btnClearAll);
            btnTheme = Btn("", () => { }, "Settings: Reconnect, Auto save, dark / light", Icons.Refresh);
            btnTheme.Content = new TextBlock { Text = "⋯", FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            btnTheme.ContextMenu = BuildSettingsMenu();
            btnTheme.Click += (s, e) => btnTheme.ContextMenu.Open(btnTheme);
            files.Children.Add(btnTheme);
            foreach (Control c in files.Children) if (c is Button) c.Margin = new Thickness(4, 0, 0, 0);

            // ---- rows 2 and 3 (View, Find) share their columns: caption | item | label, item | label, item | label, item.
            // Every column is as wide as its widest cell, and the item of a column fills that width in both rows.
            string[] cn = { "SCDC", "HDCP", "EDID", "I2C", "Event", "CEC" };
            Color[] cd = { Pal.ScdcText, Pal.HdcpText, Pal.EdidText, Pal.I2cText, Pal.EvtText, Pal.CecText };
            string[] ct = { null, null, null, "Devices other than SCDC, HDCP, EDID (DDC/CI 0x37 etc.)", "HPD, HDMI 5V, OPTICAL, log start, unknown lines", null };
            for (int i = 0; i < 6; i++)
            {
                int k = i;
                chipCat[i] = HChip(cn[i], cd[i], catOn[i], v => { catOn[k] = v; filterDirty = true; }, ct[i] ?? "Show " + cn[i] + " rows: lit = listed, dim = hidden (the log keeps them)");
            }
            chkErr = HChip("Errors", null, fErr, v => { fErr = v; filterDirty = true; }, "Only NACK, cut-off, failures and error events");
            chkTrain = HChip("Training", null, fTrain, v => { fTrain = v; filterDirty = true; }, "Only transactions with FRL training registers (SCDC 0x10, 0x31, 0x40-0x42)");
            chkFold = HChip("Fold", null, fFold, v => { fFold = v; filterDirty = true; }, "Identical transactions in a row are shown once, with a count");
            chkExpand = HChip("Expand", null, fExpand, v => { fExpand = v; filterDirty = true; }, "Every register of a transaction as its own line");
            chipHpd = HChip("HPD", null, set.HpdReset, v => set.HpdReset = v,
                            "Clear the view when the sink HPD goes HIGH again and show it again from the event just before. Only for lines received while connected");
            chipLink = HChip("OPTICAL", null, set.LinkReset, v => set.LinkReset = v,
                             "Clear the view when OPTICAL goes UP again and show it again from the event just before. Only for lines received while connected");
            btnViewReset = Btn("Reset filters", ResetFilters, "Show everything again (shown only while a filter is on)");
            btnViewReset.IsVisible = false;

            lblHits = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Pal.Br(Pal.Faint), Margin = new Thickness(0, 0, 8, 0), FontSize = 12.5 };
            var sIcon = Icons.Glyph(Icons.Find, 17); sIcon.Foreground = Pal.Br(Pal.Faint); sIcon.Margin = new Thickness(9, 0, 0, 0);
            tbSearch = new TextBox { MinWidth = 200, Text = fSearch, Watermark = "Search name, value, text", VerticalAlignment = VerticalAlignment.Center, InnerLeftContent = sIcon, InnerRightContent = lblHits };
            tbSearch.TextChanged += (s, e) => { fSearch = tbSearch.Text ?? ""; SearchChanged(); };
            ToolTip.SetTip(tbSearch, "Find in names, values and text (case-insensitive). Enter next, Shift+Enter previous, F3, Ctrl+F");
            var bPrev = Btn("", () => NextHit(-1), "Previous (Shift+Enter, Shift+F3)", Icons.Up);
            var bNext = Btn("", () => NextHit(1), "Next (Enter, F3)", Icons.Down);
            bPrev.Margin = new Thickness(4, 0, 0, 0); bNext.Margin = new Thickness(4, 0, 0, 0);
            var searchCell = new DockPanel();
            DockPanel.SetDock(bNext, Dock.Right); DockPanel.SetDock(bPrev, Dock.Right);
            searchCell.Children.Add(bNext); searchCell.Children.Add(bPrev); searchCell.Children.Add(tbSearch);
            chkOnlyHits = HChip("Matches", null, fOnlyHits, v => { fOnlyHits = v; searchQ = null; SearchChanged(); filterDirty = true; }, "Only the rows that match the search");
            var matchesGrp = new ToggleGroup(chkOnlyHits) { Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(matchesGrp, Dock.Right);
            searchCell.Children.Insert(0, matchesGrp);
            tbAddr = new TextBox { MinWidth = 120, Text = fAddr, Watermark = "54, 54:40-42", VerticalAlignment = VerticalAlignment.Center };
            tbAddr.TextChanged += (s, e) => { fAddr = tbAddr.Text ?? ""; filterDirty = true; };
            var tFpga = HChip("FPGA", null, !set.PcTime, v => { }, "Time stamps of the FPGA (since the log start)");
            var tPc = HChip("PC", null, set.PcTime, v => { }, "Time of day of the PC when the line arrived");
            tFpga.Changed += v => { if (v) { tPc.IsChecked = false; cbTime.SelectedIndex = 0; } else if (tPc.IsChecked != true) tFpga.IsChecked = true; };
            tPc.Changed += v => { if (v) { tFpga.IsChecked = false; cbTime.SelectedIndex = 1; } else if (tFpga.IsChecked != true) tPc.IsChecked = true; };
            // HPD out of the TX (HDMI_HPD_OUT, B12): Auto / Low / High and a pulse; only for a board that has it (UpdatePsRegs)
            var hseg = SegButtons(new[] { "Auto", "Low", "High" }, k => PrHpdSet(k), out hdrHpdBtn);
            string[] hkt = { "HDMI_HPD_OUT is the sink HPD, as built", "Force HPD low: the source sees no sink", "Force HPD high: the source sees a sink" };
            for (int i = 0; i < 3; i++) ToolTip.SetTip(hdrHpdBtn[i], hkt[i] + " (the same as the RETIMER tab)");
            hdrHpdPulse = Btn("Pulse", PrHpdPulse, "HPD low for the time set in the RETIMER tab, then back to Auto");
            hdrHpdPulse.Margin = new Thickness(6, 0, 0, 0);
            var hpdDock = new DockPanel { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            hdrHpdGrp = hpdDock;
            DockPanel.SetDock(hdrHpdPulse, Dock.Right);
            hpdDock.Children.Add(hdrHpdPulse); hpdDock.Children.Add(hseg);
            hdrHpdLbl = Cap2("HPD out"); hdrHpdLbl.IsVisible = false;

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition(54, GridUnitType.Pixel));          // caption
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));  // label
                cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // item
            }
            cols.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            cols.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            cols.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Put(Control c, int row, int col, Thickness? m = null)
            {
                Grid.SetRow(c, row); Grid.SetColumn(c, col);
                if (c is TextBlock) c.Margin = new Thickness(16, 0, 8, 0);
                if (m.HasValue) { c.Margin = m.Value; c.HorizontalAlignment = HorizontalAlignment.Left; }
                if (row == 1) c.Margin = new Thickness(c.Margin.Left, 8, c.Margin.Right, 0);
                c.VerticalAlignment = VerticalAlignment.Center;
                cols.Children.Add(c);
            }
            Control Fill(Control c) { c.HorizontalAlignment = HorizontalAlignment.Stretch; return c; }
            Put(Cap2("View"), 0, 0); Put(Cap2("Find"), 1, 0);
            Put(Fill(new ToggleGroup(chipCat)), 0, 1);       Put(Fill(searchCell), 1, 1);
            Put(Cap2("Only"), 0, 2);                         Put(Cap2("Address"), 1, 2);
            Put(Fill(new ToggleGroup(chkErr, chkTrain)), 0, 3); Put(Fill(tbAddr), 1, 3);
            Put(Cap2("Rows"), 0, 4);                         Put(Cap2("Time"), 1, 4);
            Put(Fill(new ToggleGroup(chkFold, chkExpand)), 0, 5); Put(Fill(new ToggleGroup(tFpga, tPc)), 1, 5);
            Put(Cap2("Clear on"), 0, 6);                     Put(hdrHpdLbl, 1, 6);
            Put(Fill(new ToggleGroup(chipHpd, chipLink)), 0, 7); Put(Fill(hdrHpdGrp), 1, 7);
            Put(btnViewReset, 0, 8, new Thickness(12, 0, 0, 0));

            var header = new StackPanel { Margin = new Thickness(8, 7, 8, 7), Spacing = 8 };
            var portRow = new Grid();
            portRow.ColumnDefinitions.Add(new ColumnDefinition(54, GridUnitType.Pixel));
            portRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            portRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderRow(portRow, 0, "Port", "Port: the serial port, its baud rate and the files", bar1, files);
            header.Children.Add(portRow);
            header.Children.Add(cols);
            var hb = new Border { Child = header, Background = Pal.Br(Pal.Surface), BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(hb, Dock.Top);
            root.Children.Add(hb);

            // ================= status bar =================
            lblCount = stShown = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
            stItems = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock stFile;
            stItems.Children.Add(StatCell(StatItem(Icons.Doc, "", out stFile))); stLabel = stFile; stFile.FontWeight = FontWeight.Normal; stFile.Text = "no input";
            stItems.Children.Add(StatCell(StatItem(Icons.Rows, "Raw", out stRaw)));
            stItems.Children.Add(StatCell(StatItem(Icons.Port, "Transactions", out stTx)));
            stItems.Children.Add(ErrClick(StatCell(StatItem(Icons.Warn, "NACK", out stNack))));
            stItems.Children.Add(ErrClick(StatCell(StatItem(Icons.Warn, "Cut off", out stCut))));
            stItems.Children.Add(StatCell(StatItem(Icons.Warn, "FIFO overflow", out stFifo), true));
            foreach (var t in new[] { stRaw, stTx, stNack, stCut, stFifo }) t.MinWidth = 36;
            stRight = new TextBlock { Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center, Foreground = Pal.Br(Pal.Muted), HorizontalAlignment = HorizontalAlignment.Right };
            var shown = StatItem(Icons.Filter, "Shown", out var stShownDummy);
            shown.Children.RemoveAt(shown.Children.Count - 1);
            shown.Children.Add(stShown);
            shown.Margin = new Thickness(0, 0, 14, 0);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(shown); right.Children.Add(stRight);
            // two columns: the counters (clipped, never over the right part) and the right part (shown count, notes)
            stRight.MaxWidth = 380; stRight.TextTrimming = TextTrimming.CharacterEllipsis;
            stItems.ClipToBounds = true;
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            sg.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var leftHost = new Border { Child = stItems, ClipToBounds = true };
            Grid.SetColumn(right, 1);
            sg.Children.Add(leftHost);
            sg.Children.Add(right);
            var sb = new Border { Child = sg, Background = Pal.Br(Pal.Surface), BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(sb, Dock.Bottom);
            root.Children.Add(sb);

            // ================= status panel and tabs =================
            var state = BuildStatePanel();
            DockPanel.SetDock(state, Dock.Left);
            root.Children.Add(state);

            tabs = new TabControl { Margin = new Thickness(0, 8, 8, 8) };
            tabHdcp = MainTab("HDCP", BuildHdcpTab());
            tabEdid = MainTab("EDID", BuildEdidTab());
            var scdcRegs = new TabItem { Header = "REGISTERS", Content = BuildRegsTab() };
            tabScdcTrain = new TabItem { Header = "FRL TRAINING (0)", Content = BuildTrainTab() };
            var scdc = new TabControl { Items = { scdcRegs, tabScdcTrain } };
            var dataStats = new TabItem { Header = "STATISTICS", Content = BuildStatsTab() };
            var dataRaw = new TabItem { Header = "RAW", Content = BuildRawTab() };
            tabErrors = new TabItem { Header = "ERRORS", Content = BuildErrTab() };
            dataTabs = new TabControl { Items = { dataStats, tabErrors, dataRaw } };
            tabData = MainTab("DATA", dataTabs);
            tabs.Items.Add(MainTab("LOG", BuildLogTab()));
            tabs.Items.Add(MainTab("SCDC", scdc));
            tabs.Items.Add(tabHdcp);
            tabs.Items.Add(tabEdid);
            tabs.Items.Add(MainTab("CEC", BuildCecTab()));
            tabs.Items.Add(tabData);
            tabs.Items.Add(MainTab("BOARD", BuildBoardTab()));
            root.Children.Add(tabs);

            // everything is shown again from the decoder (a new window after a theme change keeps the data)
            stateVer = sessVer = trainVer = hdcpVer = regsVer = statsVer = edidVer = viewVer = -1;
            builtDec = null;
            filterDirty = true;
            rawShown = 0;
            return root;
        }

        TabItem MainTab(string header, Control content)
        {
            var ti = new TabItem { Header = header, Content = content };
            ti.Classes.Add("mainTab");
            return ti;
        }

        readonly List<Style> tabStyles = new List<Style>();

        // boxed main tabs: the selected one is lifted (surface color) with a 2 px accent line on top; the others sit back
        void AddTabStyles()
        {
            foreach (var o in tabStyles) Styles.Remove(o);
            tabStyles.Clear();
            void Add(Style s) { Styles.Add(s); tabStyles.Add(s); }
            var s0 = new Style(x => x.OfType<TabItem>().Class("mainTab"));
            s0.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(20, 6)));
            s0.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 14.0));
            s0.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6, 6, 0, 0)));
            s0.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0, 0, 2, 0)));
            s0.Setters.Add(new Setter(Layoutable.MinHeightProperty, 34.0));
            Add(s0);
            var s1 = new Style(x => x.OfType<TabItem>().Class("mainTab").Template().OfType<Border>().Name("PART_LayoutRoot"));
            s1.Setters.Add(new Setter(Border.BackgroundProperty, Pal.Br(Pal.Header)));
            s1.Setters.Add(new Setter(Border.BorderBrushProperty, Pal.Br(Pal.Line)));
            s1.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0)));
            Add(s1);
            var s2 = new Style(x => x.OfType<TabItem>().Class("mainTab").Class(":selected").Template().OfType<Border>().Name("PART_LayoutRoot"));
            s2.Setters.Add(new Setter(Border.BackgroundProperty, Pal.Br(Pal.Surface)));
            s2.Setters.Add(new Setter(Border.BorderBrushProperty, Pal.Br(Pal.Accent)));
            s2.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1, 3, 1, 0)));
            Add(s2);
            var s5 = new Style(x => x.OfType<TabItem>().Class("mainTab").Not(y => y.Class(":selected")).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
            s5.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, Pal.Br(Pal.Muted)));
            Add(s5);
            var s3 = new Style(x => x.OfType<TabItem>().Class("mainTab").Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
            s3.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, Pal.Br(Pal.Text)));
            s3.Setters.Add(new Setter(ContentPresenter.FontWeightProperty, FontWeight.SemiBold));
            Add(s3);
            var s4 = new Style(x => x.OfType<TabItem>().Class("mainTab").Template().OfType<Avalonia.Controls.Shapes.Rectangle>().Name("PART_SelectedPipe"));
            s4.Setters.Add(new Setter(Visual.IsVisibleProperty, false));
            Add(s4);
        }
    }
}
