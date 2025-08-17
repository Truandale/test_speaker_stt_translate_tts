using System;
using System.Collections.Concurrent;
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
        // Состояния
        private bool isTTSActive = false;
        private bool isCapturePaused = false;
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
                SafeLog($"📥 Аудио накоплен во время TTS: {audioData.Length} байт, всего в очереди: {audioQueue.Count}");
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
                    SafeLog($"🔄 Обработка сегмента из очереди: {segment.Id} ({segment.AudioData.Length} байт)");
                    
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