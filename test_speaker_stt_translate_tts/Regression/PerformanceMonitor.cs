using System.Diagnostics;

namespace test_speaker_stt_translate_tts.Regression
{
    public static class PerformanceMonitor
    {
        public static double TryReadProcessCpuPercent(TimeSpan window)
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                var t0 = p.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                Thread.Sleep(window);
                p.Refresh();
                var cpu = (p.TotalProcessorTime - t0).TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                return Math.Max(0, Math.Min(100, cpu));
            }
            catch { return 0; }
        }

        public static double Gen0RatePerMin() => GenPerMin(0);
        public static double Gen1RatePerMin() => GenPerMin(1);
        public static double Gen2RatePerMin() => GenPerMin(2);

        static double GenPerMin(int gen)
        {
            var a = GC.CollectionCount(gen);
            Thread.Sleep(1000);
            var b = GC.CollectionCount(gen);
            return (b - a) * 60.0;
        }
    }
}