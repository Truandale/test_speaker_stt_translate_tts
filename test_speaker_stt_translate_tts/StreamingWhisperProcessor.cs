using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using Whisper.net;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Продвинутый процессор для стримингового распознавания речи с Whisper.NET
    /// Реализует скользящее окно с перекрытием для непрерывного распознавания
    /// </summary>
    public class StreamingWhisperProcessor : IDisposable, IAsyncDisposable
    {
        #region Configuration Constants
        
        private const int TARGET_SAMPLE_RATE = 16000;    // Whisper требует 16kHz
        private const int WINDOW_DURATION_SEC = 6;       // Длина окна для анализа (6 секунд)
        private const int STEP_DURATION_SEC = 1;         // Шаг между запусками (1 секунда)
        private const float OVERLAP_SEC = 0.5f;          // Перекрытие для плавности (0.5 сек)
        private const int MIN_AUDIO_LENGTH_SEC = 2;      // Минимум для обработки (2 секунды)
        private const float VAD_THRESHOLD = 0.01f;       // Порог активности голоса
        
        #endregion

        #region Private Fields
        
        private readonly int windowSamples;
        private readonly int stepSamples;
        private readonly int minAudioSamples;
        private readonly float[] ringBuffer;
        private int ringBufferPosition = 0;
        private int totalSamplesInBuffer = 0;
        
        private WhisperFactory? whisperFactory;
        private WhisperProcessor? whisperProcessor;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        
        private readonly ConcurrentQueue<float[]> processingQueue = new();
        private readonly SemaphoreSlim processingSemaphore = new(1, 1);
        private Task? processingTask;
        
        private string lastRecognizedText = "";
        private readonly object textLock = new();
        
        private bool isDisposed = false;
        
        #endregion

        #region Events
        
        /// <summary>
        /// Событие получения нового распознанного текста
        /// </summary>
        public event Action<string, double>? OnTextRecognized;
        
        /// <summary>
        /// Событие ошибки обработки
        /// </summary>
        public event Action<Exception>? OnError;
        
        /// <summary>
        /// Событие статистики обработки
        /// </summary>
        public event Action<StreamingStats>? OnStats;
        
        #endregion

        #region Constructor & Initialization
        
        public StreamingWhisperProcessor()
        {
            windowSamples = WINDOW_DURATION_SEC * TARGET_SAMPLE_RATE;
            stepSamples = STEP_DURATION_SEC * TARGET_SAMPLE_RATE;
            minAudioSamples = MIN_AUDIO_LENGTH_SEC * TARGET_SAMPLE_RATE;
            
            ringBuffer = new float[windowSamples * 2]; // Буфер с запасом
            
            Debug.WriteLine($"🔧 StreamingWhisperProcessor инициализирован:");
            Debug.WriteLine($"   Окно: {WINDOW_DURATION_SEC}с ({windowSamples} семплов)");
            Debug.WriteLine($"   Шаг: {STEP_DURATION_SEC}с ({stepSamples} семплов)");
            Debug.WriteLine($"   Перекрытие: {OVERLAP_SEC}с");
            Debug.WriteLine($"   Минимум: {MIN_AUDIO_LENGTH_SEC}с ({minAudioSamples} семплов)");
        }
        
        public async Task<bool> InitializeAsync(string modelPath)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    Debug.WriteLine($"❌ Модель Whisper не найдена: {modelPath}");
                    return false;
                }

                Debug.WriteLine($"🔄 Загрузка модели Whisper: {modelPath}");
                whisperFactory = WhisperFactory.FromPath(modelPath);
                
                whisperProcessor = whisperFactory.CreateBuilder()
                    .WithLanguage("auto")           // Автоопределение языка
                    .Build();

                // Запускаем фоновую обработку
                processingTask = Task.Run(ProcessingLoop, cancellationTokenSource.Token);
                
                var modelInfo = new FileInfo(modelPath);
                Debug.WriteLine($"✅ Whisper инициализирован: {modelInfo.Length / 1024 / 1024:F1} MB");
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка инициализации Whisper: {ex.Message}");
                OnError?.Invoke(ex);
                return false;
            }
        }
        
        #endregion

        #region Audio Processing
        
        /// <summary>
        /// Добавляет новые аудио семплы для обработки
        /// </summary>
        public void AddAudioSamples(float[] samples)
        {
            if (isDisposed || samples == null || samples.Length == 0 || cancellationTokenSource.Token.IsCancellationRequested)
                return;

            lock (ringBuffer)
            {
                // Проверяем еще раз после получения блокировки
                if (isDisposed || cancellationTokenSource.Token.IsCancellationRequested)
                    return;

                // Добавляем семплы в кольцевой буфер
                for (int i = 0; i < samples.Length; i++)
                {
                    ringBuffer[ringBufferPosition] = samples[i];
                    ringBufferPosition = (ringBufferPosition + 1) % ringBuffer.Length;
                    
                    if (totalSamplesInBuffer < ringBuffer.Length)
                        totalSamplesInBuffer++;
                }

                // Проверяем, можно ли обработать новое окно
                if (totalSamplesInBuffer >= windowSamples)
                {
                    // Проверяем активность голоса
                    if (HasVoiceActivity(samples))
                    {
                        // Извлекаем окно для обработки
                        var windowData = ExtractWindow();
                        if (windowData != null && !cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            processingQueue.Enqueue(windowData);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Проверяет наличие голосовой активности в семплах
        /// </summary>
        private bool HasVoiceActivity(float[] samples)
        {
            if (samples.Length == 0) return false;
            
            // Простой VAD на основе энергии сигнала
            float energy = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                energy += samples[i] * samples[i];
            }
            
            float rmsLevel = (float)Math.Sqrt(energy / samples.Length);
            return rmsLevel > VAD_THRESHOLD;
        }
        
        /// <summary>
        /// Извлекает окно данных из кольцевого буфера
        /// </summary>
        private float[]? ExtractWindow()
        {
            if (totalSamplesInBuffer < minAudioSamples)
                return null;

            var window = new float[windowSamples];
            int startPos = (ringBufferPosition - windowSamples + ringBuffer.Length) % ringBuffer.Length;
            
            for (int i = 0; i < windowSamples; i++)
            {
                window[i] = ringBuffer[(startPos + i) % ringBuffer.Length];
            }
            
            return window;
        }
        
        #endregion

        #region Processing Loop
        
        /// <summary>
        /// Основной цикл обработки аудио
        /// </summary>
        private async Task ProcessingLoop()
        {
            Debug.WriteLine("🚀 Запуск цикла обработки Whisper");
            
            var stats = new StreamingStats();
            
            while (!cancellationTokenSource.Token.IsCancellationRequested && !isDisposed)
            {
                try
                {
                    if (processingQueue.TryDequeue(out var audioData))
                    {
                        // Проверяем состояние еще раз перед обработкой
                        if (cancellationTokenSource.Token.IsCancellationRequested || isDisposed)
                            break;

                        var stopwatch = Stopwatch.StartNew();
                        
                        await processingSemaphore.WaitAsync(cancellationTokenSource.Token);
                        try
                        {
                            // Дополнительная проверка после получения семафора
                            if (cancellationTokenSource.Token.IsCancellationRequested || isDisposed)
                                break;

                            var text = await ProcessAudioWindow(audioData);
                            if (!string.IsNullOrWhiteSpace(text) && !isDisposed)
                            {
                                var confidence = CalculateConfidence(text);
                                
                                lock (textLock)
                                {
                                    if (!isDisposed)
                                    {
                                        var newText = ExtractNewText(text);
                                        if (!string.IsNullOrWhiteSpace(newText))
                                        {
                                            OnTextRecognized?.Invoke(newText, confidence);
                                            lastRecognizedText = text;
                                        }
                                    }
                                }
                                
                                // Обновляем статистику
                                if (!isDisposed)
                                {
                                    stats.ProcessedWindows++;
                                    stats.TotalRecognizedText += text.Length;
                                    stats.AverageProcessingTime = 
                                        (stats.AverageProcessingTime * (stats.ProcessedWindows - 1) + stopwatch.ElapsedMilliseconds) 
                                        / stats.ProcessedWindows;
                                    
                                    OnStats?.Invoke(stats);
                                }
                            }
                        }
                        finally
                        {
                            try
                            {
                                processingSemaphore.Release();
                            }
                            catch (ObjectDisposedException)
                            {
                                // Игнорируем - семафор уже освобожден
                            }
                        }
                        
                        stopwatch.Stop();
                        Debug.WriteLine($"⏱️ Обработка окна: {stopwatch.ElapsedMilliseconds}мс");
                    }
                    else
                    {
                        await Task.Delay(50, cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("🔄 Обработка отменена");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Debug.WriteLine("🔄 Ресурсы освобождены, завершаем обработку");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Ошибка в цикле обработки: {ex.Message}");
                    OnError?.Invoke(ex);
                    
                    // Небольшая пауза при ошибке
                    try
                    {
                        await Task.Delay(1000, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            
            Debug.WriteLine("🛑 Цикл обработки Whisper остановлен");
        }
        
        /// <summary>
        /// Обрабатывает одно окно аудио через Whisper
        /// </summary>
        private async Task<string> ProcessAudioWindow(float[] audioData)
        {
            if (whisperProcessor == null || audioData.Length < minAudioSamples)
                return "";

            try
            {
                // Конвертируем float в WAV в памяти
                byte[] wavData;
                var waveFormat = new WaveFormat(TARGET_SAMPLE_RATE, 16, 1); // 16kHz, 16-bit, mono
                
                using (var memoryStream = new MemoryStream())
                {
                    using (var writer = new WaveFileWriter(memoryStream, waveFormat))
                    {
                        // Конвертируем float [-1..1] в PCM16
                        for (int i = 0; i < audioData.Length; i++)
                        {
                            var sample = (short)(Math.Clamp(audioData[i], -1f, 1f) * short.MaxValue);
                            writer.WriteSample(sample);
                        }
                    }
                    wavData = memoryStream.ToArray();
                }
                
                // Создаем новый поток для Whisper с скопированными данными
                using var whisperStream = new MemoryStream(wavData);
                
                // Отправляем в Whisper
                var resultText = "";
                await foreach (var result in whisperProcessor.ProcessAsync(whisperStream, cancellationTokenSource.Token))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        resultText += result.Text;
                    }
                }

                var finalText = resultText.Trim();
                
                // 🚀 НОВЫЙ: Используем продвинутый фильтр с аудио анализом
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    bool isValid = AdvancedSpeechFilter.IsValidHumanSpeech(finalText, audioData);
                    if (!isValid)
                    {
                        Debug.WriteLine($"🚫 Продвинутый фильтр отклонил: '{finalText}'");
                        return "";
                    }
                }
                
                return finalText;
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("⚠️ Whisper поток был закрыт во время обработки");
                return "";
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("⚠️ Обработка Whisper была отменена");
                return "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка обработки аудио в Whisper: {ex.Message}");
                return "";
            }
        }
        
        /// <summary>
        /// Извлекает новый текст, исключая дубликаты из перекрытий
        /// </summary>
        private string ExtractNewText(string currentText)
        {
            if (string.IsNullOrWhiteSpace(lastRecognizedText))
                return currentText;
            
            // Находим общий суффикс для исключения дубликатов
            var commonLength = FindCommonSuffixLength(lastRecognizedText, currentText);
            if (commonLength > 0 && commonLength < currentText.Length)
            {
                return currentText.Substring(commonLength);
            }
            
            return currentText;
        }
        
        /// <summary>
        /// Находит длину общего суффикса двух строк
        /// </summary>
        private int FindCommonSuffixLength(string a, string b)
        {
            int i = a.Length - 1;
            int j = b.Length - 1;
            int common = 0;
            
            while (i >= 0 && j >= 0 && a[i] == b[j])
            {
                i--;
                j--;
                common++;
            }
            
            return Math.Max(0, a.Length - common);
        }
        
        /// <summary>
        /// Получает контекстную подсказку для Whisper
        /// </summary>
        private string GetContextPrompt()
        {
            lock (textLock)
            {
                if (string.IsNullOrWhiteSpace(lastRecognizedText))
                    return "";
                
                // Берем последние 100 символов как контекст
                var contextLength = Math.Min(100, lastRecognizedText.Length);
                return lastRecognizedText.Substring(lastRecognizedText.Length - contextLength);
            }
        }
        
        /// <summary>
        /// Вычисляет примерную уверенность в распознавании
        /// </summary>
        private double CalculateConfidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0.0;
            
            // Простая эвристика на основе длины и повторений
            var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var uniqueWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Count();
            
            if (wordCount == 0) return 0.0;
            
            var uniqueRatio = (double)uniqueWords / wordCount;
            var lengthFactor = Math.Min(1.0, wordCount / 10.0); // Длинные фразы более уверенны
            
            return Math.Min(0.95, uniqueRatio * lengthFactor + 0.3);
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// Сбрасывает состояние процессора
        /// </summary>
        public void Reset()
        {
            lock (textLock)
            {
                lastRecognizedText = "";
            }
            
            lock (ringBuffer)
            {
                Array.Clear(ringBuffer, 0, ringBuffer.Length);
                ringBufferPosition = 0;
                totalSamplesInBuffer = 0;
            }
            
            // Очищаем очередь
            while (processingQueue.TryDequeue(out _)) { }
            
            Debug.WriteLine("🔄 StreamingWhisperProcessor сброшен");
        }
        
        /// <summary>
        /// Получает текущую статистику
        /// </summary>
        public StreamingStats GetStats()
        {
            return new StreamingStats
            {
                QueueSize = processingQueue.Count,
                BufferFillLevel = (double)totalSamplesInBuffer / ringBuffer.Length,
                IsProcessing = processingSemaphore.CurrentCount == 0
            };
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (isDisposed) return;
            
            Debug.WriteLine("🔄 Остановка StreamingWhisperProcessor...");
            
            // Устанавливаем флаг досрочно
            isDisposed = true;
            
            // Отменяем все операции
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - токен уже был освобожден
            }
            
            // Ждем завершения задачи обработки
            try
            {
                processingTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при остановке задачи обработки: {ex.Message}");
            }
            
            // Очищаем очередь
            while (processingQueue.TryDequeue(out _)) { }
            
            // Освобождаем ресурсы Whisper асинхронно
            try
            {
                if (whisperProcessor != null)
                {
                    // Используем DisposeAsync если доступен
                    if (whisperProcessor is IAsyncDisposable asyncDisposable)
                    {
                        // Запускаем асинхронное освобождение в отдельной задаче
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await asyncDisposable.DisposeAsync();
                                Debug.WriteLine("✅ WhisperProcessor освобожден асинхронно");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"⚠️ Ошибка при асинхронном освобождении WhisperProcessor: {ex.Message}");
                            }
                        });
                    }
                    else
                    {
                        whisperProcessor.Dispose();
                    }
                    whisperProcessor = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при освобождении WhisperProcessor: {ex.Message}");
            }
            
            try
            {
                whisperFactory?.Dispose();
                whisperFactory = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при освобождении WhisperFactory: {ex.Message}");
            }
            
            // Освобождаем системные ресурсы
            try
            {
                cancellationTokenSource.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - уже освобожден
            }
            
            try
            {
                processingSemaphore.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - уже освобожден
            }
            
            Debug.WriteLine("✅ StreamingWhisperProcessor остановлен");
        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            if (isDisposed) return;
            
            Debug.WriteLine("🔄 Асинхронная остановка StreamingWhisperProcessor...");
            
            // Устанавливаем флаг досрочно
            isDisposed = true;
            
            // Отменяем все операции
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - токен уже был освобожден
            }
            
            // Ждем завершения задачи обработки
            try
            {
                if (processingTask != null)
                {
                    await processingTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при остановке задачи обработки: {ex.Message}");
            }
            
            // Очищаем очередь
            while (processingQueue.TryDequeue(out _)) { }
            
            // Освобождаем ресурсы Whisper асинхронно
            try
            {
                if (whisperProcessor != null)
                {
                    if (whisperProcessor is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync();
                    }
                    else
                    {
                        whisperProcessor.Dispose();
                    }
                    whisperProcessor = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при освобождении WhisperProcessor: {ex.Message}");
            }
            
            try
            {
                whisperFactory?.Dispose();
                whisperFactory = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Ошибка при освобождении WhisperFactory: {ex.Message}");
            }
            
            // Освобождаем системные ресурсы
            try
            {
                cancellationTokenSource.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - уже освобожден
            }
            
            try
            {
                processingSemaphore.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Игнорируем - уже освобожден
            }
            
            Debug.WriteLine("✅ StreamingWhisperProcessor асинхронно остановлен");
        }

        #endregion
    }
    
    /// <summary>
    /// Статистика работы стримингового процессора
    /// </summary>
    public class StreamingStats
    {
        public int ProcessedWindows { get; set; }
        public int QueueSize { get; set; }
        public double BufferFillLevel { get; set; }
        public bool IsProcessing { get; set; }
        public double AverageProcessingTime { get; set; }
        public int TotalRecognizedText { get; set; }
    }
}