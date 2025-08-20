using System.Buffers;
using System.Threading.Channels;

namespace test_speaker_stt_translate_tts
{
    public readonly struct ChannelByteBuffer
    {
        public readonly byte[] Buffer;
        public readonly int Length;
        
        public ChannelByteBuffer(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
        
        public void Return()
        {
            ArrayPoolAudioBuffer.ReturnByteBuffer(Buffer);
        }
    }
    
    public readonly struct ChannelFloatBuffer
    {
        public readonly float[] Buffer;
        public readonly int Length;
        
        public ChannelFloatBuffer(float[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
        
        public void Return()
        {
            ArrayPoolAudioBuffer.ReturnFloatBuffer(Buffer);
        }
    }
}
