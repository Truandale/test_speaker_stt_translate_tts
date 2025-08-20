using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    public static class RealTimeTelemetry
    {
        private static long _bytesProcessed, _samplesProcessed;
        private static long _whisperCalls, _sttLatencyMs;
        private static long _bufferAllocations, _bufferReturns;
        private static readonly Stopwatch _uptime = Stopwatch.StartNew();
        
        public static void RecordBytesProcessed(long bytes) => Interlocked.Add(ref _bytesProcessed, bytes);
        public static void RecordSamplesProcessed(int samples) => Interlocked.Add(ref _samplesProcessed, samples);
        public static void RecordWhisperCall(long latencyMs)
        {
            Interlocked.Increment(ref _whisperCalls);
            Interlocked.Add(ref _sttLatencyMs, latencyMs);
        }
        
        public static void RecordBufferAllocation() => Interlocked.Increment(ref _bufferAllocations);
        public static void RecordBufferReturn() => Interlocked.Increment(ref _bufferReturns);
        
        public static string FormatStats()
        {
            var avgLatency = _whisperCalls > 0 ? _sttLatencyMs / _whisperCalls : 0;
            var mbProcessed = _bytesProcessed / (1024.0 * 1024.0);
            var uptime = _uptime.Elapsed.TotalMinutes;
            var bufferLeaks = _bufferAllocations - _bufferReturns;
            
            return $"RT: {mbProcessed:F1}MB, {_samplesProcessed}smp, STT:{_whisperCalls}({avgLatency}ms), Buf:{bufferLeaks}leak, Up:{uptime:F1}m";
        }
        
        public static void Reset()
        {
            _bytesProcessed = _samplesProcessed = _whisperCalls = _sttLatencyMs = 0;
            _bufferAllocations = _bufferReturns = 0;
        }
    }
}
