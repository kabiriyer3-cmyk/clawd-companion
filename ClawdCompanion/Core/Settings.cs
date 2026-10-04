using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawdCompanion.Core
{
    public sealed class Settings
    {
        public string Size { get; set; } = "Medium";
        public bool WalkAround { get; set; } = true;
        public bool SitOnWindows { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public double? LastX { get; set; }

        [JsonIgnore]
        public double Scale
        {
            get
            {
                if (Size == "Small") return 100;
                if (Size == "Large") return 185;
                return 140;
            }
        }

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClawdCompanion"); }
        }

        static string FilePath { get { return Path.Combine(Folder, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "clawd.log"), DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "ClawdCompanion";

        public static void Apply(bool enabled)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled && !string.IsNullOrEmpty(Environment.ProcessPath))
                        key.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
                    else
                        key.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Settings.Log("AutoStart: " + ex.Message); }
        }
    }
}
