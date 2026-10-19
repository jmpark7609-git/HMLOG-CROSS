// Log tab: one line per transfer (or the expanded rows), FRL training bands, changed values and the detail
// panel of the selected transfer. Port of MainForm.Log.cs of the Windows version (view building is the same).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace HMLOG.App
{
    enum VKind { Txn, Row, BandStart, BandEnd, Fold, Edid }

    class VRow
    {
        public VKind Kind;
        public Group G;
        public Row R;                     // expanded view
        public string Text = "";          // band / fold
        public long Us = -1, Delta = -1;
        public List<Seg> Segs;
        public TRow T;                    // the visual row
    }

    partial class MainWindow
    {
        Table logTable, detailTable;
        TextBlock lblDetail;
        BitView bitView;
        readonly List<VRow> view = new List<VRow>();
        Group detailGroup;

        // view build state: groups of dec already in the view, fold / delta state
        Decoder builtDec;
        int builtGroups, shownGroups;
        Group vPrev;
        VRow vFold;
        int vRep;
        long vLastUs = -1;
        List<int[]> vRanges;
        string vQuery = "";
        string searchQ = "";

        Control BuildLogTab()
        {
            logTable = new Table("Time", 104, "+Δ", 100, "Device", 78, "R/W", 52, "Address", 80, "Reply", 84, "Length", 66, "Content", 900);
            logTable.SelectionChanged += OnLogSelect;
            lblDetail = new TextBlock { Text = "Select a transfer to see its raw line and registers here", Margin = new Thickness(10, 6, 4, 6), TextTrimming = TextTrimming.CharacterEllipsis };
            detailTable = new Table("Address", 80, "Name", 190, "Value", 80, "Previous", 80, "Meaning", 600);
            detailTable.SelectionChanged += () =>
            {
                var t = detailTable.Selected;
                if (t != null) bitView.Set(t.Tag as RegItem);
            };
            bitView = new BitView();

            var detail = new DockPanel();
            DockPanel.SetDock(lblDetail, Dock.Top);
            DockPanel.SetDock(bitView, Dock.Bottom);
            detail.Children.Add(lblDetail);
            detail.Children.Add(bitView);
            detail.Children.Add(detailTable);

            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            g.RowDefinitions.Add(new RowDefinition(6, GridUnitType.Pixel));
            g.RowDefinitions.Add(new RowDefinition(280, GridUnitType.Pixel));
            Grid.SetRow(logTable, 0);
            var sp = new GridSplitter { Background = Pal.Br(Pal.Canvas), ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetRow(sp, 1);
            Grid.SetRow(detail, 2);
            g.Children.Add(logTable);
            g.Children.Add(sp);
            g.Children.Add(detail);

            btnNew = new Button { IsVisible = false, Content = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                                  Margin = new Thickness(0, 0, 0, 12), Background = Pal.Br(Pal.Accent), Foreground = Brushes.White };
            btnNew.Click += (s, e) => GoEnd();
            Grid.SetRow(btnNew, 0);
            g.Children.Add(btnNew);
            return g;
        }

        Button btnNew;
        int newRows;

        void GoEnd()
        {
            logTable.ScrollToEnd();
            newRows = 0;
            btnNew.IsVisible = false;
        }

        // the "new lines" button: shown while scrolled up and lines came in
        void ShowNewRows(int added)
        {
            if (logTable.AtEnd()) { newRows = 0; btnNew.IsVisible = false; return; }
            newRows += added;
            if (newRows <= 0) return;
            btnNew.Content = string.Format("{0:N0} new lines  ↓ to the end", newRows);
            btnNew.IsVisible = true;
        }

        // ------------------------------------------------------------------
        // view rows
        // ------------------------------------------------------------------
        static string ShortTime(long us)
        {
            return us < 0 ? "" : string.Format("{0}.{1:D6}", us / 1000000, us % 1000000);
        }

        static string FmtDelta(long d)
        {
            if (d < 0) return "";
            if (d < 1000) return "+" + d + " us";
            if (d < 1000000) return string.Format(CultureInfo.InvariantCulture, "+{0:F1} ms", d / 1000.0);
            return string.Format(CultureInfo.InvariantCulture, "+{0:F2} s", d / 1000000.0);
        }

        static string BandStartText(FrlAttempt t)
        {
            return string.Format("FRL training #{0} start · {1} · FFE level {2}", t.No, Decoder.Rate(t.Rate), t.FfeLevels);
        }

        static string BandEndText(FrlAttempt t)
        {
            return string.Format(CultureInfo.InvariantCulture, "FRL training #{0} {1} · {2} · {3:F1} ms · LTP {4} · FFE up requests {5}{6}",
                t.No, t.Result == 1 ? "OK" : "FAILED", Decoder.Rate(t.Rate), (t.EndUs - t.StartUs) / 1000.0,
                Decoder.LtpSeq(t), t.FfeReq, t.End.Length > 0 ? " · " + t.End : "");
        }

        static bool AddrHit(Group g, List<int[]> ranges)
        {
            foreach (var rg in ranges)
            {
                foreach (int a in g.Addrs) if (a >= rg[0] && a <= rg[1]) return true;
                if (rg[2] == 1) foreach (int d in g.Devs) if (d == rg[0] >> 8) return true;
            }
            return false;
        }

        // address filter: "54" (device), "50,54", "54:31", "54:40-42"; ranges over dev << 8 | register
        List<int[]> ParseAddr(out bool bad)
        {
            bad = false;
            string t = tbAddr.Text == null ? "" : tbAddr.Text.Trim();
            if (t.Length == 0) return null;
            var r = new List<int[]>();
            foreach (string part in t.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] dr = part.Split(':');
                int dev, a, b;
                if (!int.TryParse(dr[0], NumberStyles.HexNumber, null, out dev) || dev > 0x7F) { bad = true; continue; }
                if (dr.Length == 1) { r.Add(new[] { dev << 8, (dev << 8) | 0xFF, 1 }); continue; }
                string[] ab = dr[1].Split('-');
                if (!int.TryParse(ab[0], NumberStyles.HexNumber, null, out a)) { bad = true; continue; }
                b = a;
                if (ab.Length > 1 && !int.TryParse(ab[1], NumberStyles.HexNumber, null, out b)) { bad = true; continue; }
                r.Add(new[] { (dev << 8) | Math.Min(a, b), (dev << 8) | Math.Max(a, b), 0 });
            }
            return r;
        }

        static bool Matches(Group g, string q)
        {
            foreach (var r in g.Rows)
                if (r.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.Addr.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.Value.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Type.Equals(q, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Filter changed (or new data source): the whole view is built again. Otherwise only the new groups are
        // appended, so existing rows keep their place and the list does not jump.
        void RebuildView()
        {
            bool full = filterDirty || builtDec != dec || builtGroups > dec.Groups.Count;
            int oldCount = view.Count;
            int keepId = -1;
            if (full)
            {
                bool bad;
                vRanges = ParseAddr(out bad);
                tbAddr.Background = bad ? Pal.Br(Pal.Bg(252, 225, 225)) : null;
                vQuery = chkOnlyHits.IsChecked == true ? searchQ : "";
                var sel = logTable.Selected;
                if (sel != null && sel.Tag is VRow sv && sv.G != null) keepId = sv.G.Id;
                view.Clear();
                builtDec = dec;
                builtGroups = 0;
                shownGroups = 0;
                vPrev = null; vFold = null; vRep = 0; vLastUs = -1;
            }
            bool follow = full || logTable.AtEnd();
            int foldAt = vFold != null ? view.IndexOf(vFold) : -1;
            var added = new List<VRow>();
            AddGroups(added);

            if (full)
            {
                var rows = new List<TRow>(view.Count);
                int selAt = -1;
                for (int i = 0; i < view.Count; i++)
                {
                    rows.Add(view[i].T);
                    if (keepId >= 0 && selAt < 0 && view[i].G != null && view[i].G.Id == keepId) selAt = i;
                }
                logTable.SetRows(rows);
                newRows = 0; btnNew.IsVisible = false;
                if (selAt >= 0) { logTable.SelectedIndex = selAt; logTable.ScrollTo(selAt); }
                else if (view.Count > 0) logTable.ScrollToEnd();
            }
            else
            {
                if (foldAt >= 0 && foldAt < logTable.Rows.Count && vFold != null) logTable.Rows[foldAt] = vFold.T;   // the count grew
                foreach (var v in added) logTable.Rows.Add(v.T);
                if (follow) logTable.ScrollToEnd(); else ShowNewRows(view.Count - oldCount);
            }
            lblCount.Text = string.Format("{0:N0} of {1:N0}", shownGroups, dec.Groups.Count);
            shownGroupsLast = shownGroups;
            viewVer = dec.Version;
            filterDirty = false;
            if (detailGroup != null && full) ShowDetail(detailGroup);
        }

        // the groups from builtGroups on, with the current filter; new rows go to the view and to added
        void AddGroups(List<VRow> added)
        {
            bool expand = chkExpand.IsChecked == true;
            bool train = chkTrain.IsChecked == true, err = chkErr.IsChecked == true, fold = chkFold.IsChecked == true;
            for (; builtGroups < dec.Groups.Count; builtGroups++)
            {
                var g = dec.Groups[builtGroups];
                if (train && !g.Training) continue;
                if (err && !g.Error) continue;
                if (chipCat[g.Cat].IsChecked != true) continue;
                if (vRanges != null && !AddrHit(g, vRanges)) continue;
                if (vQuery.Length > 0 && !Matches(g, vQuery)) continue;

                if (fold && vPrev != null && g.IsTransaction && vPrev.IsTransaction &&
                    !g.HasResult && !vPrev.HasResult && g.TrainStart == null && g.EdidLines.Count == 0 &&
                    g.Sig.Length > 0 && g.Sig == vPrev.Sig)
                {
                    vRep++;
                    if (vFold == null)
                    {
                        vFold = new VRow { Kind = VKind.Fold, G = vPrev };
                        Push(vFold, added);
                    }
                    vFold.Text = string.Format("… same transaction {0} more times (last {1} s)", vRep, ShortTime(g.Us));
                    vFold.T = MakeRow(vFold);
                    vLastUs = g.Us;
                    continue;
                }
                vPrev = g;
                vFold = null;
                vRep = 0;
                shownGroups++;

                if (g.TrainStart != null)
                    Push(new VRow { Kind = VKind.BandStart, G = g, Text = BandStartText(g.TrainStart) }, added);
                if (expand)
                {
                    foreach (var r in g.Rows)
                    {
                        var v = new VRow { Kind = VKind.Row, G = g, R = r, Us = r.Us };
                        if (r.Us >= 0) { if (vLastUs >= 0) v.Delta = r.Us - vLastUs; vLastUs = r.Us; }
                        Push(v, added);
                    }
                }
                else
                {
                    var v = new VRow { Kind = VKind.Txn, G = g, Us = g.Us };
                    if (g.Us >= 0) { if (vLastUs >= 0) v.Delta = g.Us - vLastUs; vLastUs = g.Us; }
                    Push(v, added);
                    if (g.TrainEnd != null)
                        Push(new VRow { Kind = VKind.BandEnd, G = g, Text = BandEndText(g.TrainEnd) }, added);
                    foreach (string s in g.EdidLines)
                        Push(new VRow { Kind = VKind.Edid, G = g, Text = s.Replace("== ", "").Replace(" ==", "") }, added);
                }
            }
        }

        void Push(VRow v, List<VRow> added)
        {
            v.T = MakeRow(v);
            view.Add(v);
            added.Add(v);
        }

        // ------------------------------------------------------------------
        // the visual row of a view row
        // ------------------------------------------------------------------
        static System.Drawing.Color L(int r, int g, int b) { return System.Drawing.Color.FromArgb(r, g, b); }
        static Color C(int r, int g, int b) { return Color.FromRgb((byte)r, (byte)g, (byte)b); }

        string TimeText(Group g, long us)
        {
            if (cbTime.SelectedIndex == 1 && g != null && g.Pc != DateTime.MinValue) return g.Pc.ToString("HH:mm:ss.fff");
            return ShortTime(us);
        }

        TRow MakeRow(VRow v)
        {
            TRow t;
            switch (v.Kind)
            {
                case VKind.Fold:
                    t = new TRow { Span = true, Cells = new[] { v.Text }, Fore = C(140, 140, 140), Indent = 360, Tag = v };
                    break;
                case VKind.BandStart:
                    t = new TRow { Span = true, Cells = new[] { "▶ " + v.Text }, Fore = C(25, 85, 160), Back = C(226, 238, 252), Bold = true, Tag = v };
                    break;
                case VKind.Edid:
                    t = new TRow { Span = true, Cells = new[] { "◆ " + v.Text }, Fore = C(0, 115, 100), Back = C(214, 241, 236), Bold = true, Tag = v };
                    break;
                case VKind.BandEnd:
                    bool ok = v.G.TrainEnd.Result == 1;
                    t = new TRow { Span = true, Cells = new[] { "■ " + v.Text }, Fore = ok ? C(20, 130, 50) : C(200, 30, 30),
                                   Back = ok ? C(225, 245, 230) : C(252, 225, 225), Bold = true, Tag = v };
                    break;
                default:
                    t = MakeDataRow(v);
                    break;
            }
            t.Hit = RowHit(v, t);
            return t;
        }

        TRow MakeDataRow(VRow v)
        {
            var g = v.G;
            var t = new TRow { Cells = new string[8], CellFore = new Color?[8], Badges = new Dictionary<int, Color[]>(), Tag = v };
            bool errRow = g.Error && (g.IsEvent || v.Kind == VKind.Txn && g.Status != "ACK");
            if (errRow) t.Back = C(252, 225, 225);
            if (v.Kind == VKind.Row && (v.R.Kind == RowKind.Training || v.R.Kind == RowKind.Edid) && !v.R.Back.IsEmpty)
                t.Back = C(v.R.Back.R, v.R.Back.G, v.R.Back.B);
            bool head = v.Kind == VKind.Txn || (v.R != null && v.R.Us >= 0);
            t.Cells[0] = head || v.Us >= 0 ? TimeText(g, v.Us) : "";
            t.Cells[1] = FmtDelta(v.Delta);
            t.CellFore[1] = v.Delta >= 10000 ? (Color?)null : C(140, 140, 140);
            if (head)
            {
                if (v.Kind == VKind.Row && (v.R.Kind == RowKind.Training || v.R.Kind == RowKind.Edid))
                {
                    t.Cells[2] = v.R.Kind == RowKind.Training ? "FRL" : "EDID";
                    t.CellFore[2] = C(v.R.Color.R, v.R.Color.G, v.R.Color.B);
                }
                else if (g.Kind == "EVENT" && g.Cat == Decoder.CatCec)
                {
                    t.Cells[2] = "CEC";
                    t.Badges[2] = g.Error ? new[] { Pal.Red, Pal.RedBack } : new[] { Pal.CecText, Pal.CecBack };
                }
                else if (g.IsTransaction) DevBadge(t, g);
                else
                {
                    string ty = g.Rows.Count > 0 ? g.Rows[0].Type : "??";
                    t.Cells[2] = ty;
                    t.CellFore[2] = ty == "??" ? C(200, 30, 30) : C(140, 140, 140);
                }
            }
            if (head && g.IsTransaction && !(v.Kind == VKind.Row && v.R.Kind != RowKind.Xfer))
            {
                bool rd = g.Kind == "RD" || g.Kind == "I2C" && g.I2cText.StartsWith("RD");
                t.Cells[3] = rd ? "RD" : "WR";
                t.CellFore[3] = rd ? C(20, 85, 165) : C(95, 55, 165);
            }
            t.Cells[4] = v.Kind == VKind.Txn ? (g.IsTransaction ? Decoder.AddrText(g) : "") : v.R.Addr;
            if (v.Kind == VKind.Txn || (v.R != null && v.R.Kind == RowKind.Reply))
            {
                string s = g.Status;
                if (s == "ACK") { t.Cells[5] = "ACK"; t.CellFore[5] = C(20, 130, 50); }
                else if (s.Length > 0) { t.Cells[5] = s == "cut off" ? "CUT" : s; t.Badges[5] = new[] { Pal.Red, Pal.RedBack }; }
            }
            if ((v.Kind == VKind.Txn || (v.R != null && v.R.Kind == RowKind.Xfer)) && g.IsTransaction) t.Cells[6] = "x" + g.Len;
            t.SegCol = 7;
            if (v.Kind == VKind.Txn)
            {
                v.Segs = Summary.Of(g);
                t.Segs = v.Segs;
                t.SegFore = g.Kind == "EVENT" ? (g.Error ? Pal.Red : Pal.EvtText) : errRow ? Pal.Red : Pal.Text;
                t.Cells[7] = Summary.Text(v.Segs);
            }
            else DataRowText(t, v.R);
            return t;
        }

        void DataRowText(TRow t, Row r)
        {
            if (r.Kind == RowKind.Reg)
            {
                t.Segs = new List<Seg> { new Seg("  " + r.Name.PadRight(20) + " = ", Mark.None),
                                         new Seg(r.Value + "  " + r.Text, r.Changed ? Mark.Changed : Mark.None) };
                t.SegFore = Pal.Text;
                t.Cells[7] = Summary.Text(t.Segs);
            }
            else if (r.Kind == RowKind.Dump) { t.Cells[7] = "  " + r.Text; t.CellFore[7] = C(140, 140, 140); }
            else if (r.Kind == RowKind.Reply) { if (r.Text.Contains("no device")) { t.Cells[7] = "(no device)"; t.CellFore[7] = C(200, 30, 30); } }
            else { t.Cells[7] = r.Text; t.CellFore[7] = C(r.Color.R, r.Color.G, r.Color.B); }
        }

        // device of a transfer: SCDC / HDCP / EDID / other I2C (by address)
        void DevBadge(TRow t, Group g)
        {
            switch (g.Cat)
            {
                case Decoder.CatScdc: t.Cells[2] = "SCDC"; t.Badges[2] = new[] { Pal.ScdcText, Pal.ScdcBack }; break;
                case Decoder.CatHdcp: t.Cells[2] = "HDCP"; t.Badges[2] = new[] { Pal.HdcpText, Pal.HdcpBack }; break;
                case Decoder.CatEdid: t.Cells[2] = "EDID"; t.Badges[2] = new[] { Pal.EdidText, Pal.EdidBack }; break;
                default: t.Cells[2] = g.Dev == Decoder.DevCi ? "CI" : g.Dev.ToString("X2"); t.Badges[2] = new[] { Pal.I2cText, Pal.I2cBack }; break;
            }
        }

        // search hit (rows of the log are marked yellow)
        bool RowHit(VRow v, TRow t)
        {
            string q = searchQ;
            if (q.Length == 0) return false;
            if (v.Kind == VKind.Txn)
                return Matches(v.G, q) || (t.Cells[7] ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            if (v.Kind == VKind.Row)
                return v.R.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.R.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       v.R.Addr.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.R.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            return v.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------
        // search: matching rows marked, Enter / F3 next, Shift previous
        // ------------------------------------------------------------------
        int hitsTotal;

        void SearchChanged()
        {
            string q = (tbSearch.Text ?? "").Trim();
            if (q == searchQ) return;
            searchQ = q;
            if (chkOnlyHits.IsChecked == true) { filterDirty = true; return; }
            hitsTotal = 0;
            foreach (var v in view) { v.T.Hit = RowHit(v, v.T); if (v.T.Hit) hitsTotal++; }
            tbSearch.Background = q.Length > 0 && hitsTotal == 0 ? Pal.Br(Pal.Bg(252, 225, 225)) : null;
            logTable.Refresh();
            HitsLabel();
        }

        void HitsLabel()
        {
            if (searchQ.Length == 0) { lblHits.Text = ""; return; }
            int sel = logTable.SelectedIndex, k = 0;
            if (sel >= 0 && sel < view.Count && view[sel].T.Hit)
                for (int i = 0; i <= sel; i++) if (view[i].T.Hit) k++;
            lblHits.Text = hitsTotal == 0 ? "none" : string.Format("{0} / {1:N0}", k > 0 ? k.ToString("N0") : "-", hitsTotal);
        }

        void NextHit(int dir)
        {
            SearchChanged();
            if (searchQ.Length == 0 || view.Count == 0) return;
            tabs.SelectedIndex = 0;
            int sel = logTable.SelectedIndex >= 0 ? logTable.SelectedIndex : dir > 0 ? -1 : view.Count;
            for (int step = 1; step <= view.Count; step++)
            {
                int i = ((sel + dir * step) % view.Count + view.Count) % view.Count;
                if (!view[i].T.Hit) continue;
                logTable.SelectedIndex = i;
                logTable.ScrollTo(i);
                HitsLabel();
                return;
            }
            HitsLabel();
        }

        // F8 / Shift+F8: next / previous error row
        void NextError(int dir)
        {
            if (view.Count == 0) return;
            tabs.SelectedIndex = 0;
            int i = logTable.SelectedIndex >= 0 ? logTable.SelectedIndex : dir > 0 ? -1 : view.Count;
            for (int k = 0; k < view.Count; k++)
            {
                i += dir;
                if (i < 0 || i >= view.Count) { Say(dir > 0 ? "no error line after this" : "no error line before this"); return; }
                var g = view[i].G;
                if (g != null && g.Error && (i == 0 || view[i - 1].G != g)) { logTable.SelectedIndex = i; logTable.ScrollTo(i); return; }
            }
        }

        // the first row of group id (or the next one shown), selected in the log tab
        void GoToGroup(int id)
        {
            tabs.SelectedIndex = 0;
            if (dec.Version != viewVer || filterDirty) RebuildView();
            int i = 0;
            while (i < view.Count && (view[i].G == null || view[i].G.Id < id)) i++;
            if (i >= view.Count) { Say("That line is hidden by a filter"); return; }
            logTable.SelectedIndex = i;
            logTable.ScrollTo(i);
        }

        // ------------------------------------------------------------------
        // detail panel
        // ------------------------------------------------------------------
        void OnLogSelect()
        {
            var t = logTable.Selected;
            if (t != null && t.Tag is VRow v && v.G != null) ShowDetail(v.G);
            HitsLabel();
        }

        static string Ago(long us)
        {
            if (us < 1000) return us + " us";
            if (us < 1000000) return (us / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " ms";
            return (us / 1000000.0).ToString("F2", CultureInfo.InvariantCulture) + " s";
        }

        // the last HPD / 5V / link event before a group
        string PrevEvent(Group g)
        {
            if (g.Us < 0) return "";
            for (int k = g.Id - 1; k >= 0 && k < dec.Groups.Count; k--)
            {
                var e = dec.Groups[k];
                if (e.IsEvent && e.Us >= 0 && e.EventCode >= 1 && e.EventCode <= 0x0C)
                    return string.Format("  ·  previous event: {0} ({1} before)", e.EventText, Ago(g.Us - e.Us));
            }
            return "";
        }

        void ShowDetail(Group g)
        {
            detailGroup = g;
            string head;
            if (g.IsTransaction)
                head = string.Format("{0}  {1}  ·  {2}  ·  raw: {3}", ShortTime(g.Us),
                    g.Kind == "I2C" ? "I2C " + g.I2cText : string.Format("{0} {1} x{2}  ({3})", g.Kind, Decoder.AddrText(g), g.Len, g.DevLabel),
                    g.Status, g.Sig);
            else
                head = ShortTime(g.Us) + "  " + (g.EventText.Length > 0 ? g.EventText : g.Rows.Count > 0 ? g.Rows[0].Text : "");
            if (g.Error) head += PrevEvent(g);
            lblDetail.Text = head;

            var rows = new List<TRow>();
            foreach (var r in g.Regs)
            {
                var t = new TRow
                {
                    Cells = new[] { string.Format("{0:X2}:{1:X2}", r.Dev, r.Off), r.Name, r.Value.ToString("X2"), r.Prev >= 0 ? r.Prev.ToString("X2") : "", r.Meaning },
                    Tag = r
                };
                if (r.Changed) t.Back = C(255, 232, 140);
                else if (r.Name.Length == 0) t.Fore = C(140, 140, 140);
                rows.Add(t);
            }
            if (g.Regs.Count == 0 && g.Text.Length > 0)
                rows.Add(new TRow { Cells = new[] { g.Dev >= 0 ? g.Dev.ToString("X2") : "", g.DevLabel, "", "", g.Text } });
            detailTable.SetRows(rows);
            // bit view: the first register that has bit fields (else the first one)
            RegItem show = null;
            foreach (var r in g.Regs)
                if (Decoder.BitFields(r.Dev, r.Off) != null) { show = r; break; }
            if (show == null && g.Regs.Count > 0) show = g.Regs[0];
            bitView.Set(show);
            if (show != null) detailTable.SelectedIndex = g.Regs.IndexOf(show);
        }
    }
}
