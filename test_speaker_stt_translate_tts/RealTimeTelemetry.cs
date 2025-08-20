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
