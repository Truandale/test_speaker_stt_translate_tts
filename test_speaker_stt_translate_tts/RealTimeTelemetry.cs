using System.Diagnostics;
using System.Threading;

namespace test_speaker_stt_translate_tts
{
    public static class RealTimeTelemetry
    {
        // Core metrics
        private static long _bytesProcessed, _samplesProcessed;
        private static long _whisperCalls, _sttLatencyMs;
        private static long _bufferAllocations, _bufferReturns;
        private static long _captureDropped, _normalizationDropped, _sttDropped;
        private static long _captureStart, _captureEnd;
        private static readonly Stopwatch _uptime = Stopwatch.StartNew();
        
        // Pipeline lag metrics
        private static long _captureLagMs, _normalizationLagMs, _sttLagMs;
        private static long _captureLagSamples, _normalizationLagSamples, _sttLagSamples;
        
        // Buffer drop tracking for DropOldest semantics
        private static long _bufferDropped = 0;
        private static long _restartCount = 0;
        private static long _restartErrors = 0;
        
        // 🚀 NEW: Rolling metrics for P95/P99 tracking
        private static readonly object _latLock = new();
        private static readonly double[] _e2eBuf = new double[4096];
        private static int _e2eIdx;
        private static readonly object _lagLock = new();
        private static readonly double[] _lagBuf = new double[4096];
        private static int _lagIdx;
        
        // Core processing metrics
        public static void RecordBytesProcessed(long bytes) => Interlocked.Add(ref _bytesProcessed, bytes);
        public static void RecordSamplesProcessed(int samples) => Interlocked.Add(ref _samplesProcessed, samples);
        
        // STT performance
        public static void RecordWhisperCall() => Interlocked.Increment(ref _whisperCalls);
        public static void RecordWhisperLatency(long latencyMs) => Interlocked.Add(ref _sttLatencyMs, latencyMs);
        
        // Pipeline lag tracking
        public static void RecordCaptureLag(long lagMs) 
        { 
            Interlocked.Add(ref _captureLagMs, lagMs); 
            Interlocked.Increment(ref _captureLagSamples);
        }
        
        public static void RecordNormalizationLag(long lagMs) 
        { 
            Interlocked.Add(ref _normalizationLagMs, lagMs); 
            Interlocked.Increment(ref _normalizationLagSamples);
        }
        
        public static void RecordSttLag(long lagMs) 
        { 
            Interlocked.Add(ref _sttLagMs, lagMs); 
            Interlocked.Increment(ref _sttLagSamples);
        }
        
        // Buffer drop tracking
        public static void RecordBufferDrop() => Interlocked.Increment(ref _bufferDropped);
        
        // 🚀 NEW: E2E latency and STT lag tracking for SLO monitoring
        public static void ObserveE2eLatencyMs(double latencyMs)
        {
            lock (_latLock) _e2eBuf[_e2eIdx++ & (_e2eBuf.Length - 1)] = latencyMs;
        }
        
        public static void ObserveSttLagMs(double lagMs)
        {
            lock (_lagLock) _lagBuf[_lagIdx++ & (_lagBuf.Length - 1)] = lagMs;
        }
        
        public static (double p95, double p99) SnapshotE2e()
        {
            lock (_latLock)
            {
                return Percentiles(_e2eBuf);
            }
        }
        
        public static double SnapshotSttLag()
        {
            lock (_lagLock)
            {
                return Percentiles(_lagBuf).p95;
            }
        }
        
        private static (double p95, double p99) Percentiles(double[] src)
        {
            var copy = new double[src.Length];
            Buffer.BlockCopy(src, 0, copy, 0, sizeof(double) * src.Length);
            Array.Sort(copy);
            double P(int p) => copy[(int)Math.Clamp((copy.Length * p / 100.0) - 1, 0, copy.Length - 1)];
            return (P(95), P(99));
        }
        
        // Restart tracking
        public static void RecordRestart() => Interlocked.Increment(ref _restartCount);
        public static void RecordRestartError() => Interlocked.Increment(ref _restartErrors);
        
        // Buffer management
        public static void RecordBufferAllocation() => Interlocked.Increment(ref _bufferAllocations);
        public static void RecordBufferReturn() => Interlocked.Increment(ref _bufferReturns);
        
        // Capture lifecycle  
        public static void RecordCaptureStart() => Interlocked.Increment(ref _captureStart);
        public static void RecordCaptureEnd() => Interlocked.Increment(ref _captureEnd);
        
        // Drop counters for monitoring pipeline health
        public static void RecordCaptureDrop() => Interlocked.Increment(ref _captureDropped);
        public static void RecordNormalizationDrop() => Interlocked.Increment(ref _normalizationDropped);
        public static void RecordSttDrop() => Interlocked.Increment(ref _sttDropped);
        
        // 🚀 NEW: Getters for SLO regression testing
        public static long GetCaptureDropped() => Interlocked.Read(ref _captureDropped);
        public static long GetNormalizationDropped() => Interlocked.Read(ref _normalizationDropped);
        public static long GetSttDropped() => Interlocked.Read(ref _sttDropped);
        public static long GetRestartCount() => Interlocked.Read(ref _restartCount);
        public static long GetRestartErrors() => Interlocked.Read(ref _restartErrors);
        
        public static void ResetDropCounters()
        {
            Interlocked.Exchange(ref _captureDropped, 0);
            Interlocked.Exchange(ref _normalizationDropped, 0);
            Interlocked.Exchange(ref _sttDropped, 0);
            Interlocked.Exchange(ref _restartCount, 0);
            Interlocked.Exchange(ref _restartErrors, 0);
        }
        
        public static string FormatStats()
        {
            var avgLatency = _whisperCalls > 0 ? _sttLatencyMs / _whisperCalls : 0;
            var mbProcessed = _bytesProcessed / (1024.0 * 1024.0);
            var uptime = _uptime.Elapsed.TotalMinutes;
            var bufferLeaks = _bufferAllocations - _bufferReturns;
            var totalDrops = _captureDropped + _normalizationDropped + _sttDropped + _bufferDropped;
            
            // Pipeline lag averages
            var capLagAvg = _captureLagSamples > 0 ? _captureLagMs / _captureLagSamples : 0;
            var normLagAvg = _normalizationLagSamples > 0 ? _normalizationLagMs / _normalizationLagSamples : 0;
            var sttLagAvg = _sttLagSamples > 0 ? _sttLagMs / _sttLagSamples : 0;
            
            return $"🚀 PRODUCTION METRICS v2.1.6: {mbProcessed:F1}MB, {_samplesProcessed}smp, STT:{_whisperCalls}({avgLatency}ms), Drops:{totalDrops}({_bufferDropped}buf), Lags:C{capLagAvg}|N{normLagAvg}|S{sttLagAvg}ms, Buf:{bufferLeaks}leak, Up:{uptime:F1}m";
        }
        
        public static void Reset()
        {
            _bytesProcessed = _samplesProcessed = _whisperCalls = _sttLatencyMs = 0;
            _bufferAllocations = _bufferReturns = 0;
            _captureDropped = _normalizationDropped = _sttDropped = _bufferDropped = 0;
            _captureStart = _captureEnd = 0;
            // Reset lag metrics
            _captureLagMs = _normalizationLagMs = _sttLagMs = 0;
            _captureLagSamples = _normalizationLagSamples = _sttLagSamples = 0;
        }
    }
}
