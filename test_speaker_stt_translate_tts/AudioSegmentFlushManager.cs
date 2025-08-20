using System.Threading.Channels;

namespace test_speaker_stt_translate_tts
{
    public class AudioSegmentFlushManager
    {
        private readonly Channel<ChannelFloatBuffer> _audioChannel;
        private readonly ChannelWriter<ChannelFloatBuffer> _writer;
        private readonly ChannelReader<ChannelFloatBuffer> _reader;
        
        private volatile bool _isRunning;
        private Task _processingTask;
        
        public AudioSegmentFlushManager()
        {
            var options = new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            
            _audioChannel = Channel.CreateBounded<ChannelFloatBuffer>(options);
            _writer = _audioChannel.Writer;
            _reader = _audioChannel.Reader;
        }
        
        public async Task StartAsync()
        {
            if (_isRunning) return;
            
            _isRunning = true;
            MmcssAudioPriority.SetAudioThreadPriority();
            
            _processingTask = Task.Run(ProcessAudioSegments);
        }
        
        public async Task StopAsync()
        {
            _isRunning = false;
            _writer.Complete();
            
            if (_processingTask != null)
            {
                await _processingTask;
            }
            
            // Flush remaining buffers
            while (_reader.TryRead(out var buffer))
            {
                buffer.Return();
            }
            
            MmcssAudioPriority.RevertAudioThreadPriority();
        }
        
        public bool TryEnqueueAudioSegment(float[] audioData, int length)
        {
            if (!_isRunning) return false;
            
            var buffer = ArrayPoolAudioBuffer.RentFloatBuffer(length);
            Array.Copy(audioData, buffer, length);
            
            var channelBuffer = new ChannelFloatBuffer(buffer, length);
            return _writer.TryWrite(channelBuffer);
        }
        
        private async Task ProcessAudioSegments()
        {
            try
            {
                while (_isRunning && await _reader.WaitToReadAsync())
                {
                    while (_reader.TryRead(out var buffer))
                    {
                        try
                        {
                            // Process audio segment for STT
                            RealTimeTelemetry.RecordSamplesProcessed(buffer.Length);
                            
                            // Here would be Whisper.NET processing
                            // var result = await whisperProcessor.ProcessAsync(buffer.Buffer, buffer.Length);
                            
                            await Task.Delay(1); // Simulate processing
                        }
                        finally
                        {
                            buffer.Return();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
        
        public string GetStatus()
        {
            return $"FlushMgr:{(_isRunning ? "Running" : "Stopped")}, Queue:Active";
        }
    }
}
