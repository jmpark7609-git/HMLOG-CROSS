using System;
using System.IO;
using System.Text.Json;

namespace HMLOG.App
{
    // Settings kept between runs: JSON file in the user's application-data folder
    // (Windows %APPDATA%\OWM\HMLOG, macOS ~/Library/Application Support/OWM/HMLOG, Linux ~/.config/OWM/HMLOG).
    // The Windows version keeps them in the registry (HKCU\Software\OWM\HMLOG).
    class Settings
    {
        public bool Dark { get; set; }
        public string Port { get; set; } = "";
        public int Baud { get; set; } = 921600;
        public bool AutoBaud { get; set; }
        public bool Reconnect { get; set; } = true;
        public bool AutoSave { get; set; } = true;
        public bool HpdReset { get; set; }
        public bool LinkReset { get; set; }
        public bool PcTime { get; set; }
        public double Width { get; set; } = 1280;
        public double Height { get; set; } = 780;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OWM", "HMLOG"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static string LogDir
        {
            get { return Path.Combine(Dir, "logs"); }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception) { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { }
        }
    }
}
