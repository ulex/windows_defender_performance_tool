using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Processes ETL files headlessly and exports a summary CSV.
/// </summary>
internal static class EtlBatchExporter
{
    public sealed class EtlSummary
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public double TotalScannedMs { get; set; }
        public int MatchedEvents { get; set; }
        public string TopProcess { get; set; } = "";
        public double TopProcessMs { get; set; }
        public string TopFile { get; set; } = "";
        public double TopFileMs { get; set; }
    }

    /// <summary>
    /// Processes a single ETL file synchronously on a background thread and returns its summary.
    /// </summary>
    public static Task<EtlSummary> ProcessFileAsync(string filePath)
    {
        return Task.Run(() =>
        {
            var summary = new EtlSummary
            {
                FileName = Path.GetFileName(filePath),
                FullPath = filePath
            };

            var processTotals = new Dictionary<string, double>();
            var fileTotals = new Dictionary<string, double>();
            double totalMs = 0;
            int matchedCount = 0;

            using var done = new ManualResetEventSlim(false);

            var listener = new EtwListener(filePath);
            listener.Events.Subscribe(
                info =>
                {
                    totalMs += info.DurationMsec;
                    matchedCount++;

                    if (!string.IsNullOrEmpty(info.Process))
                    {
                        processTotals.TryGetValue(info.Process, out var existing);
                        processTotals[info.Process] = existing + info.DurationMsec;
                    }

                    if (!string.IsNullOrEmpty(info.FilePath))
                    {
                        fileTotals.TryGetValue(info.FilePath, out var existing);
                        fileTotals[info.FilePath] = existing + info.DurationMsec;
                    }
                },
                _ => done.Set(),
                () => done.Set());

            listener.Start();
            done.Wait();
            listener.Dispose();

            summary.TotalScannedMs = totalMs;
            summary.MatchedEvents = matchedCount;

            if (processTotals.Count > 0)
            {
                var top = processTotals.OrderByDescending(kvp => kvp.Value).First();
                summary.TopProcess = top.Key;
                summary.TopProcessMs = top.Value;
            }

            if (fileTotals.Count > 0)
            {
                var top = fileTotals.OrderByDescending(kvp => kvp.Value).First();
                summary.TopFile = top.Key;
                summary.TopFileMs = top.Value;
            }

            return summary;
        });
    }

    /// <summary>
    /// Processes multiple ETL files and writes a summary CSV.
    /// </summary>
    public static async Task ExportCsvAsync(string[] etlFiles, string outputPath,
        IProgress<(int completed, int total)>? progress = null)
    {
        var summaries = new List<EtlSummary>();

        for (int i = 0; i < etlFiles.Length; i++)
        {
            summaries.Add(await ProcessFileAsync(etlFiles[i]));
            progress?.Report((i + 1, etlFiles.Length));
        }

        WriteCsv(summaries, outputPath);
    }

    private static void WriteCsv(List<EtlSummary> summaries, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FileName;TotalScannedSeconds;MatchedEvents;TopProcess;TopProcessSeconds;TopFile;TopFileSeconds");

        foreach (var s in summaries)
        {
            sb.AppendLine(string.Join(";",
                CsvEscape(s.FileName),
                (s.TotalScannedMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture),
                s.MatchedEvents.ToString(CultureInfo.InvariantCulture),
                CsvEscape(s.TopProcess),
                (s.TopProcessMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture),
                CsvEscape(s.TopFile),
                (s.TopFileMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(";") || value.Contains("\"") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
