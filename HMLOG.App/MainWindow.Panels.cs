// Status panel (live state and sessions), FRL training, HDCP, registers, statistics, EDID and raw tabs.
// Port of MainForm.Panels.cs / the Rebuild* methods of MainForm.cs of the Windows version.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HMLOG.App
{
    partial class MainWindow
    {
        // ------------------------------------------------------------------
        // status panel (left)
        // ------------------------------------------------------------------
        static readonly string[] StNames = { "OPTICAL", "HDMI 5V", "HDMI HPD", "Mode", "FFE level", "Training", "CED", "HDCP", "EDID", "Board", "Status", "Lost", "Link err" };
        const int StFpga = 9;
        TextBlock[] stVal;
        Table sessTable;
        int stateVer = -1, sessVer = -1, trainVer = -1, hdcpVer = -1, regsVer = -1, statsVer = -1, edidVer = -1;
        Table trainTable, hdcpTable, regsTable, statsTable, rawTable;
        TextBox tbEdid;
        int rawShown;

        Control Card(string title, Control body)
        {
            var head = new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 13, Foreground = Pal.Br(Pal.CecText), Margin = new Thickness(14, 10, 0, 6) };
            var dp = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            dp.Children.Add(head);
            dp.Children.Add(body);
            return new Border { Child = dp, Background = Pal.Br(Pal.Surface), BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true };
        }

        Control StateRows(params int[] idx)
        {
            var g = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition(96, GridUnitType.Pixel));
            g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (int r = 0; r < idx.Length; r++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var line = new Border { BorderThickness = new Thickness(0, 0, 0, r < idx.Length - 1 ? 1 : 0), BorderBrush = Pal.Br(Pal.Line) };
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, 2);
                g.Children.Add(line);
                var n = new TextBlock { Text = StNames[idx[r]], Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 5, 0, 5), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(n, r);
                Grid.SetRow(stVal[idx[r]], r);
                Grid.SetColumn(stVal[idx[r]], 1);
                g.Children.Add(n);
                g.Children.Add(stVal[idx[r]]);
            }
            return g;
        }

        // the values, the session list and the cards TARGET, LINK, SINK, SESSION
        Control BuildStatePanel()
        {
            stVal = new TextBlock[StNames.Length];
            for (int i = 0; i < stVal.Length; i++)
                stVal[i] = new TextBlock { Text = "-", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(0, 5, 0, 5), TextWrapping = TextWrapping.Wrap, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
            sessTable = new Table("#", 34, "Time", 76, "Result", 200);
            sessTable.SelectionChanged += () =>
            {
                var t = sessTable.Selected;
                if (t != null && t.Tag is Session s) GoToGroup(s.GroupId);
            };
            sessTable.Height = 220;

            var sp = new StackPanel { Margin = new Thickness(8, 8, 2, 8) };
            sp.Children.Add(Card("TARGET", StateRows(9, 10, 11, 12)));
            sp.Children.Add(Card("LINK", StateRows(0, 1, 2, 3, 4)));
            sp.Children.Add(Card("SINK", StateRows(5, 6, 7, 8)));
            sp.Children.Add(Card("SESSION", sessTable));
            return new ScrollViewer { Content = sp, Width = 330, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }

        void SetState(int i, string t, Color c)
        {
            if (stVal[i].Text != t) stVal[i].Text = t;
            stVal[i].Foreground = Pal.Br(c);
        }

        static string OnOff(int v, string on, string off)
        {
            return v < 0 ? "-" : v == 1 ? on : off;
        }

        int RegValue(int dev, int off)
        {
            RegState rs;
            return dec.Regs.TryGetValue((dev << 8) | off, out rs) ? rs.Value : -1;
        }

        static string HdcpResultText(HdcpAttempt h)
        {
            if (h.Result == 1) return "authenticated" + (h.End.Length > 0 ? " · then " + h.End : "");
            if (h.Result == 0) return "failed" + (h.End.Length > 0 ? " (" + h.End + ")" : "");
            return "in progress";
        }

        static string SessionText(Session s, out Color? c)
        {
            var p = new List<string>();
            bool bad = false, good = false;
            if (s.OkRate >= 0) { p.Add("FRL " + Decoder.Rate(s.OkRate) + " OK"); good = true; }
            else if (s.TrainFail > 0) { p.Add("FRL failed " + s.TrainFail); bad = true; }
            else if (s.Tmds) { p.Add(s.Scramble ? "TMDS scramble" : "TMDS"); good = true; }
            if (s.OkRate >= 0 && s.TrainFail > 0) p.Add("failed " + s.TrainFail);
            if (s.Hdcp != null)
            {
                p.Add("HDCP " + s.Hdcp.Ver + (s.Hdcp.Result == 1 ? " OK" : s.Hdcp.Result == 0 ? " failed" : " …"));
                if (s.Hdcp.Result == 0) bad = true;
            }
            if (p.Count == 0) p.Add(s.Xfers > 0 ? "DDC " + s.Xfers : "-");
            c = bad ? Lc(200, 30, 30) : good ? Lc(20, 130, 50) : (Color?)null;
            return string.Join(" · ", p);
        }

        string SessTime(Session s)
        {
            if (cbTime.SelectedIndex == 1 && s.Pc != DateTime.MinValue) return s.Pc.ToString("HH:mm:ss");
            return s.Us < 0 ? "" : string.Format("{0}.{1:D3}", s.Us / 1000000, s.Us % 1000000 / 1000);
        }

        void UpdatePanels()
        {
            if (dec.Version == stateVer) return;
            stateVer = dec.Version;
            var gray = Pal.Muted;
            SetState(2, OnOff(dec.Hpd, "HIGH", "LOW"), dec.Hpd < 0 ? gray : dec.Hpd == 1 ? Pal.Good : Pal.Red);
            SetState(1, OnOff(dec.Power, "ON", "OFF"), dec.Power < 0 ? gray : dec.Power == 1 ? Pal.Good : Pal.Red);
            SetState(0, OnOff(dec.Link, "UP", "DOWN"), dec.Link < 0 ? gray : dec.Link == 1 ? Pal.Good : Pal.Red);

            int c1 = RegValue(Decoder.DevScdc, 0x31), tm = RegValue(Decoder.DevScdc, 0x20);
            if (c1 > 0 && (c1 & 0xF) != 0)
            {
                SetState(3, "FRL " + Decoder.Rate(c1 & 0xF), Pal.Text);
                SetState(4, (c1 >> 4).ToString(), Pal.Text);
            }
            else
            {
                SetState(3, tm >= 0 ? "TMDS" + ((tm & 1) != 0 ? " (scramble, 1/40)" : "") : "-", tm >= 0 ? Pal.Text : gray);
                SetState(4, "-", gray);
            }

            var cd = dec.Trainings.Count == 0 && dec.Prev != null ? dec.Prev : dec;   // after a reset the cards keep the last
            var tr = cd.Trainings;
            if (tr.Count <= cd.TrainShow) SetState(5, "-", gray);
            else
            {
                var last = tr[tr.Count - 1];
                SetState(5, string.Format("{0}{1}", last.Result == 1 ? "OK" : last.Result == 0 ? "failed" : "",
                                          last.Result < 0 && last.End.Length == 0 ? "in progress" : last.Result < 0 ? "-" : ""),
                         last.Result == 1 ? Pal.Good : last.Result == 0 ? Pal.Red : Pal.Warn);
            }

            // CED: lanes 0..3 with the valid bit, the count of the last read
            var ced = new List<string>();
            int[] lo = { 0x50, 0x52, 0x54, 0x57 };
            bool anyCed = false, errCed = false;
            foreach (int a in lo)
            {
                int l = RegValue(Decoder.DevScdc, a), h = RegValue(Decoder.DevScdc, a + 1);
                if (l < 0 || h < 0) { ced.Add("-"); continue; }
                anyCed = true;
                int n = ((h & 0x7F) << 8) | l;
                if ((h & 0x80) != 0 && n > 0) errCed = true;
                ced.Add((h & 0x80) != 0 ? n.ToString() : "-");
            }
            SetState(6, anyCed ? string.Join(" / ", ced) : "-", !anyCed ? gray : errCed ? Pal.Red : Pal.Good);

            var hd = dec.HdcpTries.Count == 0 && dec.Prev != null ? dec.Prev : dec;
            if (hd.HdcpTries.Count <= hd.HdcpShow)
                SetState(7, dec.Hdcp2Cap < 0 ? "-" : dec.Hdcp2Cap == 1 ? "2.2 supported" : "2.2 not supported",
                         dec.Hdcp2Cap < 0 ? gray : dec.Hdcp2Cap == 1 ? Pal.Good : Pal.Warn);
            else
            {
                var h = hd.HdcpTries[hd.HdcpTries.Count - 1];
                SetState(7, string.Format("{0} {1}", h.Ver, HdcpResultText(h)), h.Result == 1 ? Pal.Good : h.Result == 0 ? Pal.Red : Pal.Warn);
            }
            string ed = Edid.Short(dec.EdidData, dec.EdidValid);
            SetState(8, ed.Replace(" · ", "\n"), ed == "-" ? gray : Pal.Text);

            UpdateSessions();
            tabHdcp.Header = dec.HdcpTries.Count > 0 ? string.Format("HDCP ({0})", dec.HdcpTries.Count) : "HDCP";
        }

        void UpdateSessions()
        {
            var ss = dec.Sessions;
            var rows = new List<TRow>();
            foreach (var s in ss)
            {
                Color? c;
                string t = SessionText(s, out c);
                rows.Add(new TRow { Cells = new[] { s.No.ToString(), SessTime(s), t }, Fore = c, Tag = s });
            }
            bool end = sessTable.AtEnd();
            int keep = sessTable.SelectedIndex;
            sessTable.SetRows(rows);
            if (end) sessTable.ScrollToEnd();
            sessVer = dec.Version;
        }

        // ------------------------------------------------------------------
        // FRL training tab
        // ------------------------------------------------------------------
        Control BuildTrainTab()
        {
            trainTable = new Table("#", 44, "Time", 96, "Duration", 84, "Rate", 100, "FFE", 50, "LTP requests", 240, "Updates", 74, "FFE up", 70, "Result", 100, "End", 400);
            laneGrid = new LaneGrid();
            trainTable.SelectionChanged += () => laneGrid.Set(trainTable.Selected != null ? trainTable.Selected.Tag as FrlAttempt : null);
            trainTable.List.DoubleTapped += (s, e) =>
            {
                if (trainTable.Selected != null && trainTable.Selected.Tag is FrlAttempt a) GoToGroup(a.GroupId);
            };
            // list on top, the lane grid of the selected training below (a splitter between them)
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            g.RowDefinitions.Add(new RowDefinition(6, GridUnitType.Pixel));
            g.RowDefinitions.Add(new RowDefinition(250, GridUnitType.Pixel) { MinHeight = 120 });
            Grid.SetRow(laneGrid, 2);
            var sp = new GridSplitter { Background = Pal.Br(Pal.Canvas), ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(sp, 1);
            g.Children.Add(trainTable);
            g.Children.Add(sp);
            g.Children.Add(laneGrid);
            return g;
        }

        LaneGrid laneGrid;

        static string ResultText(FrlAttempt t)
        {
            return t.Result == 1 ? "OK" : t.Result == 0 ? "FAILED" : t.End.Length > 0 ? "aborted" : "in progress";
        }

        void RebuildTrain()
        {
            var rows = new List<TRow>();
            foreach (var t in dec.Trainings)
            {
                string dur = t.EndUs >= 0 ? string.Format(CultureInfo.InvariantCulture, "{0:F1} ms", (t.EndUs - t.StartUs) / 1000.0) : "-";
                rows.Add(new TRow
                {
                    Cells = new[] { t.No.ToString(), Decoder.FmtTime(t.StartUs).Trim('[', ']').Trim(), dur, Decoder.Rate(t.Rate), t.FfeLevels.ToString(),
                                    Decoder.LtpSeq(t), t.Updates.ToString(), t.FfeReq.ToString(), ResultText(t), t.End },
                    Fore = t.Result == 1 ? Lc(0, 130, 40) : t.Result == 0 ? Lc(200, 30, 30) : Lc(128, 128, 128), Tag = t
                });
            }
            trainTable.SetRows(rows);
            if (trainTable.SelectedIndex < 0 && rows.Count > 0) trainTable.SelectedIndex = rows.Count - 1;
            laneGrid.Set(trainTable.Selected != null ? trainTable.Selected.Tag as FrlAttempt : null);
            tabScdcTrain.Header = string.Format("FRL TRAINING ({0})", dec.Trainings.Count);
            trainVer = dec.Version;
        }

        static Color Lc(int r, int g, int b) { return Color.FromRgb((byte)r, (byte)g, (byte)b); }

        // ------------------------------------------------------------------
        // HDCP tab
        // ------------------------------------------------------------------
        Control BuildHdcpTab()
        {
            hdcpTable = new Table("#", 44, "Session", 70, "Time", 100, "Version", 70, "Result", 260, "Steps", 900);
            hdcpTable.SelectionChanged += () =>
            {
                var t = hdcpTable.Selected;
                if (t != null && t.Tag is HdcpAttempt h) GoToGroup(h.GroupId);
            };
            return hdcpTable;
        }


        void RebuildHdcp()
        {
            var rows = new List<TRow>();
            foreach (var h in dec.HdcpTries)
            {
                // the steps as chips: purple = written by the source, teal = read from the sink, red = REAUTH_REQ, last = result
                var chips = new List<Chip>();
                for (int i = 0; i < h.Steps.Count; i++)
                {
                    var ch = new Chip { Text = h.Steps[i] + (h.Reps[i] > 1 ? " ×" + h.Reps[i] : "") };
                    if (h.Steps[i] == "REAUTH_REQ") { ch.Fore = Pal.Red; ch.Back = Pal.RedBack; }
                    else if (h.FromSink[i]) { ch.Fore = Pal.EdidText; ch.Back = Pal.EdidBack; }
                    else { ch.Fore = Pal.HdcpText; ch.Back = Pal.HdcpBack; }
                    chips.Add(ch);
                }
                chips.Add(new Chip
                {
                    Text = h.Result == 1 ? "authenticated" : h.Result == 0 ? "failed" : "in progress", Arrow = false,
                    Fore = h.Result < 0 ? Pal.Muted : Pal.Dark ? Pal.Canvas : Colors.White,
                    Back = h.Result == 1 ? Pal.Good : h.Result == 0 ? Pal.Red : Pal.Dark ? Pal.Header : Color.FromRgb(236, 236, 236)
                });
                Color c = h.Result == 1 ? Lc(20, 130, 50) : h.Result == 0 ? Lc(200, 30, 30) : Lc(190, 120, 0);
                rows.Add(new TRow
                {
                    Cells = new[] { h.No.ToString(), h.Session.ToString(), ShortTime(h.Us), h.Ver, HdcpResultText(h), "" },
                    CellFore = new Color?[] { c, null, null, null, c, null }, Tag = h, ChipCol = 5, Chips = chips
                });
            }
            hdcpTable.SetRows(rows);
            hdcpVer = dec.Version;
        }

        // ------------------------------------------------------------------
        // registers tab (last value of every SCDC / HDCP register)
        // ------------------------------------------------------------------
        Control BuildRegsTab()
        {
            regsTable = new Table("Address", 80, "Name", 220, "Value", 70, "R/W", 50, "Meaning", 440, "Time", 120, "Count", 200);
            return regsTable;
        }

        void RebuildRegs()
        {
            var rows = new List<TRow>();
            foreach (var rs in dec.Regs.Values)
            {
                string name = "", mean = "";
                if (rs.Dev == Decoder.DevScdc)
                {
                    if (!Decoder.ScdcNames.TryGetValue(rs.Off, out name)) name = "";
                    mean = Decoder.DecodeScdc(rs.Off, rs.Value);
                }
                else name = "HDCP " + rs.Off.ToString("X2");
                rows.Add(new TRow
                {
                    Cells = new[] { string.Format("{0:X2}:{1:X2}", rs.Dev, rs.Off), name, rs.Value.ToString("X2"), rs.Access == 'W' ? "W" : "R", mean,
                                    Decoder.FmtTime(rs.Us).Trim('[', ']').Trim(), rs.Count.ToString() },
                    Fore = rs.Access == 'W' ? Lc(0, 90, 160) : name.Length == 0 ? Lc(128, 128, 128) : (Color?)null
                });
            }
            regsTable.SetRows(rows);
            regsVer = dec.Version;
        }

        // ------------------------------------------------------------------
        // statistics tab
        // ------------------------------------------------------------------
        Control BuildStatsTab()
        {
            statsTable = new Table("Item", 360, "Value", 300);
            return statsTable;
        }

        List<TRow> statRows;

        void StatHead(string t)
        {
            statRows.Add(new TRow { Cells = new[] { t, "" }, Back = Lc(236, 238, 242), Bold = true });
        }

        void StatRow(string name, string value)
        {
            statRows.Add(new TRow { Cells = new[] { "    " + name, value } });
        }

        void StatRow(string name, long value)
        {
            StatRow(name, value.ToString("N0", CultureInfo.InvariantCulture));
        }

        static int Count(SortedDictionary<int, int> d, int k)
        {
            int v;
            return d.TryGetValue(k, out v) ? v : 0;
        }

        void RebuildStats()
        {
            var s = dec.St;
            statRows = new List<TRow>();
            StatHead("Input");
            StatRow("raw lines", s.Lines);
            StatRow("unknown lines", s.Unknown);
            StatHead("Transaction");
            StatRow("reads (RD, SCDC·HDCP)", s.Reads);
            StatRow("writes (WR, SCDC·HDCP)", s.Writes);
            StatRow("I2C (EDID etc.)", s.I2c);
            StatRow("ACK", s.Ack);
            StatRow("NACK", s.Nack);
            StatRow("    address NACK (no device)", s.AddrNack);
            StatRow("    data NACK", s.DataNack);
            StatRow("cut off (ended without STOP)", s.Cut);
            StatHead("Byte");
            StatRow("bytes read", s.BytesRd);
            StatRow("bytes written", s.BytesWr);
            StatHead("Transfers per device");
            if (s.Devs.Count == 0) StatRow("(none)", "");
            foreach (var kv in s.Devs)
            {
                string n = Decoder.DevName(kv.Key);
                StatRow(string.Format("{0:X2}{1}", kv.Key, n.Length > 0 ? "  " + n : ""), kv.Value);
            }
            StatHead("FRL link training");
            StatRow("attempts", dec.Trainings.Count);
            StatRow("OK (FRL_start)", s.TrainOk);
            StatRow("failed", s.TrainFail);
            StatHead("Event");
            if (s.Events.Count == 0) StatRow("(none)", "");
            foreach (var kv in s.Events)
                StatRow(string.Format("{0:X2}  {1}", kv.Key, Decoder.EventName(kv.Key)), kv.Value);
            statsTable.SetRows(statRows);
            statsVer = dec.Version;
        }

        // ------------------------------------------------------------------
        // EDID tab
        // ------------------------------------------------------------------
        Control BuildEdidTab()
        {
            tbEdid = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = Table.Mono, FontSize = Table.FontSize, TextWrapping = TextWrapping.NoWrap,
                                   BorderThickness = new Thickness(0), Background = Pal.Br(Pal.Surface) };
            var save = Btn("Save EDID (.bin)", () => { }, "Save the EDID as a .bin file", Icons.Save); save.Margin = new Thickness(8, 6, 0, 6); save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Click += async (s, e) => await SaveEdid();
            var dp = new DockPanel();
            DockPanel.SetDock(save, Dock.Top);
            dp.Children.Add(save);
            dp.Children.Add(tbEdid);
            return dp;
        }

        void RebuildEdid()
        {
            tbEdid.Text = Edid.Describe(dec.EdidData, dec.EdidValid);
            int n = 0;
            foreach (bool v in dec.EdidValid) if (v) n++;
            tabEdid.Header = n > 0 ? string.Format("EDID ({0})", n) : "EDID";
            edidVer = dec.EdidVersion;
        }

        async System.Threading.Tasks.Task SaveEdid()
        {
            if (!dec.EdidValid[0]) { Say("EDID block 0 not received yet."); return; }
            int n = dec.EdidData[126] + 1, have = 0;
            while (have < n && have < dec.EdidValid.Length && dec.EdidValid[have]) have++;
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "edid.bin", Title = "Save EDID" });
            if (f == null) return;
            var bytes = new byte[have * 128];
            Array.Copy(dec.EdidData, bytes, bytes.Length);
            try { File.WriteAllBytes(f.Path.LocalPath, bytes); Say(string.Format("EDID saved ({0} of {1} blocks)", have, n)); }
            catch (Exception ex) { Say("Cannot save the EDID: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // raw tab
        // ------------------------------------------------------------------
        Control BuildRawTab()
        {
            rawTable = new Table("Raw line", 1000);
            rawShown = 0;
            return rawTable;
        }

        void AppendRaw()
        {
            if (rawShown >= raw.Count) return;
            bool end = rawTable.AtEnd();
            if (raw.Count - rawShown > 50000)
            {
                rawTable.SetRows(new List<TRow>());
                rawShown = Math.Max(rawShown, raw.Count - 50000);
                rawTable.Rows.Add(new TRow { Span = true, Cells = new[] { "(earlier lines left out; Save raw saves them all)" }, Fore = Lc(140, 140, 140) });
            }
            for (; rawShown < raw.Count; rawShown++)
                rawTable.Rows.Add(new TRow { Cells = new[] { raw[rawShown] } });
            if (end) rawTable.ScrollToEnd();
        }
    }
}
