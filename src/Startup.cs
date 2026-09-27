using System;
using Microsoft.Win32;
using System.Windows.Forms;

namespace AutoMute
{
    /// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AutoMute";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" /minimized"; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, Command);
                else k.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Portable exe may have been moved: keep the Run entry pointing at us.</summary>
        public static void RepairPathIfMoved()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    string current = k.GetValue(ValueName) as string;
                    if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                        k.SetValue(ValueName, Command);
                }
            }
            catch
            {
            }
        }
    }
}
