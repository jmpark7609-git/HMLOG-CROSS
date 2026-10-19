using Avalonia;
using System;
using System.IO;
using System.Text;

namespace HMLOG.App
{
    static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // HMLOG --decode raw.log out.txt : decode without a window (text of the "Save decoded" button)
            if (args.Length >= 3 && args[0] == "--decode")
            {
                var d = new Decoder();
                foreach (string l in File.ReadLines(args[1], Encoding.Latin1)) d.Feed(l);
                d.Finish();
                File.WriteAllLines(args[2], d.Lines);
                return 0;
            }
            try
            {
                AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // an unexpected error is written to crash.log in the settings folder, not lost
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    File.AppendAllText(Path.Combine(Settings.Dir, "crash.log"), DateTime.Now.ToString("s") + "\n" + ex + "\n\n");
                }
                catch (Exception) { }
                throw;
            }
            return 0;
        }
    }
}
