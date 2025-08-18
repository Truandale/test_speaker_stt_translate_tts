using System.Buffers;
using System.Text;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Система скользящего окна с VAD для агрегации аудио-сегментов
    /// Накапливает 2-4 секунды аудио с перекрытием для стабильного STT
    /// </summary>
    public class SlidingWindowAggregator : IDisposable
    {
        #region Configuration
        
        private const int SAMPLE_RATE = 16000;
        private const float WINDOW_DURATION_SEC = 3.0f; // Размер окна
        private const float OVERLAP_DURATION_SEC = 0.5f; // Перекрытие
        private const float SLIDE_INTERVAL_SEC = 1.5f; // Интервал сдвига
        
        private const float RMS_THRESHOLD = 0.001f; // Порог активности по RMS
        private const float SILENCE_THRESHOLD_SEC = 0.6f; // Пауза для триггера
        private const int MAX_TEXT_LENGTH = 400; // Максимальная длина для TTS
        
        private readonly int _windowSamples;
        private readonly int _overlapSamples;
        private readonly int _slideSamples;
        private readonly int _silenceThresholdSamples;
        
        #endregion

        #region Private Fields
        
        private readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;
        private readonly List<float> _audioBuffer = new();
        private readonly StringBuilder _textAccumulator = new();
        
        private DateTime _lastSlideTime = DateTime.UtcNow;
        private DateTime _lastActivityTime = DateTime.UtcNow;
        private bool _isDisposed = false;
        
        // Статистика для мониторинга
        private int _processedSegments = 0;
        private int _droppedSilentSegments = 0;
        private float _averageRms = 0f;
        
        #endregion

        #region Events
        
        public event Func<float[], CancellationToken, Task<string?>>? OnAudioSegmentReady;
        public event Action<string>? OnTextReady;
        public event Action<string>? OnStatusChanged;
        public event Action<AudioAnalysisResult>? OnAudioAnalysis;
        
        #endregion

        public SlidingWindowAggregator()
        {
            _windowSamples = (int)(WINDOW_DURATION_SEC * SAMPLE_RATE);
            _overlapSamples = (int)(OVERLAP_DURATION_SEC * SAMPLE_RATE);
            _slideSamples = (int)(SLIDE_INTERVAL_SEC * SAMPLE_RATE);
            _silenceThresholdSamples = (int)(SILENCE_THRESHOLD_SEC * SAMPLE_RATE);
            
            OnStatusChanged?.Invoke($"🎚️ Окно: {WINDOW_DURATION_SEC}с, перекрытие: {OVERLAP_DURATION_SEC}с, сдвиг: {SLIDE_INTERVAL_SEC}с");
        }

        #region Public Methods
        
        /// <summary>
        /// Добавление нового аудио-сегмента в агрегатор
        /// </summary>
        public async Task AddAudioSegmentAsync(float[] audioData, CancellationToken ct = default)
        {
            if (_isDisposed || audioData == null || audioData.Length == 0)
                return;
                
            try
            {
                // Добавление в общий буфер
                lock (_audioBuffer)
                {
                    _audioBuffer.AddRange(audioData);
                }
                
                // Анализ активности
                var analysis = AnalyzeAudioActivity(audioData);
                OnAudioAnalysis?.Invoke(analysis);
                
                // Обновление статистики
                UpdateActivityTracking(analysis);
                
                // Проверка готовности к обработке
                await CheckAndProcessWindowAsync(ct);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка агрегации: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Принудительная обработка накопленного аудио
        /// </summary>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            if (_audioBuffer.Count == 0)
                return;
                
            OnStatusChanged?.Invoke("🔄 Принудительная обработка буфера...");
            
            float[] windowData;
            lock (_audioBuffer)
            {
                windowData = _audioBuffer.ToArray();
                _audioBuffer.Clear();
            }
            
            if (windowData.Length > 0)
            {
                await ProcessAudioWindowAsync(windowData, ct);
            }
        }
        
        /// <summary>
        /// Получение статистики работы агрегатора
        /// </summary>
        public AggregatorStatistics GetStatistics()
        {
            return new AggregatorStatistics
            {
                ProcessedSegments = _processedSegments,
                DroppedSilentSegments = _droppedSilentSegments,
                AverageRms = _averageRms,
                BufferSamples = _audioBuffer.Count,
                BufferDurationSeconds = (float)_audioBuffer.Count / SAMPLE_RATE,
                ActivityRate = _droppedSilentSegments > 0 ? 
                    (float)_processedSegments / (_processedSegments + _droppedSilentSegments) : 1.0f
            };
        }
        
        #endregion

        #region Private Methods - Window Processing
        
        private async Task CheckAndProcessWindowAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastSlide = (now - _lastSlideTime).TotalSeconds;
            var timeSinceLastActivity = (now - _lastActivityTime).TotalSeconds;
            
            bool shouldProcess = false;
            string reason = "";
            
            // Условия для обработки окна
            if (_audioBuffer.Count >= _windowSamples)
            {
                shouldProcess = true;
                reason = "размер окна достигнут";
            }
            else if (timeSinceLastSlide >= SLIDE_INTERVAL_SEC && _audioBuffer.Count >= _slideSamples)
            {
                shouldProcess = true;
                reason = "интервал сдвига";
            }
            else if (timeSinceLastActivity >= SILENCE_THRESHOLD_SEC && _audioBuffer.Count > 0)
            {
                shouldProcess = true;
                reason = "длительная пауза";
            }
            
            if (shouldProcess)
            {
                OnStatusChanged?.Invoke($"🎯 Обработка окна: {reason} ({_audioBuffer.Count} семплов)");
                await ProcessCurrentWindowAsync(ct);
                _lastSlideTime = now;
            }
        }
        
        private async Task ProcessCurrentWindowAsync(CancellationToken ct)
        {
            float[] windowData;
            
            lock (_audioBuffer)
            {
                if (_audioBuffer.Count == 0)
                    return;
                    
                // Извлечение окна с сохранением перекрытия
                var extractLength = Math.Min(_windowSamples, _audioBuffer.Count);
                windowData = new float[extractLength];
                
                for (int i = 0; i < extractLength; i++)
                {
                    windowData[i] = _audioBuffer[i];
                }
                
                // Удаление обработанной части с сохранением перекрытия
                var removeCount = Math.Max(0, extractLength - _overlapSamples);
                if (removeCount > 0)
                {
                    _audioBuffer.RemoveRange(0, removeCount);
                }
            }
            
            await ProcessAudioWindowAsync(windowData, ct);
        }
        
        private async Task ProcessAudioWindowAsync(float[] windowData, CancellationToken ct)
        {
            if (OnAudioSegmentReady == null)
                return;
                
            try
            {
                // Анализ качества окна
                var windowAnalysis = AnalyzeAudioActivity(windowData);
                
                // Проверка активности - фильтрация тишины
                if (windowAnalysis.RmsLevel < RMS_THRESHOLD && windowAnalysis.SpectralEnergy < RMS_THRESHOLD * 2)
                {
                    _droppedSilentSegments++;
                    OnStatusChanged?.Invoke($"🔇 Пропуск тишины (RMS: {windowAnalysis.RmsLevel:F6})");
                    return;
                }
                
                // Отправка на STT
                var recognizedText = await OnAudioSegmentReady(windowData, ct);
                
                if (!string.IsNullOrWhiteSpace(recognizedText))
                {
                    await ProcessRecognizedTextAsync(recognizedText);
                    _processedSegments++;
                }
                
                OnStatusChanged?.Invoke($"✅ Окно обработано: {windowData.Length} семплов → '{recognizedText?.Substring(0, Math.Min(recognizedText.Length, 50))}...'");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка обработки окна: {ex.Message}");
            }
        }
        
        #endregion

        #region Private Methods - Text Processing
        
        private async Task ProcessRecognizedTextAsync(string text)
        {
            // Очистка и нормализация текста
            var cleanedText = CleanAndNormalizeText(text);
            if (string.IsNullOrWhiteSpace(cleanedText))
                return;
                
            // Агрегация текста с умным слиянием
            lock (_textAccumulator)
            {
                if (_textAccumulator.Length > 0)
                {
                    // Проверка на дублирование/перекрытие
                    if (!IsTextDuplicate(cleanedText))
                    {
                        // Добавление разделителя если нужно
                        if (!EndsWithSentenceTerminator(_textAccumulator.ToString()))
                        {
                            _textAccumulator.Append(' ');
                        }
                        _textAccumulator.Append(cleanedText);
                    }
                }
                else
                {
                    _textAccumulator.Append(cleanedText);
                }
                
                // Проверка готовности для отправки
                CheckAndFlushAccumulatedText();
            }
        }
        
        private string CleanAndNormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
                
            // Удаление placeholder токенов
            text = text.Trim();
            
            // Фильтрация мусорных символов и коротких фрагментов
            if (text.Length < 3 || 
                text.All(c => !char.IsLetter(c)) ||
                IsPlaceholderToken(text))
            {
                return string.Empty;
            }
            
            // Нормализация пробелов
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            
            return text;
        }
        
        private bool IsPlaceholderToken(string text)
        {
            var placeholders = new[]
            {
                "спасибо за просмотр", "подписывайтесь", "лайк", "комментарий",
                "субтитры", "автор", "перевод", "озвучка", "музыка",
                "♪", "♫", "🎵", "🎶", "[музыка]", "[смех]", "[аплодисменты]"
            };
            
            return placeholders.Any(p => text.ToLowerInvariant().Contains(p.ToLowerInvariant()));
        }
        
        private bool IsTextDuplicate(string newText)
        {
            if (_textAccumulator.Length == 0)
                return false;
                
            var currentText = _textAccumulator.ToString();
            var lastWords = GetLastWords(currentText, 5);
            var firstWords = GetFirstWords(newText, 5);
            
            // Простая проверка перекрытия последних и первых слов
            return lastWords.Length > 0 && firstWords.Length > 0 && 
                   lastWords.Intersect(firstWords).Count() >= Math.Min(2, Math.Min(lastWords.Length, firstWords.Length));
        }
        
        private string[] GetLastWords(string text, int count)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();
                
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .TakeLast(count)
                      .ToArray();
        }
        
        private string[] GetFirstWords(string text, int count)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();
                
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Take(count)
                      .ToArray();
        }
        
        private bool EndsWithSentenceTerminator(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            var lastChar = text.TrimEnd().LastOrDefault();
            return lastChar == '.' || lastChar == '!' || lastChar == '?' || lastChar == ':';
        }
        
        private void CheckAndFlushAccumulatedText()
        {
            var currentText = _textAccumulator.ToString().Trim();
            
            // Условия для отправки накопленного текста
            bool shouldFlush = false;
            
            if (currentText.Length >= MAX_TEXT_LENGTH)
            {
                shouldFlush = true;
            }
            else if (currentText.Length > 50 && EndsWithSentenceTerminator(currentText))
            {
                shouldFlush = true;
            }
            
            if (shouldFlush && currentText.Length > 0)
            {
                OnTextReady?.Invoke(currentText);
                _textAccumulator.Clear();
                OnStatusChanged?.Invoke($"📝 Текст отправлен: {currentText.Length} символов");
            }
        }
        
        #endregion

        #region Private Methods - Audio Analysis
        
        private AudioAnalysisResult AnalyzeAudioActivity(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return new AudioAnalysisResult();
                
            // RMS вычисление
            double sumSquares = 0;
            float maxAmplitude = 0;
            
            for (int i = 0; i < audioData.Length; i++)
            {
                var sample = audioData[i];
                sumSquares += sample * sample;
                maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sample));
            }
            
            var rmsLevel = Math.Sqrt(sumSquares / audioData.Length);
            
            // Простое спектральное энергия (высокочастотная активность)
            double spectralEnergy = 0;
            for (int i = 1; i < audioData.Length; i++)
            {
                var diff = audioData[i] - audioData[i - 1];
                spectralEnergy += diff * diff;
            }
            spectralEnergy = Math.Sqrt(spectralEnergy / (audioData.Length - 1));
            
            // Детекция клиппинга
            var clippingRate = audioData.Count(s => Math.Abs(s) > 0.95f) / (float)audioData.Length;
            
            return new AudioAnalysisResult
            {
                RmsLevel = (float)rmsLevel,
                MaxAmplitude = maxAmplitude,
                SpectralEnergy = (float)spectralEnergy,
                ClippingRate = clippingRate,
                SampleCount = audioData.Length,
                DurationSeconds = (float)audioData.Length / SAMPLE_RATE,
                IsActive = rmsLevel > RMS_THRESHOLD
            };
        }
        
        private void UpdateActivityTracking(AudioAnalysisResult analysis)
        {
            // Обновление скользящего среднего RMS
            _averageRms = _averageRms * 0.9f + analysis.RmsLevel * 0.1f;
            
            // Обновление времени последней активности
            if (analysis.IsActive)
            {
                _lastActivityTime = DateTime.UtcNow;
            }
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (_isDisposed)
                return;
                
            _isDisposed = true;
            
            // Обработка остатков буфера
            try
            {
                FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Игнорируем ошибки при dispose
            }
            
            lock (_audioBuffer)
            {
                _audioBuffer.Clear();
                _textAccumulator.Clear();
            }
        }
        
        #endregion
    }

    #region Supporting Types
    
    public class AudioAnalysisResult
    {
        public float RmsLevel { get; set; }
        public float MaxAmplitude { get; set; }
        public float SpectralEnergy { get; set; }
        public float ClippingRate { get; set; }
        public int SampleCount { get; set; }
        public float DurationSeconds { get; set; }
        public bool IsActive { get; set; }
        
        public override string ToString()
        {
            return $"RMS: {RmsLevel:F6}, Max: {MaxAmplitude:F3}, Spectral: {SpectralEnergy:F6}, " +
                   $"Clipping: {ClippingRate:P1}, Duration: {DurationSeconds:F2}s, Active: {IsActive}";
        }
    }
    
    public class AggregatorStatistics
    {
        public int ProcessedSegments { get; set; }
        public int DroppedSilentSegments { get; set; }
        public float AverageRms { get; set; }
        public int BufferSamples { get; set; }
        public float BufferDurationSeconds { get; set; }
        public float ActivityRate { get; set; }
        
        public override string ToString()
        {
            return $"Обработано: {ProcessedSegments}, Пропущено: {DroppedSilentSegments}, " +
                   $"Активность: {ActivityRate:P1}, Буфер: {BufferDurationSeconds:F2}с, Ср.RMS: {AverageRms:F6}";
        }
    }
    
    #endregion
}