using System;
using System.IO;
using System.Linq;

namespace MLTrading
{
    public class LoggerService
    {
        private readonly string _logFilePath;
        private readonly string _logDirectory;
        private readonly TimeZoneInfo _easternZone;

        public LoggerService(string logDirectory, TimeZoneInfo easternZone)
        {
            _logDirectory = logDirectory;
            _easternZone = easternZone;
            Directory.CreateDirectory(_logDirectory);
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternZone);
            _logFilePath = Path.Combine(_logDirectory, $"trading_log_{nowEt:yyyy-MM-dd_HH-mm-ss}.log");
        }

        public void Log(string message)
        {
            var timestampedMessage = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}: {message}";
            Console.WriteLine(timestampedMessage);
            File.AppendAllText(_logFilePath, timestampedMessage + Environment.NewLine);
        }

        public void ConsolidateLogsForDay()
        {
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternZone);
            var dateString = nowEt.ToString("yyyy-MM-dd");
            var consolidatedFilePath = Path.Combine(_logDirectory, $"trading_log_{dateString}_consolidated.log");

            var filesToConsolidate = Directory.GetFiles(_logDirectory, $"trading_log_{dateString}_*.log");
            if (filesToConsolidate.Length == 0) return;

            Log($"\nConsolidating {filesToConsolidate.Length} log file(s) for {dateString}...");
            using (var destStream = File.Create(consolidatedFilePath))
            {
                foreach (var filePath in filesToConsolidate.OrderBy(f => f))
                {
                    using (var sourceStream = File.OpenRead(filePath))
                    {
                        sourceStream.CopyTo(destStream);
                    }
                    var separator = $"\n--- End of {Path.GetFileName(filePath)} ---\n";
                    var separatorBytes = System.Text.Encoding.UTF8.GetBytes(separator);
                    destStream.Write(separatorBytes, 0, separatorBytes.Length);
                }
            }

            foreach (var filePath in filesToConsolidate)
            {
                File.Delete(filePath);
            }

            Log($"Consolidation complete. Final log saved to: {consolidatedFilePath}");
        }
    }
}