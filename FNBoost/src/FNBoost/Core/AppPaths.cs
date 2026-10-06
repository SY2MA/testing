using System;
using System.IO;

namespace FNBoost.Core
{
    public static class AppPaths
    {
        public static string Root { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNBoost");

        public static string SettingsFile => Path.Combine(Root, "settings.json");
        public static string BackupFile => Path.Combine(Root, "tweak-backups.json");
        public static string IniBackupDir => Path.Combine(Root, "ini-backups");
        public static string LogDir => Path.Combine(Root, "logs");
        public static string LogFile => Path.Combine(LogDir, "fnboost.log");

        public static void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(IniBackupDir);
            Directory.CreateDirectory(LogDir);
        }

        public static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string ProgramData =>
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        public static string System32 =>
            Environment.GetFolderPath(Environment.SpecialFolder.System);
    }
}
