using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FNBoost.Core
{
    public static class Log
    {
        private static readonly object Gate = new();

        /// <summary>Notifica la UI (barra di stato) di un nuovo messaggio.</summary>
        public static event Action<string>? MessageLogged;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            lock (Gate)
            {
                try
                {
                    AppPaths.Ensure();
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        File.Copy(AppPaths.LogFile, AppPaths.LogFile + ".old", true);
                        File.WriteAllText(AppPaths.LogFile, string.Empty);
                    }
                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
                }
                catch
                {
                    // Il log non deve mai far crashare l'app.
                }
            }
            MessageLogged?.Invoke(message);
        }

        public static IReadOnlyList<string> Tail(int lines)
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(AppPaths.LogFile)) return Array.Empty<string>();
                    return File.ReadAllLines(AppPaths.LogFile).Reverse().Take(lines).Reverse().ToList();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }
        }
    }
}
