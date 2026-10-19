using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace HMLOG.App
{
    // one toggle of a joined group (ToggleGroup): text with an optional color dot; on = tinted face and full-strength text,
    // off = plain face and dim text. The group draws the outline and the dividers.
    class ChipCtl : Border
    {
        readonly Color? dotColor;
        readonly Ellipse dot;
        readonly TextBlock label;
        bool on;
        public event Action<bool> Changed;

        public ChipCtl(string text, Color? dotColor)
        {
            this.dotColor = dotColor;
            Padding = new Thickness(10, 0);
            Height = 26;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Cursor = new Cursor(StandardCursorType.Hand);
            dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = dotColor.HasValue };
            label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(dot); sp.Children.Add(label);
            Child = sp;
            Apply();
        }

        public bool? IsChecked
        {
            get { return on; }
            set { bool v = value == true; if (v == on) return; on = v; Apply(); if (Changed != null) Changed(on); }
        }

        void Apply()
        {
            Background = on ? Pal.Br(Pal.Sel) : Brushes.Transparent;
            label.Foreground = Pal.Br(on ? Pal.Text : Pal.Faint);
            if (dotColor.HasValue) { dot.Fill = Pal.Br(dotColor.Value); dot.Opacity = on ? 1 : 0.35; }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) IsChecked = !on;
        }
    }

    // the toggles side by side in one outline (every toggle as wide as the widest), thin dividers between them
    class ToggleGroup : Border
    {
        public ToggleGroup(params ChipCtl[] items)
        {
            BorderBrush = Pal.Br(Pal.Line);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Background = Pal.Br(Pal.Surface);
            ClipToBounds = true;
            VerticalAlignment = VerticalAlignment.Center;
            var grid = new UniformGrid { Rows = 1 };
            for (int i = 0; i < items.Length; i++)
            {
                items[i].BorderBrush = Pal.Br(Pal.Line);
                items[i].BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
                items[i].HorizontalAlignment = HorizontalAlignment.Stretch;
                grid.Children.Add(items[i]);
            }
            Child = grid;
        }
    }
}
