using System.Buffers;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    public sealed class ChannelByteBuffer
    {
        public byte[] Buffer { get; }
        public int Length { get; }
        public long EnqueuedAtTicks { get; } = Stopwatch.GetTimestamp();
        
        private int _returned = 0; // 0=active, 1=returned
        
        public ChannelByteBuffer(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 1)
            {
#if DEBUG
                Debug.Fail($"🚨 DOUBLE RETURN on ChannelByteBuffer: {Buffer?.Length ?? -1} bytes");
#endif
                return; // Идемпотентно - безопасно вызывать много раз
            }
            ArrayPoolAudioBuffer.ReturnByteBuffer(Buffer);
        }

#if DEBUG
        ~ChannelByteBuffer()
        {
            // Детектор утечек - если деструктор сработал без Return()
            if (Volatile.Read(ref _returned) == 0)
            {
                Debug.Fail($"🔴 LEAKED ChannelByteBuffer: {Buffer?.Length ?? -1} bytes (Return() not called)");
            }
        }
#endif
    }
    
    public sealed class ChannelFloatBuffer
    {
        public float[] Buffer { get; }
        public int Length { get; }
        public long EnqueuedAtTicks { get; } = Stopwatch.GetTimestamp();
        
        private int _returned = 0; // 0=active, 1=returned
        
        public ChannelFloatBuffer(float[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 1)
            {
#if DEBUG
                Debug.Fail($"🚨 DOUBLE RETURN on ChannelFloatBuffer: {Buffer?.Length ?? -1} samples");
#endif
                return; // Идемпотентно - безопасно вызывать много раз
            }
            ArrayPoolAudioBuffer.ReturnFloatBuffer(Buffer);
        }

#if DEBUG
        ~ChannelFloatBuffer()
        {
            // Детектор утечек - если деструктор сработал без Return()
            if (Volatile.Read(ref _returned) == 0)
            {
                Debug.Fail($"🔴 LEAKED ChannelFloatBuffer: {Buffer?.Length ?? -1} samples (Return() not called)");
            }
        }
#endif
    }
}
