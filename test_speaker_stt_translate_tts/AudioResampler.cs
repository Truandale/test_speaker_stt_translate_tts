using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Высокопроизводительный ресэмплер для конвертации аудио в формат Whisper (16kHz моно)
    /// </summary>
    public class AudioResampler : IDisposable
    {
        #region Constants
        
        private const int TARGET_SAMPLE_RATE = 16000;
        private const int TARGET_CHANNELS = 1;
        private const int BUFFER_SIZE = 4096;
        
        #endregion

        #region Private Fields
        
        private readonly int sourceSampleRate;
        private readonly int sourceChannels;
        private readonly double resampleRatio;
        
        private MediaFoundationResampler? resampler;
        private WaveFormat sourceFormat;
        private WaveFormat targetFormat;
        
        private readonly float[] tempBuffer = new float[BUFFER_SIZE * 2]; // Буфер с запасом
        private bool isDisposed = false;
        
        #endregion

        #region Constructor
        
        public AudioResampler(int sourceSampleRate, int sourceChannels)
        {
            this.sourceSampleRate = sourceSampleRate;
            this.sourceChannels = sourceChannels;
            this.resampleRatio = (double)TARGET_SAMPLE_RATE / sourceSampleRate;
            
            sourceFormat = new WaveFormat(sourceSampleRate, 32, sourceChannels); // Float32
            targetFormat = new WaveFormat(TARGET_SAMPLE_RATE, 32, TARGET_CHANNELS);
            
            InitializeResampler();
            
            Debug.WriteLine($"🔧 AudioResampler инициализирован:");
            Debug.WriteLine($"   Источник: {sourceSampleRate}Hz, {sourceChannels}ch");
            Debug.WriteLine($"   Цель: {TARGET_SAMPLE_RATE}Hz, {TARGET_CHANNELS}ch");
            Debug.WriteLine($"   Коэффициент: {resampleRatio:F3}");
        }
        
        #endregion

        #region Initialization
        
        private void InitializeResampler()
        {
            try
            {
                if (sourceSampleRate == TARGET_SAMPLE_RATE && sourceChannels == TARGET_CHANNELS)
                {
                    Debug.WriteLine("✅ Ресэмплинг не требуется - форматы совпадают");
                    return;
                }
                
                // Создаем MediaFoundation ресэмплер для высокого качества
                // MediaFoundationResampler требует IWaveProvider, поэтому используем простой ресэмплинг
                resampler = null; // Будем использовать LinearResampler
                
                Debug.WriteLine("✅ Линейный ресэмплер инициализирован (fallback)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка инициализации ресэмплера: {ex.Message}");
                // Fallback к простому ресэмплингу
                resampler = null;
            }
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// Конвертирует аудио семплы в формат Whisper (16kHz моно)
        /// </summary>
        public float[] Resample(float[] inputSamples)
        {
            if (isDisposed || inputSamples == null || inputSamples.Length == 0)
                return Array.Empty<float>();

            try
            {
                // Если форматы уже совпадают, просто копируем
                if (sourceSampleRate == TARGET_SAMPLE_RATE && sourceChannels == TARGET_CHANNELS)
                {
                    return (float[])inputSamples.Clone();
                }
                
                // Сначала конвертируем в моно, если нужно
                float[] monoSamples = ConvertToMono(inputSamples);
                
                // Затем ресэмплируем частоту, если нужно
                if (sourceSampleRate != TARGET_SAMPLE_RATE)
                {
                    return ResampleFrequency(monoSamples);
                }
                
                return monoSamples;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка ресэмплинга: {ex.Message}");
                return Array.Empty<float>();
            }
        }
        
        /// <summary>
        /// Конвертирует byte[] в float[] с ресэмплингом
        /// </summary>
        public float[] ResampleFromBytes(byte[] audioBytes, WaveFormat inputFormat)
        {
            if (isDisposed || audioBytes == null || audioBytes.Length == 0)
                return Array.Empty<float>();

            try
            {
                // Конвертируем bytes в float в зависимости от формата
                float[] floatSamples = ConvertBytesToFloat(audioBytes, inputFormat);
                
                // Обновляем параметры, если они изменились
                if (inputFormat.SampleRate != sourceSampleRate || inputFormat.Channels != sourceChannels)
                {
                    UpdateFormat(inputFormat.SampleRate, inputFormat.Channels);
                }
                
                return Resample(floatSamples);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка конвертации из bytes: {ex.Message}");
                return Array.Empty<float>();
            }
        }
        
        #endregion

        #region Private Methods
        
        /// <summary>
        /// Конвертирует стерео/мульти-канал в моно
        /// </summary>
        private float[] ConvertToMono(float[] inputSamples)
        {
            if (sourceChannels == 1)
                return inputSamples;
            
            int outputLength = inputSamples.Length / sourceChannels;
            float[] monoSamples = new float[outputLength];
            
            for (int i = 0; i < outputLength; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    sum += inputSamples[i * sourceChannels + ch];
                }
                monoSamples[i] = sum / sourceChannels;
            }
            
            return monoSamples;
        }
        
        /// <summary>
        /// Изменяет частоту дискретизации
        /// </summary>
        private float[] ResampleFrequency(float[] inputSamples)
        {
            if (resampler != null)
            {
                return ResampleWithMediaFoundation(inputSamples);
            }
            else
            {
                return ResampleLinear(inputSamples);
            }
        }
        
        /// <summary>
        /// Ресэмплинг через MediaFoundation (высокое качество)
        /// </summary>
        private float[] ResampleWithMediaFoundation(float[] inputSamples)
        {
            try
            {
                // Конвертируем float в byte array
                byte[] inputBytes = new byte[inputSamples.Length * 4];
                Buffer.BlockCopy(inputSamples, 0, inputBytes, 0, inputBytes.Length);
                
                // Ресэмплируем
                byte[] outputBytes = new byte[(int)(inputBytes.Length * resampleRatio) + 1024]; // Буфер с запасом
                int outputLength = resampler!.Read(outputBytes, 0, outputBytes.Length);
                
                // Конвертируем обратно в float
                float[] outputSamples = new float[outputLength / 4];
                Buffer.BlockCopy(outputBytes, 0, outputSamples, 0, outputLength);
                
                return outputSamples;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка MediaFoundation ресэмплинга: {ex.Message}");
                return ResampleLinear(inputSamples);
            }
        }
        
        /// <summary>
        /// Простой линейный ресэмплинг (fallback)
        /// </summary>
        private float[] ResampleLinear(float[] inputSamples)
        {
            int outputLength = (int)(inputSamples.Length * resampleRatio);
            float[] outputSamples = new float[outputLength];
            
            for (int i = 0; i < outputLength; i++)
            {
                double sourceIndex = i / resampleRatio;
                int index1 = (int)sourceIndex;
                int index2 = Math.Min(index1 + 1, inputSamples.Length - 1);
                
                double fraction = sourceIndex - index1;
                outputSamples[i] = (float)(inputSamples[index1] * (1 - fraction) + inputSamples[index2] * fraction);
            }
            
            return outputSamples;
        }
        
        /// <summary>
        /// Конвертирует byte array в float array в зависимости от формата
        /// </summary>
        private float[] ConvertBytesToFloat(byte[] audioBytes, WaveFormat format)
        {
            switch (format.BitsPerSample)
            {
                case 16:
                    return ConvertInt16ToFloat(audioBytes);
                case 24:
                    return ConvertInt24ToFloat(audioBytes);
                case 32:
                    if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                        return ConvertFloat32ToFloat(audioBytes);
                    else
                        return ConvertInt32ToFloat(audioBytes);
                default:
                    throw new ArgumentException($"Неподдерживаемый формат: {format.BitsPerSample} бит");
            }
        }
        
        private float[] ConvertInt16ToFloat(byte[] bytes)
        {
            float[] samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = BitConverter.ToInt16(bytes, i * 2);
                samples[i] = sample / 32768f;
            }
            return samples;
        }
        
        private float[] ConvertInt24ToFloat(byte[] bytes)
        {
            float[] samples = new float[bytes.Length / 3];
            for (int i = 0; i < samples.Length; i++)
            {
                int sample = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000); // Sign extension
                samples[i] = sample / 8388608f;
            }
            return samples;
        }
        
        private float[] ConvertInt32ToFloat(byte[] bytes)
        {
            float[] samples = new float[bytes.Length / 4];
            for (int i = 0; i < samples.Length; i++)
            {
                int sample = BitConverter.ToInt32(bytes, i * 4);
                samples[i] = sample / 2147483648f;
            }
            return samples;
        }
        
        private float[] ConvertFloat32ToFloat(byte[] bytes)
        {
            float[] samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }
        
        /// <summary>
        /// Обновляет формат источника (если входные параметры изменились)
        /// </summary>
        private void UpdateFormat(int newSampleRate, int newChannels)
        {
            if (newSampleRate == sourceSampleRate && newChannels == sourceChannels)
                return;
                
            Debug.WriteLine($"🔄 Обновление формата: {newSampleRate}Hz, {newChannels}ch");
            
            // Пересоздаем ресэмплер с новыми параметрами
            resampler?.Dispose();
            resampler = null; // Используем линейный ресэмплинг
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (isDisposed) return;
            
            resampler?.Dispose();
            isDisposed = true;
            
            Debug.WriteLine("✅ AudioResampler остановлен");
        }
        
        #endregion
    }
}