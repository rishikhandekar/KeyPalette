using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KeyPalette
{
    public class ColorPreset
    {
        public string Name { get; set; } = "";
        public List<string> HexColors { get; set; } = new();
        public double SpeedMs { get; set; } = 150;
        public double BrightnessPercent { get; set; } = 100;
    }

    /// <summary>
    /// Simple JSON-backed store for saved color sequences, kept in the user's
    /// AppData folder so presets survive rebuilds and reinstalls of the app.
    /// </summary>
    public static class PresetStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KeyPalette", "presets.json");

        public static List<ColorPreset> LoadAll()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<ColorPreset>();
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<ColorPreset>>(json) ?? new List<ColorPreset>();
            }
            catch
            {
                return new List<ColorPreset>();
            }
        }

        public static void SaveAll(List<ColorPreset> presets)
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
