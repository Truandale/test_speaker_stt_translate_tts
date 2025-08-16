using NAudio.Wave;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Потоковый процессор аудио для параллельного STT без ожидания окончания речи
    /// Основан на логике MORT с адаптацией для непрерывной обработки
    /// </summary>
    public class StreamingAudioProcessor : IDisposable
    {
        private readonly ConcurrentQueue<AudioChunk> processingQueue = new();
        private readonly List<byte> streamBuffer = new();
        private readonly SemaphoreSlim processingSemaphore = new(3); // Максимум 3 параллельных обработки
        private bool isProcessing = false;
        private bool isTTSPlaying = false;
        private int chunkCounter = 0;
        private DateTime lastProcessedTime = DateTime.Now;
        
        // Настройки потоковой обработки
        private int streamChunkSizeMs = 3000; // Обрабатываем каждые 3 секунды
        private int overlapMs = 500; // Перекрытие чанков для непрерывности
        private float voiceThreshold = 0.05f;
        private int minChunkSizeBytes = 24000; // Минимум данных для обработки (~0.5 сек)
        
        // События
        public event Func<byte[], int, Task<string>>? ChunkReadyForSTT;
        public event Action<string, int>? StreamingResultReceived; // текст, номер чанка
        public event Action<string>? StatusUpdated;
        public event Action<bool>? TTSStateChanged;
        
        public bool IsProcessing => isProcessing;
        public bool IsTTSPlaying 
        { 
            get => isTTSPlaying; 
            set 
            { 
                isTTSPlaying = value;
                TTSStateChanged?.Invoke(value);
            } 
        }
        
        public int StreamChunkSizeMs 
        { 
            get => streamChunkSizeMs; 
            set => streamChunkSizeMs = Math.Max(1000, Math.Min(10000, value)); 
        }
        
        public float VoiceThreshold 
        { 
            get => voiceThreshold; 
            set => voiceThreshold = Math.Max(0.001f, Math.Min(1.0f, value)); 
        }

        private struct AudioChunk
        {
            public byte[] Data;
            public int ChunkNumber;
            public DateTime Timestamp;
            public float AudioLevel;
        }

        /// <summary>
        /// Основная функция потоковой обработки аудио
        /// </summary>
        public async Task ProcessStreamingAudio(byte[] buffer, int bytesRecorded, float audioLevel)
        {
            try
            {
                // Блокировка во время TTS (предотвращение эха)
                if (isTTSPlaying)
                {
                    return;
                }

                // Добавляем данные в потоковый буфер
                streamBuffer.AddRange(buffer.Take(bytesRecorded));

                // Проверяем, нужно ли обработать накопленные данные
                bool shouldProcessChunk = ShouldProcessCurrentBuffer(audioLevel);
                
                if (shouldProcessChunk && streamBuffer.Count >= minChunkSizeBytes)
                {
                    await ProcessBufferAsChunk();
                }

                // Очистка старых данных для предотвращения переполнения
                if (streamBuffer.Count > 1_000_000) // 1MB лимит
                {
                    AudioAnalysisUtils.SafeDebugLog("⚠️ Очистка переполненного потокового буфера");
                    int keepSize = streamBuffer.Count / 2;
                    streamBuffer.RemoveRange(0, streamBuffer.Count - keepSize);
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка потоковой обработки: {ex.Message}");
            }
        }

        /// <summary>
        /// Определяет, нужно ли обработать текущий буфер
        /// </summary>
        private bool ShouldProcessCurrentBuffer(float audioLevel)
        {
            var timeSinceLastProcess = DateTime.Now - lastProcessedTime;
            
            // Обрабатываем по времени ИЛИ при достижении большого размера
            bool timeThreshold = timeSinceLastProcess.TotalMilliseconds >= streamChunkSizeMs;
            bool sizeThreshold = streamBuffer.Count >= (streamChunkSizeMs * 44100 * 4 / 1000); // ~размер для времени
            bool hasVoice = audioLevel > voiceThreshold;
            
            return (timeThreshold || sizeThreshold) && hasVoice;
        }

        /// <summary>
        /// Обрабатывает текущий буфер как чанк
        /// </summary>
        private async Task ProcessBufferAsChunk()
        {
            try
            {
                chunkCounter++;
                lastProcessedTime = DateTime.Now;
                
                // Копируем данные для обработки с перекрытием
                int chunkSize = streamBuffer.Count;
                int overlapSize = Math.Min(overlapMs * 44100 * 4 / 1000, streamBuffer.Count / 4);
                
                byte[] chunkData = new byte[chunkSize];
                streamBuffer.CopyTo(chunkData);
                
                // Создаем чанк для обработки
                var chunk = new AudioChunk
                {
                    Data = chunkData,
                    ChunkNumber = chunkCounter,
                    Timestamp = DateTime.Now,
                    AudioLevel = CalculateAverageLevel(chunkData)
                };

                // Добавляем в очередь обработки
                processingQueue.Enqueue(chunk);
                
                // Запускаем параллельную обработку
                _ = Task.Run(() => ProcessChunkAsync(chunk));
                
                // Очищаем буфер с сохранением перекрытия
                if (streamBuffer.Count > overlapSize)
                {
                    streamBuffer.RemoveRange(0, streamBuffer.Count - overlapSize);
                }
                
                AudioAnalysisUtils.SafeDebugLog($"🔄 Чанк #{chunkCounter} отправлен на обработку ({chunkSize} байт)");
                StatusUpdated?.Invoke($"🔄 Обрабатываю чанк #{chunkCounter}...");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка создания чанка: {ex.Message}");
            }
        }

        /// <summary>
        /// Асинхронная обработка одного чанка
        /// </summary>
        private async Task ProcessChunkAsync(AudioChunk chunk)
        {
            await processingSemaphore.WaitAsync();
            
            try
            {
                if (ChunkReadyForSTT == null) return;
                
                AudioAnalysisUtils.SafeDebugLog($"🎤 Обработка чанка #{chunk.ChunkNumber}");
                
                // Выполняем STT для чанка
                string result = await ChunkReadyForSTT(chunk.Data, chunk.ChunkNumber);
                
                // Фильтруем результат
                if (!string.IsNullOrWhiteSpace(result) && !AudioAnalysisUtils.IsAudioPlaceholder(result))
                {
                    AudioAnalysisUtils.SafeDebugLog($"✅ Чанк #{chunk.ChunkNumber}: '{result}'");
                    StreamingResultReceived?.Invoke(result, chunk.ChunkNumber);
                }
                else
                {
                    AudioAnalysisUtils.SafeDebugLog($"🚫 Чанк #{chunk.ChunkNumber} отфильтрован: '{result}'");
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка обработки чанка #{chunk.ChunkNumber}: {ex.Message}");
            }
            finally
            {
                processingSemaphore.Release();
            }
        }

        /// <summary>
        /// Принудительная обработка текущего буфера (для завершения речи)
        /// </summary>
        public async Task FlushCurrentBuffer()
        {
            if (streamBuffer.Count > minChunkSizeBytes)
            {
                AudioAnalysisUtils.SafeDebugLog("🔄 Принудительная обработка остатка буфера");
                await ProcessBufferAsChunk();
            }
        }

        /// <summary>
        /// Вычисляет средний уровень аудио в буфере
        /// </summary>
        private float CalculateAverageLevel(byte[] buffer)
        {
            if (buffer.Length < 4) return 0f;
            
            float sum = 0f;
            int sampleCount = 0;
            
            for (int i = 0; i < buffer.Length - 3; i += 4)
            {
                float sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                sum += sample;
                sampleCount++;
            }
            
            return sampleCount > 0 ? sum / sampleCount : 0f;
        }

        /// <summary>
        /// Настройка параметров потоковой обработки
        /// </summary>
        public void ConfigureStreaming(int chunkSizeMs = 3000, int overlapMs = 500, float threshold = 0.05f)
        {
            this.streamChunkSizeMs = Math.Max(1000, Math.Min(10000, chunkSizeMs));
            this.overlapMs = Math.Max(100, Math.Min(2000, overlapMs));
            this.voiceThreshold = Math.Max(0.001f, Math.Min(1.0f, threshold));
            
            AudioAnalysisUtils.SafeDebugLog($"🔧 Настройки потока: чанк={this.streamChunkSizeMs}мс, перекрытие={this.overlapMs}мс, порог={this.voiceThreshold}");
        }

        /// <summary>
        /// Сброс состояния процессора
        /// </summary>
        public void Reset()
        {
            try
            {
                streamBuffer.Clear();
                while (processingQueue.TryDequeue(out _)) { }
                chunkCounter = 0;
                lastProcessedTime = DateTime.Now;
                isProcessing = false;
                
                AudioAnalysisUtils.SafeDebugLog("🔄 Потоковый процессор сброшен");
                StatusUpdated?.Invoke("🔄 Готов к потоковой обработке");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка сброса: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение статистики обработки
        /// </summary>
        public string GetProcessingStats()
        {
            return $"Чанков: {chunkCounter}, Буфер: {streamBuffer.Count} байт, Очередь: {processingQueue.Count}";
        }

        public void Dispose()
        {
            try
            {
                Reset();
                processingSemaphore?.Dispose();
                AudioAnalysisUtils.SafeDebugLog("🗑️ StreamingAudioProcessor утилизирован");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка утилизации: {ex.Message}");
            }
        }
    }
}