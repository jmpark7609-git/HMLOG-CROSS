using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HMLOG.App
{
    // Material Symbols Rounded (subset font Assets/MaterialSymbols.ttf, Apache 2.0): the glyphs of the header buttons
    static class Icons
    {
        public static readonly FontFamily Font = new FontFamily("avares://HMLOG.App/Assets/MaterialSymbols.ttf#Material Symbols Rounded");
        public const string Refresh = "", Usb = "", UsbOff = "", Open = "", Save = "", Export = "",
                            Delete = "", Download = "", Sun = "", Moon = "", Find = "", More = "";

        // the glyph as a text block (size in px)
        public const string Up = "", Down = "", Doc = "", View = "", Filter = "", Rows = "",
                            Sweep = "", Warn = "", Port = "";

        public static TextBlock Glyph(string g, double size = 17)
        {
            return new TextBlock { Text = g, FontFamily = Font, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        }
    }
}

namespace HMLOG.App
{
    partial class MainWindow
    {
        // an icon in front of a group of controls (the tool tip says what the group does)
        Control IconCap(string glyph, string tip)
        {
            var t = Icons.Glyph(glyph, 18);
            t.Foreground = Pal.Br(Pal.Muted);
            t.Margin = new Avalonia.Thickness(8, 0, 2, 0);
            ToolTip.SetTip(t, tip);
            return t;
        }
    }
}
