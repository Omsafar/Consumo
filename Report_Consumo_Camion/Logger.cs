using System;
using System.IO;

namespace CamionReportGPT
{
    internal static class Logger
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");

        public static void Log(Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {ex}\n");
            }
            catch
            {
                // Ignored
            }
        }
    }
}