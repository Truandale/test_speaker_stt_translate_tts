using System.Diagnostics;

namespace test_speaker_stt_translate_tts.Regression
{
    public sealed class RegressionRunner
    {
        readonly SloConfig _slo;
        readonly TimeSpan _duration;
        readonly CancellationToken _ct;

        public RegressionRunner(SloConfig slo, TimeSpan duration, CancellationToken ct)
        {
            _slo = slo;
            _duration = duration;
            _ct = ct;
        }

        public async Task<RegressionReport> RunAsync()
        {
            var rep = new RegressionReport { Slo = _slo };
            ResetCounters();

            // 1) Прогрев: 10-15 секунд
            await PlaySilenceAsync(TimeSpan.FromSeconds(10));

            // 2) Основной прогон: слушаем системный звук (реальный сценарий)
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < _duration && !_ct.IsCancellationRequested)
            {
                await Task.Delay(500, _ct);
            }

            rep.FinishedAtUtc = DateTime.UtcNow;

            // 3) Сбор метрик
            (rep.LatencyE2eP95Ms, rep.LatencyE2eP99Ms) = RealTimeTelemetry.SnapshotE2e();
            rep.LagSttP95Ms = RealTimeTelemetry.SnapshotSttLag();
            rep.CaptureDropped = RealTimeTelemetry.GetCaptureDropped();
            rep.NormalizeDropped = RealTimeTelemetry.GetNormalizationDropped();
            rep.SttDropped = RealTimeTelemetry.GetSttDropped();
            rep.RestartCount = RealTimeTelemetry.GetRestartCount();
            rep.RestartErrors = RealTimeTelemetry.GetRestartErrors();

            var proc = Process.GetCurrentProcess();
            rep.WorkingSetMb = proc.WorkingSet64 / 1024.0 / 1024.0;
            rep.CpuAvgPercent = PerformanceMonitor.TryReadProcessCpuPercent(TimeSpan.FromSeconds(5));

            // Сбор GC-статистики
            rep.Gen0PerMin = PerformanceMonitor.Gen0RatePerMin();
            rep.Gen1PerMin = PerformanceMonitor.Gen1RatePerMin();
            rep.Gen2PerMin = PerformanceMonitor.Gen2RatePerMin();

            // 4) Проверка SLO
            var fails = new List<string>();
            if (rep.LatencyE2eP95Ms > _slo.LatencyE2eP95Ms) 
                fails.Add($"P95 e2e {rep.LatencyE2eP95Ms:F0}ms > {_slo.LatencyE2eP95Ms}ms");
            if (rep.LatencyE2eP99Ms > _slo.LatencyE2eP99Ms) 
                fails.Add($"P99 e2e {rep.LatencyE2eP99Ms:F0}ms > {_slo.LatencyE2eP99Ms}ms");
            if (rep.LagSttP95Ms > _slo.LagSttP95Ms) 
                fails.Add($"P95 STT lag {rep.LagSttP95Ms:F0}ms > {_slo.LagSttP95Ms}ms");
            if (rep.WorkingSetMb > _slo.WorkingSetMb) 
                fails.Add($"WS {rep.WorkingSetMb:F0}MB > {_slo.WorkingSetMb}MB");
            if (rep.CpuAvgPercent > _slo.CpuAvgPercent) 
                fails.Add($"CPU {rep.CpuAvgPercent:F0}% > {_slo.CpuAvgPercent}%");
            if (rep.RestartErrors > 0) 
                fails.Add($"RestartErrors: {rep.RestartErrors}");

            rep.Passed = fails.Count == 0;
            rep.FailReasons = fails.ToArray();
            RegressionReport.Save(rep);
            return rep;
        }

        static Task PlaySilenceAsync(TimeSpan dur) => Task.Delay(dur);

        static void ResetCounters()
        {
            // Сбрасываем счетчики для чистого измерения
            RealTimeTelemetry.ResetDropCounters();
        }
    }
}