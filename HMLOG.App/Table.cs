using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HMLOG.App
{
    // one row of a Table. Colors are in light-theme values (Pal converts them for the dark theme).
    class TRow
    {
        public string[] Cells;
        public Color? Fore, Back;                 // row colors (null = theme text / none)
        public Color?[] CellFore;                 // per cell
        public Dictionary<int, Color[]> Badges;   // cell -> { fore, back }: drawn as a badge
        public List<Seg> Segs;                    // the last cell as colored segments (changed values)
        public int SegCol = -1;
        public Color SegFore;
        public bool Span;                         // one text over all columns (bands, folded rows)
        public bool Bold;
        public int Indent;                        // span: extra left space (px)
        public object Tag;
        public bool Hit;                          // search hit (yellow)
        public int ChipCol = -1;                  // cell drawn as chips (HDCP steps)
        public List<Chip> Chips;
    }

    // one chip of a chip cell: text, colors of the current theme, an arrow after it
    class Chip
    {
        public string Text;
        public Color Fore, Back;
        public bool Arrow = true;
    }

    // header + virtual list of TRow (the list is the ItemsControl of Avalonia: only visible rows are built)
    class Table : DockPanel
    {
        public static readonly FontFamily Mono = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, Liberation Mono, monospace");
        public const double FontSize = 14;
        public const double RowH = 26;

        readonly string[] titles;
        readonly double[] widths;                 // last column takes the rest
        readonly Grid head;
        public readonly ListBox List;
        ObservableCollection<TRow> rows = new ObservableCollection<TRow>();
        ScrollViewer sv;

        public event Action SelectionChanged;

        public Table(params object[] cols)
        {
            int n = cols.Length / 2;
            titles = new string[n];
            widths = new double[n];
            for (int i = 0; i < n; i++) { titles[i] = (string)cols[2 * i]; widths[i] = Convert.ToDouble(cols[2 * i + 1]); }

            head = new Grid { Height = 28 };
            BuildColumns(head);
            for (int i = 0; i < n; i++)
            {
                var t = new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold, FontSize = 13.5, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, i);
                head.Children.Add(t);
            }
            var hb = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
            HeadBorder = hb;
            SetDock(hb, Dock.Top);
            Children.Add(hb);

            List = new ListBox { ItemsSource = rows, FontFamily = Mono, FontSize = FontSize, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
            List.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TRow>((r, _) => Build(r));
            List.ContainerPrepared += (s, e) =>
            {
                if (e.Container is ListBoxItem li && li.DataContext is TRow r)
                {
                    li.Background = r.Back.HasValue ? Pal.Br(Pal.Bg(r.Back.Value.R, r.Back.Value.G, r.Back.Value.B)) : null;
                    if (r.Hit) li.Background = Pal.Br(Pal.HitBack);
                }
            };
            List.SelectionChanged += (s, e) => { if (!suppress && SelectionChanged != null) SelectionChanged(); };
            ApplyTheme();
            Children.Add(List);
        }

        Border HeadBorder;

        void BuildColumns(Grid g)
        {
            g.ColumnDefinitions.Clear();
            for (int i = 0; i < widths.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition(i == widths.Length - 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i])));
        }

        // theme change: header / selection colors and the rows are built again
        public void ApplyTheme()
        {
            HeadBorder.Background = Pal.Br(Pal.Header);
            HeadBorder.BorderBrush = Pal.Br(Pal.Line);
            List.Background = Pal.Br(Pal.Surface);
            List.Styles.Clear();
            var item = new Style(x => x.OfType<ListBoxItem>());
            item.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(0)));
            item.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0.0));
            item.Setters.Add(new Setter(Layoutable.HeightProperty, RowH));
            List.Styles.Add(item);
            var s1 = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>());
            s1.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Pal.Br(Pal.Sel)));
            List.Styles.Add(s1);
            var s2 = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":focus").Template().OfType<ContentPresenter>());
            s2.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Pal.Br(Pal.Sel)));
            List.Styles.Add(s2);
            var s3 = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover").Template().OfType<ContentPresenter>());
            s3.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Pal.Br(Pal.Sel)));
            List.Styles.Add(s3);
            Refresh();
        }

        public void Refresh()
        {
            var keep = SelectedIndex;
            List.ItemsSource = null;
            List.ItemsSource = rows;
            if (keep >= 0 && keep < rows.Count) List.SelectedIndex = keep;
        }

        // ------------------------------------------------------------------
        // rows
        // ------------------------------------------------------------------
        public ObservableCollection<TRow> Rows { get { return rows; } }

        // all rows at once (a long list is not added one by one)
        public void SetRows(IEnumerable<TRow> list)
        {
            int keep = List.SelectedIndex;
            suppress = true;
            rows = new ObservableCollection<TRow>(list);
            List.ItemsSource = rows;
            if (keep >= 0 && keep < rows.Count) List.SelectedIndex = keep;
            suppress = false;
        }

        bool suppress;
        public int SelectedIndex { get { return List.SelectedIndex; } set { List.SelectedIndex = value; } }
        public TRow Selected { get { return List.SelectedItem as TRow; } }

        public void ScrollTo(int i)
        {
            if (i >= 0 && i < rows.Count) List.ScrollIntoView(i);
        }

        ScrollViewer Sv()
        {
            if (sv == null) sv = List.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            return sv;
        }

        public bool AtEnd()
        {
            var s = Sv();
            if (s == null || rows.Count == 0) return true;
            return s.Offset.Y + s.Viewport.Height >= s.Extent.Height - 2 * RowH;
        }

        public void ScrollToEnd()
        {
            if (rows.Count > 0) List.ScrollIntoView(rows.Count - 1);
        }

        // ------------------------------------------------------------------
        // row visual
        // ------------------------------------------------------------------
        Control Build(TRow r)
        {
            if (r == null) return new Border();
            var fore = r.Fore.HasValue ? Pal.Br(Pal.Fg(r.Fore.Value.R, r.Fore.Value.G, r.Fore.Value.B)) : Pal.Br(Pal.Text);
            if (r.Span)
            {
                var t = new TextBlock { Text = r.Cells[0], Foreground = fore, VerticalAlignment = VerticalAlignment.Center,
                                        Margin = new Thickness(8 + r.Indent, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                                        FontWeight = r.Bold ? FontWeight.SemiBold : FontWeight.Normal };
                return t;
            }
            for (int q = 0; q < r.Cells.Length; q++) if (r.Cells[q] == null) r.Cells[q] = "";
            var g = new Grid { Height = RowH };
            BuildColumns(g);
            for (int i = 0; i < r.Cells.Length && i < widths.Length; i++)
            {
                Control c;
                if (r.Badges != null && r.Badges.TryGetValue(i, out var bd) && r.Cells[i].Length > 0)
                {
                    c = new Border
                    {
                        Background = Pal.Br(bd[1]), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 0),
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                        Child = new TextBlock { Text = r.Cells[i], Foreground = Pal.Br(bd[0]), FontWeight = FontWeight.SemiBold }
                    };
                }
                else if (r.Chips != null && i == r.ChipCol)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                    foreach (var ch in r.Chips)
                    {
                        sp.Children.Add(new Border
                        {
                            Background = Pal.Br(ch.Back), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 0), Height = RowH - 4,
                            Child = new TextBlock { Text = ch.Text, Foreground = Pal.Br(ch.Fore), VerticalAlignment = VerticalAlignment.Center }
                        });
                        if (ch.Arrow) sp.Children.Add(new TextBlock { Text = "›", Foreground = Pal.Br(Pal.Muted), Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    }
                    c = sp;
                }
                else if (r.Segs != null && i == r.SegCol)
                {
                    var t = new TextBlock { Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Pal.Br(r.SegFore) };
                    foreach (var s in r.Segs)
                    {
                        var run = new Run { Text = s.Text };
                        if (s.Mark == Mark.Changed) { run.Background = Pal.Br(Pal.ChgBack); }
                        else if (s.Mark == Mark.Muted) run.Foreground = Pal.Br(Pal.Muted);
                        t.Inlines.Add(run);
                    }
                    c = t;
                }
                else
                {
                    Color? cf = r.CellFore != null && i < r.CellFore.Length ? r.CellFore[i] : null;
                    IBrush fb = cf.HasValue ? Pal.Br(Pal.Fg(cf.Value.R, cf.Value.G, cf.Value.B)) : fore;
                    c = new TextBlock { Text = r.Cells[i], Foreground = fb, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                                        TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = r.Bold ? FontWeight.SemiBold : FontWeight.Normal };
                }
                Grid.SetColumn(c, i);
                g.Children.Add(c);
            }
            return g;
        }
    }
}
