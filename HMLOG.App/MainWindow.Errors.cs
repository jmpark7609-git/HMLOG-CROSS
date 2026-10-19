// DATA > ERRORS: NACK / cut-off / error events in detail: counters by kind, a summary by device and address,
// and every error with what happened just before it (previous transfer, HPD / 5 V / OPTICAL change, repeats).
// Port of MainForm.Err.cs of the Windows version. Reads the decoder only.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace HMLOG.App
{
    partial class MainWindow
    {
        TabItem tabData, tabErrors;
        TabControl dataTabs;
        Table errAgg, errList;
        TextBlock errTileAddr, errTileData, errTileCut, errTileLost;
        TextBlock errListLabel, errAggLabel;
        int errVer = -1;

        Control BuildErrTab()
        {
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 0) };
            tiles.Children.Add(ErrTile("Address NACK", "no device answers", out errTileAddr));
            tiles.Children.Add(ErrTile("Data NACK", "a device refused a byte", out errTileData));
            tiles.Children.Add(ErrTile("Cut off", "ended without STOP", out errTileCut));
            tiles.Children.Add(ErrTile("Bytes lost", "FPGA FIFO / UART", out errTileLost));

            errAggLabel = new TextBlock { Text = "By device and address", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(8, 8, 8, 2) };
            errAgg = new Table("Device", 60, "Address", 90, "Kind", 135, "Count", 110, "First", 110, "Last", 110, "Note", 560);

            errListLabel = new TextBlock { Text = "Every error, with what happened just before it (double click: show it in the LOG)",
                                           Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(8, 8, 8, 2) };
            errList = new Table("Device", 60, "Address", 90, "Kind", 135, "Time", 110, "Transfer", 220, "+D prev", 90,
                                "Previous transfer", 190, "Last HPD / 5V / OPTICAL", 170, "Since", 80, "Repeat", 60);
            errList.List.DoubleTapped += (s, e) =>
            {
                if (errList.Selected != null && errList.Selected.Tag is int) GoToGroup((int)errList.Selected.Tag);
            };

            var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,160,Auto,*") };
            Grid.SetRow(tiles, 0);
            Grid.SetRow(errAggLabel, 1);
            Grid.SetRow(errAgg, 2);
            Grid.SetRow(errListLabel, 3);
            Grid.SetRow(errList, 4);
            g.Children.Add(tiles);
            g.Children.Add(errAggLabel);
            g.Children.Add(errAgg);
            g.Children.Add(errListLabel);
            g.Children.Add(errList);
            return g;
        }

        Control ErrTile(string caption, string sub, out TextBlock value)
        {
            value = new TextBlock { Text = "0", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Pal.Br(Pal.Text) };
            var body = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = caption, Foreground = Pal.Br(Pal.Muted) },
                    value,
                    new TextBlock { Text = sub, Foreground = Pal.Br(Pal.Muted), FontSize = 12 },
                }
            };
            var card = new Border
            {
                Background = Pal.Br(Pal.Surface), BorderBrush = Pal.Br(Pal.Line), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 8, 8),
                MinWidth = 170, Child = body,
            };
            return card;
        }

        // the count in the kind's color when it is not zero
        void SetErrTile(TextBlock v, long n, Color tint)
        {
            string s = n.ToString("N0");
            if (v.Text != s) v.Text = s;
            var fore = Pal.Br(n > 0 ? tint : Pal.Text);
            if (v.Foreground != fore) v.Foreground = fore;
        }

        // one color per kind of error (the same in the tiles, the summary and the list)
        static Color ErrKindColor(string kind)
        {
            switch (kind)
            {
                case "address NACK": return Pal.Fg(200, 30, 30);
                case "data NACK": return Pal.Fg(200, 100, 0);
                case "cut off": return Pal.Fg(110, 60, 190);
                case "bytes lost": return Pal.Fg(205, 60, 120);
                case "training failed": return Pal.Fg(20, 85, 165);
                case "bus stuck": return Pal.Fg(140, 40, 150);
                case "FIFO full": return Pal.Fg(0, 121, 128);
                default: return Pal.Fg(90, 90, 90);
            }
        }

        // kind of an error group: "address NACK", "data NACK", "cut off", "bus stuck", "FIFO full", "error"
        static string ErrKind(Group g, out string nackDev)
        {
            nackDev = "";
            if (g.IsEvent)
            {
                string et = g.EventText.Length > 0 ? g.EventText : g.Rows.Count > 0 ? g.Rows[0].Text : "";
                if (et.StartsWith("PC:")) return "PC note";          // seen by the PC, not by the FPGA (UART errors, resync)
                return g.EventCode == 0x07 ? "bus stuck" : g.EventCode == 0xFF ? "FIFO full" : "error event";
            }
            if (g.Status == "cut off") return "cut off";
            if (g.Status.StartsWith("bytes lost")) return "bytes lost";
            if (g.Status != "NACK") return g.Training ? "training failed" : "failed";
            bool inSeg = false, read = false, data = false;
            foreach (string t in g.Sig.Split(' '))
            {
                if (t == "S") { inSeg = false; continue; }
                if (t.Length == 3 && t[2] == 'R' || t.Length == 3 && t[2] == 'W' || t.Length == 4 && t[3] == '-' && (t[2] == 'R' || t[2] == 'W'))
                {
                    inSeg = true; read = t[2] == 'R';
                    if (t.Length == 4) { nackDev = t.Substring(0, 2); return "address NACK"; }
                    continue;
                }
                if (inSeg && !read && t.Length == 3 && t[2] == '-') data = true;
            }
            return data ? "data NACK" : g.Cut ? "cut off" : "NACK";
        }

        class ErrAgg { public string Dev, Addr, Kind; public int Count; public long FirstUs, LastUs; public int NearSame, NearCode = -1; }

        void RebuildErr()
        {
            errVer = dec.Version;
            var st = dec.St;
            SetErrTile(errTileAddr, st.AddrNack, ErrKindColor("address NACK"));
            SetErrTile(errTileData, st.DataNack, ErrKindColor("data NACK"));
            SetErrTile(errTileCut, st.Cut, ErrKindColor("cut off"));
            long lost = st.Lost + st.FifoLost;
            SetErrTile(errTileLost, lost, ErrKindColor("bytes lost"));

            var agg = new Dictionary<string, ErrAgg>();
            var rows = new List<TRow>();
            Group prevXfer = null, lastState = null;
            var sigCount = new Dictionary<string, int>();
            foreach (var g in dec.Groups)
            {
                if (g.IsEvent)
                {
                    int c = g.EventCode;
                    if (c >= 0x01 && c <= 0x06) lastState = g;
                    else if (c == 0x00) lastState = null;                // the log restarted: the time starts again
                }
                bool err = g.Error || g.Cut && g.IsTransaction;
                if (err)
                {
                    string nd;
                    string kind = ErrKind(g, out nd);
                    string dPrev = "", tPrev = "", tState = "", dState = "";
                    if (prevXfer != null && g.Us >= 0 && g.Us >= prevXfer.Us) { dPrev = FmtDelta(g.Us - prevXfer.Us); tPrev = ErrBrief(prevXfer); }
                    int nearCode = -1;
                    if (lastState != null && g.Us >= 0 && g.Us - lastState.Us >= 0)
                    {
                        tState = Decoder.EventName(lastState.EventCode);
                        dState = FmtDelta(g.Us - lastState.Us);
                        if (g.Us - lastState.Us < 5000000) nearCode = lastState.EventCode;     // the summary note: only changes right before
                    }
                    else tState = "-";
                    int rep;
                    sigCount.TryGetValue(g.Sig, out rep);
                    sigCount[g.Sig] = ++rep;
                    string xf = g.IsEvent ? (g.EventText.Length > 0 ? g.EventText : Decoder.EventName(g.EventCode))
                              : ErrXfer(g);
                    string dev0 = g.DevLabel.Length > 0 ? g.DevLabel : (g.IsEvent ? "EVENT" : "I2C");
                    string addr0 = g.IsEvent ? "" : nd.Length > 0 ? nd : g.Addr >= 0 ? Decoder.AddrText(g) : "";
                    rows.Add(new TRow
                    {
                        Cells = new[] { dev0, addr0, kind, ShortTime(g.Us), xf, dPrev, tPrev, tState, dState, rep > 1 ? rep.ToString() : "" },
                        Fore = ErrKindColor(kind), Tag = g.Id,
                    });

                    string dev = dev0;
                    string addr = addr0;
                    string key = dev + "|" + addr + "|" + kind;
                    ErrAgg a;
                    if (!agg.TryGetValue(key, out a)) agg[key] = a = new ErrAgg { Dev = dev, Addr = addr, Kind = kind, FirstUs = g.Us };
                    a.Count++;
                    a.LastUs = g.Us;
                    if (nearCode >= 0) { if (a.NearCode < 0 || a.NearCode == nearCode) { a.NearCode = nearCode; a.NearSame++; } else a.NearCode = -2; }
                }
                if (g.IsTransaction) prevXfer = g;
            }

            var list = new List<ErrAgg>(agg.Values);
            list.Sort(delegate (ErrAgg x, ErrAgg y) { return y.Count.CompareTo(x.Count); });
            var aggRows = new List<TRow>();
            foreach (var a in list)
            {
                string note = a.NearCode >= 0 && a.NearSame == a.Count ? "always within 5 s after: " + Decoder.EventName(a.NearCode) : "";
                aggRows.Add(new TRow
                {
                    Cells = new[] { a.Dev, a.Addr, a.Kind, a.Count.ToString("N0"), ShortTime(a.FirstUs), ShortTime(a.LastUs), note },
                    Fore = ErrKindColor(a.Kind),
                });
            }
            errAgg.SetRows(aggRows);

            // newest last, at most 5000 rows
            int skip = Math.Max(0, rows.Count - 5000);
            errList.SetRows(rows.GetRange(skip, rows.Count - skip));
            if (rows.Count > skip) errList.ScrollToEnd();
            errListLabel.Text = "Every error, with what happened just before it (double click: show it in the LOG)" +
                                (skip > 0 ? string.Format("  -  last {0:N0} of {1:N0}", rows.Count - skip, rows.Count) : "");
            tabErrors.Header = rows.Count > 0 ? string.Format("ERRORS ({0:N0})", rows.Count) : "ERRORS";
        }

        // "<device> <RD|WR> <address> x<bytes>": the device first (SCDC, HDCP, I2C), the direction the same everywhere
        static string ErrXfer(Group g)
        {
            if (g.Kind == "I2C") return "I2C".PadRight(4) + " " + g.I2cText;
            return (g.DevLabel.Length > 0 ? g.DevLabel : "I2C").PadRight(4) + " " + g.Kind + " " + Decoder.AddrText(g) + " x" + g.Len;
        }

        static string ErrBrief(Group g)
        {
            return ErrXfer(g) + " " + g.Status;
        }

        // the status bar counters: a click shows the errors in detail
        Control ErrClick(Control cell)
        {
            cell.Cursor = new Cursor(StandardCursorType.Hand);
            cell.PointerPressed += (s, e) => ShowErrors();
            return cell;
        }

        void ShowErrors()
        {
            tabs.SelectedItem = tabData;
            dataTabs.SelectedItem = tabErrors;
            if (dec.Version != errVer) RebuildErr();
        }
    }
}
