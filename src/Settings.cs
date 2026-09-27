using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AutoMute
{
    /// <summary>User settings. Treated as immutable once handed to the engine:
    /// changes are made on a Clone() and the new object is swapped in.</summary>
    public sealed class Settings
    {
        public bool Enabled = true;
        public double ThresholdDb = -13.0;
        public int WindowMs = 0;
        public double MuteSeconds = 2.0;
        public bool ExtendWhileLoud = true;
        public bool FreqEnabled = false;
        public int FreqCutoffHz = 3000;
        public double FreqThresholdDb = -30.0;

        private static string path;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }

        /// <summary>AutoMute.ini next to the exe (portable). Falls back to
        /// %APPDATA%\AutoMute if the exe's folder isn't writable.</summary>
        public static string FilePath
        {
            get
            {
                if (path == null) path = ResolvePath();
                return path;
            }
        }

        private static string ResolvePath()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string local = Path.Combine(exeDir, "AutoMute.ini");
            if (File.Exists(local) || CanWrite(exeDir)) return local;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoMute");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "AutoMute.ini");
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string test = Path.Combine(dir, ".automute_write_test");
                File.WriteAllText(test, "");
                File.Delete(test);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "enabled": s.Enabled = ParseBool(val, s.Enabled); break;
                        case "thresholddb": s.ThresholdDb = ParseDouble(val, s.ThresholdDb, -100, 0); break;
                        case "windowms": s.WindowMs = (int)ParseDouble(val, s.WindowMs, 0, 5000); break;
                        case "muteseconds": s.MuteSeconds = ParseDouble(val, s.MuteSeconds, 0.1, 3600); break;
                        case "extendwhileloud": s.ExtendWhileLoud = ParseBool(val, s.ExtendWhileLoud); break;
                        case "freqenabled": s.FreqEnabled = ParseBool(val, s.FreqEnabled); break;
                        case "freqcutoffhz": s.FreqCutoffHz = (int)ParseDouble(val, s.FreqCutoffHz, 20, 24000); break;
                        case "freqthresholddb": s.FreqThresholdDb = ParseDouble(val, s.FreqThresholdDb, -120, 0); break;
                    }
                }
            }
            catch
            {
                // unreadable file -> defaults
            }
            return s;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; AutoMute settings - edited automatically by the app.");
                sb.AppendLine("[AutoMute]");
                sb.AppendLine("Enabled=" + Enabled);
                sb.AppendLine("ThresholdDb=" + ThresholdDb.ToString("0.0", Inv));
                sb.AppendLine("WindowMs=" + WindowMs.ToString(Inv));
                sb.AppendLine("MuteSeconds=" + MuteSeconds.ToString("0.0", Inv));
                sb.AppendLine("ExtendWhileLoud=" + ExtendWhileLoud);
                sb.AppendLine("FreqEnabled=" + FreqEnabled);
                sb.AppendLine("FreqCutoffHz=" + FreqCutoffHz.ToString(Inv));
                sb.AppendLine("FreqThresholdDb=" + FreqThresholdDb.ToString("0.0", Inv));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch
            {
                // read-only location etc. - settings just won't persist
            }
        }

        private static bool ParseBool(string v, bool fallback)
        {
            bool b;
            return bool.TryParse(v, out b) ? b : fallback;
        }

        private static double ParseDouble(string v, double fallback, double min, double max)
        {
            double d;
            if (!double.TryParse(v, NumberStyles.Float, Inv, out d)) return fallback;
            return Math.Max(min, Math.Min(max, d));
        }
    }
}
