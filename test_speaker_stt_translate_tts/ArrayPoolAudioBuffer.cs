using System.Buffers;

namespace test_speaker_stt_translate_tts
{
    public static class ArrayPoolAudioBuffer
    {
        private static readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
        private static readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;
        
        private static long _bytesRented, _floatsRented;
        private static long _bytesReturned, _floatsReturned;
        
        public static byte[] RentByteBuffer(int minimumLength)
        {
            Interlocked.Increment(ref _bytesRented);
            return _bytePool.Rent(minimumLength);
        }
        
        public static void ReturnByteBuffer(byte[] buffer)
        {
            if (buffer != null)
            {
                Interlocked.Increment(ref _bytesReturned);
                _bytePool.Return(buffer);
            }
        }
        
        public static float[] RentFloatBuffer(int minimumLength)
        {
            Interlocked.Increment(ref _floatsRented);
            return _floatPool.Rent(minimumLength);
        }
        
        public static void ReturnFloatBuffer(float[] buffer)
        {
            if (buffer != null)
            {
                Interlocked.Increment(ref _floatsReturned);
                _floatPool.Return(buffer);
            }
        }
        
        public static string FormatStats()
        {
            var bytesLeaked = _bytesRented - _bytesReturned;
            var floatsLeaked = _floatsRented - _floatsReturned;
            
            return $"ArrayPool: B{_bytesRented}R/{_bytesReturned}Ret({bytesLeaked}), F{_floatsRented}R/{_floatsReturned}Ret({floatsLeaked})";
        }
    }
}
