using System.Text.Json;

namespace test_speaker_stt_translate_tts.Regression
{
    public sealed class SloConfig
    {
        public int LatencyE2eP95Ms { get; init; } = 2300;
        public int LatencyE2eP99Ms { get; init; } = 3500;
        public int LagSttP95Ms     { get; init; } = 1800;
        public int DropsPer10Min   { get; init; } = 5;
        public int CpuAvgPercent   { get; init; } = 50;
        public int WorkingSetMb    { get; init; } = 500;
        public double Gen0PerMin   { get; init; } = 30;
        public double Gen1PerMin   { get; init; } = 2;
        public double Gen2PerMin   { get; init; } = 0.2;
        public int ErrorsPer10Min  { get; init; } = 1;

        public static SloConfig Load(string? path = null)
        {
            try
            {
                path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.slo.json");
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<SloConfig>(File.ReadAllText(path)) ?? new();
            }
            catch { /* fallback */ }
            return new();
        }
    }
}
