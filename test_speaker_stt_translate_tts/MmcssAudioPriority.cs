using System.Runtime.InteropServices;

namespace test_speaker_stt_translate_tts
{
    public static class MmcssAudioPriority
    {
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);
        
        [DllImport("avrt.dll")]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        
        private static IntPtr _mmcssHandle = IntPtr.Zero;
        private static uint _taskIndex;
        
        public static bool SetAudioThreadPriority()
        {
            try
            {
                if (_mmcssHandle != IntPtr.Zero) return true;
                
                _mmcssHandle = AvSetMmThreadCharacteristics("Audio", out _taskIndex);
                return _mmcssHandle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
        
        public static void RevertAudioThreadPriority()
        {
            try
            {
                if (_mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(_mmcssHandle);
                    _mmcssHandle = IntPtr.Zero;
                }
            }
            catch { }
        }
        
        public static string GetStatus()
        {
            return _mmcssHandle != IntPtr.Zero ? "MMCSS:Active" : "MMCSS:Inactive";
        }
    }
}
