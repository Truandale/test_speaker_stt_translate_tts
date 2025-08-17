using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Умный менеджер аудио для предотвращения петли обратной связи
    /// и управления очередью обработки речи
    /// </summary>
    public class SmartAudioManager : IDisposable
    {
        // 📚 КОНСТАНТЫ ДЛЯ РЕЖИМА АУДИОКНИГИ
        private const int MAX_QUEUE_SIZE = 200;           // Максимальный размер очереди для аудиокниг
        private const int AUDIOBOOK_MERGE_SIZE = 150;     // Порог объединения сегментов
        
        // Состояния
        private bool isTTSActive = false;
        private bool isCapturePaused = false;
        private bool isProcessing = false;
        private DateTime lastProcessTime = DateTime.Now;
        private readonly object lockObject = new object();
        
        // Очередь для обработки речи
        private readonly ConcurrentQueue<AudioSegment> audioQueue = new ConcurrentQueue<AudioSegment>();
        private readonly SemaphoreSlim processingLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? cancellationTokenSource;
        private Task? queueProcessingTask;
        
        // События
        public event Action<bool>? TTSStateChanged;
        public event Action<bool>? CaptureStateChanged;
        public event Action<string>? LogMessage;
        public event Func<AudioSegment, Task>? ProcessAudioSegment;
        
        public bool IsTTSActive => isTTSActive;
        public bool IsCapturePaused => isCapturePaused;
        public int QueueCount => audioQueue.Count;

        public SmartAudioManager()
        {
            cancellationTokenSource = new CancellationTokenSource();
            StartQueueProcessor();
            SafeLog("🧠 SmartAudioManager инициализирован");
        }

        /// <summary>
        /// Начало TTS - приостанавливает захват аудио
        /// </summary>
        public void NotifyTTSStarted()
        {
            lock (lockObject)
            {
                if (!isTTSActive)
                {
                    isTTSActive = true;
                    isCapturePaused = true;
                    
                    TTSStateChanged?.Invoke(true);
                    CaptureStateChanged?.Invoke(false);
                    
                    SafeLog("🔊 TTS активен - захват приостановлен");
                }
            }
        }

        /// <summary>
        /// Окончание TTS - возобновляет захват аудио и обрабатывает очередь
        /// </summary>
        public void NotifyTTSCompleted()
        {
            lock (lockObject)
            {
                if (isTTSActive)
                {
                    isTTSActive = false;
                    isCapturePaused = false;
                    
                    TTSStateChanged?.Invoke(false);
                    CaptureStateChanged?.Invoke(true);
                    
                    SafeLog($"✅ TTS завершен - захват возобновлен, в очереди: {audioQueue.Count}");
                }
            }
        }

        /// <summary>
        /// Добавление сегмента аудио в очередь
        /// </summary>
        public void QueueAudioSegment(byte[] audioData, DateTime timestamp, string source = "capture")
        {
            // 🎧 РЕЖИМ АУДИОКНИГИ: накапливаем без потерь вместо удаления
            const int MAX_QUEUE_SIZE = 200; // Увеличили лимит для аудиокниг
            const int AUDIOBOOK_MERGE_SIZE = 150; // Объединяем сегменты при этом размере
            
            if (audioQueue.Count >= MAX_QUEUE_SIZE)
            {
                SafeLog($"📚 Режим аудиокниги: очередь достигла {audioQueue.Count} сегментов, объединяем в крупные блоки...");
                
                // Вместо удаления - объединяем мелкие сегменты в крупные
                var mergedSegments = MergeSmallSegments();
                SafeLog($"✅ Объединено в {mergedSegments} крупных блоков без потери данных");
            }
            else if (audioQueue.Count >= AUDIOBOOK_MERGE_SIZE)
            {
                // Превентивное объединение для оптимизации
                _ = Task.Run(() => MergeSmallSegments());
            }
            
            var segment = new AudioSegment
            {
                AudioData = audioData,
                Timestamp = timestamp,
                Source = source,
                Id = Guid.NewGuid()
            };

            audioQueue.Enqueue(segment);
            
            if (isTTSActive && source != "priority")
            {
                // Ограничиваем логирование при TTS для предотвращения спама
                if (audioQueue.Count % 20 == 0) // Логируем каждый 20-й сегмент
                {
                    SafeLog($"📥 Аудио накоплен во время TTS: {audioData.Length} байт, всего в очереди: {audioQueue.Count}");
                }
            }
            else
            {
                SafeLog($"📥 Аудио в очереди: {audioData.Length} байт, всего в очереди: {audioQueue.Count}");
            }
        }

        /// <summary>
        /// Принудительное добавление приоритетного сегмента
        /// </summary>
        public void QueuePriorityAudioSegment(byte[] audioData, DateTime timestamp)
        {
            QueueAudioSegment(audioData, timestamp, "priority");
        }

        /// <summary>
        /// Очистка очереди
        /// </summary>
        public void ClearQueue()
        {
            var count = audioQueue.Count;
            while (audioQueue.TryDequeue(out _)) { }
            SafeLog($"🗑️ Очередь очищена: удалено {count} сегментов");
        }

        /// <summary>
        /// 🎧 Объединение мелких сегментов в крупные для аудиокниг
        /// Предотвращает потерю данных при переполнении
        /// </summary>
        private int MergeSmallSegments()
        {
            const int MIN_MERGE_SIZE = 32000; // 32KB - минимальный размер для объединения
            const int MAX_MERGED_SIZE = 256000; // 256KB - максимальный размер объединенного сегмента
            
            var tempQueue = new Queue<AudioSegment>();
            var mergedSegments = 0;
            
            try
            {
                // Извлекаем все сегменты во временную очередь
                while (audioQueue.TryDequeue(out AudioSegment? segment))
                {
                    tempQueue.Enqueue(segment);
                }
                
                var mergeBuffer = new List<byte>();
                DateTime? mergeStartTime = null;
                var segmentsInMerge = 0;
                
                while (tempQueue.Count > 0)
                {
                    var segment = tempQueue.Dequeue();
                    
                    // Если сегмент уже достаточно большой - оставляем как есть
                    if (segment.AudioData.Length >= MIN_MERGE_SIZE)
                    {
                        // Сначала завершаем текущее объединение, если есть
                        if (mergeBuffer.Count > 0)
                        {
                            var mergedSegment = new AudioSegment
                            {
                                AudioData = mergeBuffer.ToArray(),
                                Timestamp = mergeStartTime ?? DateTime.Now,
                                Source = "merged_audiobook",
                                Id = Guid.NewGuid()
                            };
                            audioQueue.Enqueue(mergedSegment);
                            mergedSegments++;
                            
                            mergeBuffer.Clear();
                            mergeStartTime = null;
                            segmentsInMerge = 0;
                        }
                        
                        // Добавляем большой сегмент обратно
                        audioQueue.Enqueue(segment);
                    }
                    else
                    {
                        // Добавляем к объединению
                        if (mergeStartTime == null)
                            mergeStartTime = segment.Timestamp;
                        
                        mergeBuffer.AddRange(segment.AudioData);
                        segmentsInMerge++;
                        
                        // Если буфер стал достаточно большим или достигли лимита - завершаем объединение
                        if (mergeBuffer.Count >= MAX_MERGED_SIZE || 
                            (mergeBuffer.Count >= MIN_MERGE_SIZE && tempQueue.Count == 0))
                        {
                            var mergedSegment = new AudioSegment
                            {
                                AudioData = mergeBuffer.ToArray(),
                                Timestamp = mergeStartTime.Value,
                                Source = $"merged_audiobook_{segmentsInMerge}_segments",
                                Id = Guid.NewGuid()
                            };
                            audioQueue.Enqueue(mergedSegment);
                            mergedSegments++;
                            
                            SafeLog($"📚 Объединено {segmentsInMerge} сегментов в блок {mergedSegment.AudioData.Length} байт");
                            
                            mergeBuffer.Clear();
                            mergeStartTime = null;
                            segmentsInMerge = 0;
                        }
                    }
                }
                
                // Завершаем последнее объединение, если осталось что-то в буфере
                if (mergeBuffer.Count > 0)
                {
                    var finalMerged = new AudioSegment
                    {
                        AudioData = mergeBuffer.ToArray(),
                        Timestamp = mergeStartTime ?? DateTime.Now,
                        Source = $"merged_audiobook_final_{segmentsInMerge}_segments",
                        Id = Guid.NewGuid()
                    };
                    audioQueue.Enqueue(finalMerged);
                    mergedSegments++;
                    
                    SafeLog($"📚 Финальное объединение {segmentsInMerge} сегментов в блок {finalMerged.AudioData.Length} байт");
                }
                
                return mergedSegments;
            }
            catch (Exception ex)
            {
                SafeLog($"❌ Ошибка объединения сегментов: {ex.Message}");
                
                // В случае ошибки возвращаем все сегменты обратно
                while (tempQueue.Count > 0)
                {
                    audioQueue.Enqueue(tempQueue.Dequeue());
                }
                
                return 0;
            }
        }

        /// <summary>
        /// Полная остановка и сброс SmartAudioManager
        /// </summary>
        public void FullStop()
        {
            lock (lockObject)
            {
                try
                {
                    SafeLog("🛑 Выполняется полная остановка SmartAudioManager...");
                    
                    // Останавливаем все процессы
                    isTTSActive = false;
                    isCapturePaused = true;
                    
                    // Очищаем очередь
                    ClearQueue();
                    
                    // Уведомляем об изменениях состояния
                    TTSStateChanged?.Invoke(false);
                    CaptureStateChanged?.Invoke(false);
                    
                    SafeLog("✅ SmartAudioManager полностью остановлен");
                }
                catch (Exception ex)
                {
                    SafeLog($"❌ Ошибка полной остановки SmartAudioManager: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Сброс состояния для нового запуска
        /// </summary>
        public void ResetForNewStart()
        {
            lock (lockObject)
            {
                try
                {
                    SafeLog("🔄 Сброс SmartAudioManager для нового запуска...");
                    
                    // Сбрасываем все блокировки
                    isTTSActive = false;
                    isCapturePaused = false;
                    
                    // Очищаем очередь (на всякий случай)
                    ClearQueue();
                    
                    // Создаем новый токен отмены
                    cancellationTokenSource?.Cancel();
                    cancellationTokenSource = new CancellationTokenSource();
                    
                    // Перезапускаем процессор очереди
                    StartQueueProcessor();
                    
                    SafeLog("✅ SmartAudioManager готов к новому запуску");
                }
                catch (Exception ex)
                {
                    SafeLog($"❌ Ошибка сброса SmartAudioManager: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Экстренная остановка со сбросом состояния
        /// </summary>
        public void EmergencyStop()
        {
            try
            {
                SafeLog("🚨 Экстренная остановка SmartAudioManager!");
                
                // Принудительно сбрасываем состояние TTS
                lock (lockObject)
                {
                    if (isTTSActive)
                    {
                        isTTSActive = false;
                        isCapturePaused = false;
                        SafeLog("🛑 Принудительный сброс состояния TTS");
                        
                        // Уведомляем о принудительном завершении
                        try
                        {
                            TTSStateChanged?.Invoke(false);
                            CaptureStateChanged?.Invoke(false);
                        }
                        catch { } // Игнорируем ошибки уведомлений
                    }
                }
                
                // Отменяем токен отмены, чтобы остановить все задачи
                cancellationTokenSource?.Cancel();
                
                // Полная остановка
                FullStop();
                
                // Ждем завершения задач с таймаутом
                queueProcessingTask?.Wait(2000);
                
                SafeLog("✅ Экстренная остановка SmartAudioManager завершена");
            }
            catch (Exception ex)
            {
                SafeLog($"💀 Критическая ошибка экстренной остановки: {ex.Message}");
                
                // В критической ситуации принудительно сбрасываем все
                try
                {
                    isTTSActive = false;
                    isCapturePaused = false;
                }
                catch { }
            }
        }

        /// <summary>
        /// Проверка, можно ли обрабатывать аудио
        /// </summary>
        public bool CanProcessAudio()
        {
            return !isTTSActive && !isCapturePaused;
        }

        /// <summary>
        /// Принудительная пауза захвата (для внешнего управления)
        /// </summary>
        public void PauseCapture(string reason = "external")
        {
            lock (lockObject)
            {
                if (!isCapturePaused)
                {
                    isCapturePaused = true;
                    CaptureStateChanged?.Invoke(false);
                    SafeLog($"⏸️ Захват приостановлен: {reason}");
                }
            }
        }

        /// <summary>
        /// Возобновление захвата
        /// </summary>
        public void ResumeCapture(string reason = "external")
        {
            lock (lockObject)
            {
                if (isCapturePaused && !isTTSActive)
                {
                    isCapturePaused = false;
                    CaptureStateChanged?.Invoke(true);
                    SafeLog($"▶️ Захват возобновлен: {reason}");
                }
            }
        }

        /// <summary>
        /// Запуск обработчика очереди
        /// </summary>
        private void StartQueueProcessor()
        {
            queueProcessingTask = Task.Run(async () =>
            {
                while (!cancellationTokenSource!.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessQueueAsync();
                        await Task.Delay(100, cancellationTokenSource.Token); // Проверяем очередь каждые 100мс
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"❌ Ошибка в обработчике очереди: {ex.Message}");
                        await Task.Delay(1000, cancellationTokenSource.Token); // Пауза при ошибке
                    }
                }
            });
        }

        /// <summary>
        /// Обработка очереди аудио сегментов
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            // Не обрабатываем очередь во время TTS
            if (isTTSActive) return;

            // Блокируем одновременную обработку
            if (!await processingLock.WaitAsync(10))
                return;

            try
            {
                if (audioQueue.TryDequeue(out AudioSegment? segment))
                {
                    // 🎧 РЕЖИМ АУДИОКНИГИ: обрабатываем все сегменты без фильтрации по размеру
                    const int MIN_AUDIO_SIZE = 8000; // Снижен для аудиокниг (8KB)
                    
                    if (segment.AudioData.Length < MIN_AUDIO_SIZE)
                    {
                        SafeLog($"⚠️ Пропуск микро-сегмента {segment.Id}: размер {segment.AudioData.Length} байт < {MIN_AUDIO_SIZE}");
                        return;
                    }
                    
                    // Специальная обработка объединенных сегментов
                    if (segment.Source.StartsWith("merged_audiobook"))
                    {
                        SafeLog($"📚 Обработка объединенного аудиокнига сегмента: {segment.Source} ({segment.AudioData.Length} байт)");
                    }
                    else
                    {
                        SafeLog($"🔄 Обработка сегмента из очереди: {segment.Id} ({segment.AudioData.Length} байт)");
                    }
                    
                    // Уведомляем о начале обработки
                    if (ProcessAudioSegment != null)
                    {
                        await ProcessAudioSegment.Invoke(segment);
                    }
                    
                    SafeLog($"✅ Сегмент обработан: {segment.Id}");
                }
            }
            finally
            {
                processingLock.Release();
            }
        }

        /// <summary>
        /// Получение статистики менеджера
        /// </summary>
        public AudioManagerStats GetStats()
        {
            return new AudioManagerStats
            {
                IsTTSActive = isTTSActive,
                IsCapturePaused = isCapturePaused,
                QueueCount = audioQueue.Count,
                CanProcessAudio = CanProcessAudio()
            };
        }

        public string GetAudiobookStatistics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("📚 СТАТИСТИКА РЕЖИМА АУДИОКНИГИ:");
            sb.AppendLine($"📊 Размер очереди: {audioQueue.Count}");
            sb.AppendLine($"🔄 Активно обрабатывается: {(isProcessing ? "Да" : "Нет")}");
            sb.AppendLine($"⏰ Дата последней обработки: {lastProcessTime:HH:mm:ss}");
            sb.AppendLine($"📈 Максимальный размер очереди: {MAX_QUEUE_SIZE}");
            sb.AppendLine($"🔗 Порог объединения: {AUDIOBOOK_MERGE_SIZE}");
            sb.AppendLine($"💾 Максимальный размер объединенного сегмента: 256KB");
            return sb.ToString();
        }

        private void SafeLog(string message)
        {
            try
            {
                LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                cancellationTokenSource?.Cancel();
                queueProcessingTask?.Wait(2000);
                
                ClearQueue();
                
                cancellationTokenSource?.Dispose();
                processingLock?.Dispose();
                
                SafeLog("🗑️ SmartAudioManager утилизирован");
            }
            catch (Exception ex)
            {
                SafeLog($"❌ Ошибка утилизации SmartAudioManager: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Сегмент аудио для обработки
    /// </summary>
    public class AudioSegment
    {
        public Guid Id { get; set; }
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// Статистика менеджера аудио
    /// </summary>
    public class AudioManagerStats
    {
        public bool IsTTSActive { get; set; }
        public bool IsCapturePaused { get; set; }
        public int QueueCount { get; set; }
        public bool CanProcessAudio { get; set; }
    }
}