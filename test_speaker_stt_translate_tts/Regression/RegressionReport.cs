using System.Text.Json;

namespace test_speaker_stt_translate_tts.Regression
{
    public sealed class RegressionReport
    {
        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
        public DateTime FinishedAtUtc { get; set; }
        public string Machine { get; init; } = Environment.MachineName;
        public string Os { get; init; } = Environment.OSVersion.ToString();
        public string Commit { get; init; } = GetEnv("GIT_COMMIT") ?? "unknown";

        // Метрики (заполняем из RealTimeTelemetry)
        public double LatencyE2eP95Ms { get; set; }
        public double LatencyE2eP99Ms { get; set; }
        public double LagSttP95Ms { get; set; }
        public long CaptureDropped { get; set; }
        public long NormalizeDropped { get; set; }
        public long SttDropped { get; set; }
        public long RestartCount { get; set; }
        public long RestartErrors { get; set; }
        public double CpuAvgPercent { get; set; }
        public double WorkingSetMb { get; set; }
        public double Gen0PerMin { get; set; }
        public double Gen1PerMin { get; set; }
        public double Gen2PerMin { get; set; }
        public int ErrorLogPer10Min { get; set; }

        public required SloConfig Slo { get; init; }
        public bool Passed { get; set; }
        public string[] FailReasons { get; set; } = Array.Empty<string>();
        public string ReportPath { get; set; } = "";

        public static string Save(RegressionReport r)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"regression_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(r, new JsonSerializerOptions{ WriteIndented = true }));
            return r.ReportPath = path;
        }

        static string? GetEnv(string k) => Environment.GetEnvironmentVariable(k);
    }
}