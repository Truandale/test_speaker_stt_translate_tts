using System.Speech.Synthesis;
using System.Threading.Channels;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Единый TTS воркер с очередью для стабильной озвучки без блокировок
    /// </summary>
    public class StableTtsEngine : IDisposable
    {
        #region Configuration
        
        private const int QUEUE_CAPACITY = 32;
        private const int MAX_TEXT_LENGTH = 500; // Максимальная длина для одного TTS
        private const int MIN_TEXT_LENGTH = 5; // Минимальная длина для озвучки
        private const int SPEECH_RATE = 0; // Скорость речи (-10 to 10)
        private const int SPEECH_VOLUME = 100; // Громкость (0 to 100)
        
        // Таймауты и задержки
        private const int SYNTHESIS_TIMEOUT_MS = 10000; // Максимальное время синтеза
        private const int QUEUE_PROCESSING_DELAY_MS = 50; // Задержка между элементами очереди
        
        #endregion

        #region Private Fields
        
        private readonly SpeechSynthesizer _synthesizer;
        private readonly Channel<TtsRequest> _ttsQueue;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        
        private Task? _processingTask;
        private bool _isDisposed = false;
        private bool _isInitialized = false;
        
        // Статистика
        private int _processedRequests = 0;
        private int _failedRequests = 0;
        private int _queuedRequests = 0;
        private DateTime _lastSpeechTime = DateTime.UtcNow;
        
        // Кеш голосов для быстрого переключения
        private readonly Dictionary<string, VoiceInfo> _voiceCache = new();
        private string _currentLanguage = "en-US";
        private VoiceInfo? _currentVoice;
        
        #endregion

        #region Events
        
        public event Action<string>? OnSpeechStarted;
        public event Action<string>? OnSpeechCompleted;
        public event Action<string>? OnSpeechFailed;
        public event Action<string>? OnStatusChanged;
        public event Action<TtsStatistics>? OnStatisticsUpdated;
        
        #endregion

        public StableTtsEngine()
        {
            // Инициализация синтезатора
            _synthesizer = new SpeechSynthesizer();
            
            // Настройка событий
            _synthesizer.SpeakStarted += OnSynthesizerSpeakStarted;
            _synthesizer.SpeakCompleted += OnSynthesizerSpeakCompleted;
            
            // Создание очереди с backpressure
            var queueOptions = new BoundedChannelOptions(QUEUE_CAPACITY)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest // Дропаем старые при переполнении
            };
            
            _ttsQueue = Channel.CreateBounded<TtsRequest>(queueOptions);
            
            // Инициализация голосов
            InitializeVoices();
            
            // Запуск обработчика очереди
            StartProcessing();
            
            OnStatusChanged?.Invoke("🎤 TTS Engine инициализирован");
        }

        #region Public Methods
        
        /// <summary>
        /// Добавление текста в очередь для озвучки
        /// </summary>
        public async Task<bool> SpeakAsync(string text, string? language = null, CancellationToken ct = default)
        {
            if (_isDisposed || string.IsNullOrWhiteSpace(text))
                return false;
                
            try
            {
                // Очистка и подготовка текста
                var cleanedText = CleanTextForSynthesis(text);
                if (string.IsNullOrWhiteSpace(cleanedText) || cleanedText.Length < MIN_TEXT_LENGTH)
                {
                    OnStatusChanged?.Invoke($"🚫 Текст слишком короткий для TTS: '{text}'");
                    return false;
                }
                
                // Разбивка длинного текста на части
                var textParts = SplitTextIntoChunks(cleanedText, MAX_TEXT_LENGTH);
                
                foreach (var part in textParts)
                {
                    var request = new TtsRequest
                    {
                        Text = part,
                        Language = language ?? _currentLanguage,
                        Priority = TtsPriority.Normal,
                        RequestId = Guid.NewGuid(),
                        Timestamp = DateTime.UtcNow
                    };
                    
                    // Добавление в очередь
                    try
                    {
                        await _ttsQueue.Writer.WriteAsync(request, ct);
                        _queuedRequests++;
                        OnStatusChanged?.Invoke($"📝 Добавлено в очередь TTS: '{part.Substring(0, Math.Min(part.Length, 50))}...' [{_queuedRequests} в очереди]");
                    }
                    catch (Exception)
                    {
                        OnSpeechFailed?.Invoke($"Не удалось добавить в очередь: {part}");
                        return false;
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                OnSpeechFailed?.Invoke($"Ошибка добавления в TTS: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Изменение языка для последующих синтезов
        /// </summary>
        public bool SetLanguage(string language)
        {
            if (_voiceCache.TryGetValue(language, out var voice))
            {
                _currentLanguage = language;
                _currentVoice = voice;
                OnStatusChanged?.Invoke($"🌐 Язык TTS изменен на: {language} ({voice.Name})");
                return true;
            }
            
            OnStatusChanged?.Invoke($"❌ Язык TTS не поддерживается: {language}");
            return false;
        }
        
        /// <summary>
        /// Очистка очереди TTS
        /// </summary>
        public async Task ClearQueueAsync()
        {
            // Сбрасываем все из очереди
            while (_ttsQueue.Reader.TryRead(out _))
            {
                _queuedRequests = Math.Max(0, _queuedRequests - 1);
            }
            
            // Останавливаем текущий синтез
            try
            {
                _synthesizer.SpeakAsyncCancelAll();
            }
            catch
            {
                // Игнорируем ошибки отмены
            }
            
            OnStatusChanged?.Invoke("🗑️ Очередь TTS очищена");
        }
        
        /// <summary>
        /// Получение статистики работы TTS
        /// </summary>
        public TtsStatistics GetStatistics()
        {
            return new TtsStatistics
            {
                ProcessedRequests = _processedRequests,
                FailedRequests = _failedRequests,
                QueuedRequests = _queuedRequests,
                LastSpeechTime = _lastSpeechTime,
                CurrentLanguage = _currentLanguage,
                AvailableLanguages = _voiceCache.Keys.ToArray(),
                IsProcessing = _processingTask?.Status == TaskStatus.Running,
                QueueCapacity = QUEUE_CAPACITY
            };
        }
        
        /// <summary>
        /// Проверка поддерживаемых языков
        /// </summary>
        public string[] GetSupportedLanguages()
        {
            return _voiceCache.Keys.ToArray();
        }
        
        #endregion

        #region Private Methods - Initialization
        
        private void InitializeVoices()
        {
            try
            {
                foreach (var voice in _synthesizer.GetInstalledVoices())
                {
                    if (voice.Enabled && voice.VoiceInfo != null)
                    {
                        var culture = voice.VoiceInfo.Culture.Name;
                        if (!_voiceCache.ContainsKey(culture))
                        {
                            _voiceCache[culture] = voice.VoiceInfo;
                            OnStatusChanged?.Invoke($"🎭 Обнаружен голос: {culture} - {voice.VoiceInfo.Name}");
                        }
                    }
                }
                
                // Установка дефолтного голоса
                if (_voiceCache.ContainsKey("en-US"))
                {
                    SetLanguage("en-US");
                }
                else if (_voiceCache.ContainsKey("ru-RU"))
                {
                    SetLanguage("ru-RU");
                }
                else if (_voiceCache.Any())
                {
                    SetLanguage(_voiceCache.Keys.First());
                }
                
                _isInitialized = true;
                OnStatusChanged?.Invoke($"✅ TTS инициализация завершена. Доступно языков: {_voiceCache.Count}");
            }
            catch (Exception ex)
            {
                OnSpeechFailed?.Invoke($"Ошибка инициализации TTS: {ex.Message}");
            }
        }
        
        private void StartProcessing()
        {
            _processingTask = Task.Run(async () => await ProcessTtsQueueAsync(_cancellationTokenSource.Token));
        }
        
        #endregion

        #region Private Methods - Queue Processing
        
        private async Task ProcessTtsQueueAsync(CancellationToken ct)
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            OnStatusChanged?.Invoke("🔄 TTS обработчик очереди запущен");
            
            try
            {
                await foreach (var request in _ttsQueue.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        _queuedRequests = Math.Max(0, _queuedRequests - 1);
                        await ProcessSingleRequestAsync(request, ct);
                        
                        // Небольшая задержка между синтезами для стабильности
                        if (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(QUEUE_PROCESSING_DELAY_MS, ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _failedRequests++;
                        OnSpeechFailed?.Invoke($"Ошибка обработки TTS запроса: {ex.Message}");
                    }
                    
                    // Обновление статистики
                    OnStatisticsUpdated?.Invoke(GetStatistics());
                }
            }
            catch (OperationCanceledException)
            {
                OnStatusChanged?.Invoke("⏹️ TTS обработчик остановлен");
            }
            catch (Exception ex)
            {
                OnSpeechFailed?.Invoke($"Критическая ошибка TTS обработчика: {ex.Message}");
            }
        }
        
        private async Task ProcessSingleRequestAsync(TtsRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return;
                
            try
            {
                // Переключение языка если нужно
                if (request.Language != _currentLanguage && _voiceCache.ContainsKey(request.Language))
                {
                    SetLanguage(request.Language);
                }
                
                // Настройка синтезатора
                ConfigureSynthesizer();
                
                OnSpeechStarted?.Invoke(request.Text);
                
                // Синтез с таймаутом
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(SYNTHESIS_TIMEOUT_MS);
                
                await SynthesizeWithTimeoutAsync(request.Text, timeoutCts.Token);
                
                _processedRequests++;
                _lastSpeechTime = DateTime.UtcNow;
                
                OnSpeechCompleted?.Invoke(request.Text);
            }
            catch (OperationCanceledException)
            {
                _synthesizer.SpeakAsyncCancelAll();
                OnStatusChanged?.Invoke($"⏸️ TTS синтез отменен: {request.Text.Substring(0, Math.Min(request.Text.Length, 50))}...");
            }
            catch (Exception ex)
            {
                _failedRequests++;
                OnSpeechFailed?.Invoke($"Ошибка синтеза '{request.Text}': {ex.Message}");
            }
        }
        
        private void ConfigureSynthesizer()
        {
            try
            {
                // Установка голоса
                if (_currentVoice != null)
                {
                    _synthesizer.SelectVoice(_currentVoice.Name);
                }
                
                // Настройка параметров
                _synthesizer.Rate = SPEECH_RATE;
                _synthesizer.Volume = SPEECH_VOLUME;
                
                // Настройка аудио выхода на дефолтное устройство
                _synthesizer.SetOutputToDefaultAudioDevice();
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"⚠️ Ошибка настройки синтезатора: {ex.Message}");
            }
        }
        
        private async Task SynthesizeWithTimeoutAsync(string text, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            
            EventHandler<SpeakCompletedEventArgs>? completedHandler = null;
            completedHandler = (s, e) =>
            {
                _synthesizer.SpeakCompleted -= completedHandler;
                if (e.Error != null)
                {
                    tcs.SetException(e.Error);
                }
                else if (e.Cancelled)
                {
                    tcs.SetCanceled();
                }
                else
                {
                    tcs.SetResult(true);
                }
            };
            
            try
            {
                _synthesizer.SpeakCompleted += completedHandler;
                
                // Запуск асинхронного синтеза
                _synthesizer.SpeakAsync(text);
                
                // Ожидание завершения с возможностью отмены
                await tcs.Task.WaitAsync(ct);
            }
            finally
            {
                _synthesizer.SpeakCompleted -= completedHandler;
            }
        }
        
        #endregion

        #region Private Methods - Text Processing
        
        private string CleanTextForSynthesis(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
                
            // Удаление лишних символов и нормализация
            text = text.Trim();
            
            // Удаление URL и email
            text = Regex.Replace(text, @"http[s]?://[^\s]+", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "", RegexOptions.IgnoreCase);
            
            // Замена символов, которые могут вызвать проблемы в TTS
            text = text.Replace("&", " и ");
            text = text.Replace("@", " at ");
            text = text.Replace("#", " номер ");
            text = text.Replace("$", " доллар ");
            text = text.Replace("%", " процент ");
            
            // Удаление лишних пробелов
            text = Regex.Replace(text, @"\s+", " ");
            
            // Удаление специальных маркеров
            text = Regex.Replace(text, @"\[.*?\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\(.*?\)", "", RegexOptions.IgnoreCase);
            
            return text.Trim();
        }
        
        private List<string> SplitTextIntoChunks(string text, int maxLength)
        {
            var chunks = new List<string>();
            
            if (text.Length <= maxLength)
            {
                chunks.Add(text);
                return chunks;
            }
            
            // Разбивка по предложениям
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+", RegexOptions.Multiline);
            
            var currentChunk = new StringBuilder();
            
            foreach (var sentence in sentences)
            {
                // Если предложение само по себе слишком длинное
                if (sentence.Length > maxLength)
                {
                    // Сохраняем текущий chunk если есть
                    if (currentChunk.Length > 0)
                    {
                        chunks.Add(currentChunk.ToString().Trim());
                        currentChunk.Clear();
                    }
                    
                    // Разбиваем длинное предложение по запятым или пробелам
                    var longSentenceParts = SplitLongSentence(sentence, maxLength);
                    chunks.AddRange(longSentenceParts);
                }
                else
                {
                    // Проверяем, поместится ли предложение в текущий chunk
                    if (currentChunk.Length + sentence.Length + 1 > maxLength)
                    {
                        // Сохраняем текущий chunk и начинаем новый
                        if (currentChunk.Length > 0)
                        {
                            chunks.Add(currentChunk.ToString().Trim());
                            currentChunk.Clear();
                        }
                    }
                    
                    if (currentChunk.Length > 0)
                    {
                        currentChunk.Append(" ");
                    }
                    currentChunk.Append(sentence);
                }
            }
            
            // Добавляем последний chunk
            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
            }
            
            return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }
        
        private List<string> SplitLongSentence(string sentence, int maxLength)
        {
            var parts = new List<string>();
            
            // Попытка разбить по запятым
            var commaParts = sentence.Split(',');
            var currentPart = new StringBuilder();
            
            foreach (var part in commaParts)
            {
                if (currentPart.Length + part.Length + 1 > maxLength)
                {
                    if (currentPart.Length > 0)
                    {
                        parts.Add(currentPart.ToString().Trim());
                        currentPart.Clear();
                    }
                    
                    // Если часть все еще слишком длинная, разбиваем по словам
                    if (part.Length > maxLength)
                    {
                        parts.AddRange(SplitByWords(part, maxLength));
                    }
                    else
                    {
                        currentPart.Append(part);
                    }
                }
                else
                {
                    if (currentPart.Length > 0)
                    {
                        currentPart.Append(", ");
                    }
                    currentPart.Append(part);
                }
            }
            
            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString().Trim());
            }
            
            return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }
        
        private List<string> SplitByWords(string text, int maxLength)
        {
            var parts = new List<string>();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var currentPart = new StringBuilder();
            
            foreach (var word in words)
            {
                if (currentPart.Length + word.Length + 1 > maxLength)
                {
                    if (currentPart.Length > 0)
                    {
                        parts.Add(currentPart.ToString().Trim());
                        currentPart.Clear();
                    }
                    
                    // Если слово само слишком длинное, принудительно разрезаем
                    if (word.Length > maxLength)
                    {
                        parts.Add(word.Substring(0, maxLength));
                    }
                    else
                    {
                        currentPart.Append(word);
                    }
                }
                else
                {
                    if (currentPart.Length > 0)
                    {
                        currentPart.Append(" ");
                    }
                    currentPart.Append(word);
                }
            }
            
            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString().Trim());
            }
            
            return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }
        
        #endregion

        #region Event Handlers
        
        private void OnSynthesizerSpeakStarted(object? sender, SpeakStartedEventArgs e)
        {
            OnStatusChanged?.Invoke($"🔊 Начало синтеза TTS");
        }
        
        private void OnSynthesizerSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка синтеза: {e.Error.Message}");
            }
            else if (e.Cancelled)
            {
                OnStatusChanged?.Invoke($"⏸️ Синтез отменен");
            }
            else
            {
                OnStatusChanged?.Invoke($"✅ Синтез завершен");
            }
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (_isDisposed)
                return;
                
            _isDisposed = true;
            
            OnStatusChanged?.Invoke("🛑 Остановка TTS Engine...");
            
            // Отмена всех операций
            _cancellationTokenSource.Cancel();
            
            // Остановка синтеза
            try
            {
                _synthesizer.SpeakAsyncCancelAll();
            }
            catch
            {
                // Игнорируем ошибки при dispose
            }
            
            // Закрытие очереди
            _ttsQueue.Writer.TryComplete();
            
            // Ожидание завершения обработки
            try
            {
                _processingTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Таймаут - принудительно завершаем
            }
            
            // Очистка ресурсов
            _synthesizer.Dispose();
            _cancellationTokenSource.Dispose();
            
            OnStatusChanged?.Invoke("✅ TTS Engine остановлен");
        }
        
        #endregion
    }

    #region Supporting Types
    
    public class TtsRequest
    {
        public string Text { get; set; } = string.Empty;
        public string Language { get; set; } = "en-US";
        public TtsPriority Priority { get; set; } = TtsPriority.Normal;
        public Guid RequestId { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    public enum TtsPriority
    {
        Low,
        Normal,
        High,
        Urgent
    }
    
    public class TtsStatistics
    {
        public int ProcessedRequests { get; set; }
        public int FailedRequests { get; set; }
        public int QueuedRequests { get; set; }
        public DateTime LastSpeechTime { get; set; }
        public string CurrentLanguage { get; set; } = string.Empty;
        public string[] AvailableLanguages { get; set; } = Array.Empty<string>();
        public bool IsProcessing { get; set; }
        public int QueueCapacity { get; set; }
        
        public double SuccessRate => ProcessedRequests + FailedRequests > 0 ? 
            (double)ProcessedRequests / (ProcessedRequests + FailedRequests) : 1.0;
            
        public TimeSpan TimeSinceLastSpeech => DateTime.UtcNow - LastSpeechTime;
        
        public override string ToString()
        {
            return $"TTS: {ProcessedRequests} успешно, {FailedRequests} ошибок, {QueuedRequests} в очереди, " +
                   $"Успешность: {SuccessRate:P1}, Язык: {CurrentLanguage}";
        }
    }
    
    #endregion
}