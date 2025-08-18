using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.MediaFoundation;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using System.Speech.Synthesis;
using RestSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace test_speaker_stt_translate_tts
{
    public partial class Form1 : Form
    {
        #region Private Fields
        
        // 🚀 НОВАЯ СТАБИЛЬНАЯ АРХИТЕКТУРА
        private StableAudioCapture? stableAudioCapture;
        private SlidingWindowAggregator? slidingWindowAggregator;
        private StableTtsEngine? stableTtsEngine;
        
        // Legacy fields для совместимости (будут удалены позже)
        private WasapiLoopbackCapture? wasapiCapture;
        private WaveInEvent? waveInCapture;
        private List<byte> audioBuffer = new();
        private bool isCapturing = false;
        private bool isCollectingAudio = false;
        private int audioLogCount = 0; // Для отладки перезапуска
        
        // Семафоры для последовательной обработки
        private readonly SemaphoreSlim audioProcessingSemaphore = new(1, 1);
        private int audioSequenceNumber = 0;
        private readonly SemaphoreSlim ttsProcessingSemaphore = new(1, 1);
        private int ttsSequenceNumber = 0;
        
        // 🚀 CPU ОПТИМИЗАЦИИ: Переменные для умного UI обновления
        private int lastAudioPercentage = -1;
        private DateTime lastUIUpdate = DateTime.MinValue;
        private const int UI_UPDATE_INTERVAL_MS = 200;
        
        // 🚀 CPU ОПТИМИЗАЦИИ: Throttling аудиообработки
        private DateTime lastAudioProcessTime = DateTime.MinValue;
        private const int AUDIO_THROTTLE_MS = 50; // Минимальный интервал между обработкой
        
        // 🚀 CPU ОПТИМИЗАЦИИ: Оптимизированное логирование
        private bool enableDetailedLogging = false; // Отключено по умолчанию для производительности
        
        private volatile bool isTTSActive = false; // Для отслеживания активных TTS операций
        private DateTime lastVoiceActivity = DateTime.Now;
        private DateTime _lastDropLogTime = DateTime.MinValue;
        
        // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Теплый Whisper instance
        private static readonly object _whisperLock = new();
        private static WhisperFactory? _whisperFactory;
        private static WhisperProcessor? _whisperProcessor;
        
        // 🚀 НОВАЯ PIPELINE АРХИТЕКТУРА: Bounded Channels с backpressure
        private readonly Channel<byte[]> _captureChannel = 
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { 
                SingleWriter = true, 
                FullMode = BoundedChannelFullMode.DropOldest 
            });
        private readonly Channel<float[]> _mono16kChannel = 
            Channel.CreateBounded<float[]>(new BoundedChannelOptions(64) { 
                FullMode = BoundedChannelFullMode.DropOldest 
            });
        private readonly Channel<string> _sttChannel = 
            Channel.CreateBounded<string>(new BoundedChannelOptions(64) { 
                FullMode = BoundedChannelFullMode.DropOldest 
            });
        
        // CancellationToken для остановки пайплайна
        private CancellationTokenSource? _pipelineCts;
        private DateTime recordingStartTime = DateTime.Now;
        private float voiceThreshold = 0.05f; // Повысим порог активации
        private int silenceDurationMs = 1000; // Сократим до 1 сек
        private int maxRecordingMs = 5000; // Максимум 5 секунд записи (сократили с 10 сек)
        private System.Windows.Forms.Timer? audioLevelTimer;
        private float currentAudioLevel = 0f;
        
        // Processing mode
        private bool isStreamingMode = false;
        private int currentProcessingMode = 0; // Кэшированное значение для многопоточного доступа
        
        // Smart Audio Management
        private SmartAudioManager? smartAudioManager;
        
        // Новые компоненты для стриминга
        private StreamingWhisperProcessor? streamingProcessor;
        private AudioResampler? audioResampler;
        private bool isDisposed = false;
        
        // User Settings
        private UserSettings userSettings = new UserSettings();
        
        // STT & Translation - Enhanced
        private static string WhisperModelPath => Path.Combine(Application.StartupPath, "models", "whisper", "ggml-small.bin");
        private WhisperFactory? whisperFactory;
        private WhisperProcessor? whisperProcessor;
        private SpeechSynthesizer? speechSynthesizer;
        private TtsVoiceManager? ttsVoiceManager;
        private RestClient? googleTranslateClient;
        
        // UI Elements (может быть null если не в дизайнере)
        private System.Windows.Forms.TextBox? txtRecognized;
        private System.Windows.Forms.TextBox? txtTranslated;
        private System.Windows.Forms.ProgressBar? progressBarAudio;
        private System.Windows.Forms.ComboBox? cbSourceLanguage;
        private System.Windows.Forms.ComboBox? cbTargetLanguage;
        
        // Статистика и мониторинг
        private int totalProcessedFrames = 0;
        private DateTime sessionStartTime = DateTime.Now;
        private System.Windows.Forms.Timer? statisticsTimer;
        
        // Language mappings
        private readonly Dictionary<string, string> languageCodes = new()
        {
            { "Русский", "ru" },
            { "Английский", "en" },
            { "Немецкий", "de" },
            { "Французский", "fr" },
            { "Испанский", "es" },
            { "Итальянский", "it" },
            { "Японский", "ja" },
            { "Китайский", "zh" }
        };

        #endregion

        #region Constructor & Initialization

        public Form1()
        {
            InitializeComponent();
            
            // Подписываемся на событие закрытия формы для корректной очистки ресурсов
            this.FormClosing += Form1_OnFormClosing;
            
            InitializeApplication();
        }

        private void InitializeApplication()
        {
            LogMessage("🚀 Инициализация приложения с новой стабильной архитектурой...");
            
            // 🚀 КРИТИЧЕСКАЯ ИНИЦИАЛИЗАЦИЯ: MediaFoundation для качественного ресемплинга
            try
            {
                MediaFoundationApi.Startup();
                LogMessage("✅ MediaFoundation инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Ошибка инициализации MediaFoundation: {ex.Message}");
            }
            
            // Загружаем пользовательские настройки
            LoadUserSettings();
            
            // 🧪 ТЕСТ ФИЛЬТРА НЕЗАВЕРШЕННЫХ ФРАЗ
            IncompletePhrasesTest.RunTest();
            
            // 🧪 ДЕМОНСТРАЦИЯ: ЗАГЛАВНЫЕ БУКВЫ
            CapitalLetterTest.RunCapitalLetterDemo();
            
            // 🇪🇺 ТЕСТ ЕВРОПЕЙСКИХ ЯЗЫКОВ
            EuropeanLanguageTest.RunAllTests();
            EuropeanLanguageTest.CompareFilters();
            
            // Check Whisper model first
            if (!CheckWhisperModel())
            {
                MessageBox.Show(
                    $"❌ Whisper модель не найдена!\n\n" +
                    $"Ожидаемый путь:\n{WhisperModelPath}\n\n" +
                    $"Пожалуйста, убедитесь что модель ggml-small.bin находится по указанному пути.",
                    "Ошибка инициализации",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            
            // Initialize components
            InitializeAudioDevices();
            InitializeLanguages();
            InitializeSmartAudioManager();
            
            // 🚀 НОВАЯ СТАБИЛЬНАЯ АРХИТЕКТУРА
            InitializeStableComponents();
            
            // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Инициализация Bounded Channels пайплайна
            InitializeBoundedPipeline();
            
            // 🚀 АВТОМАТИЧЕСКОЕ ПЕРЕПОДКЛЮЧЕНИЕ: Мониторинг аудиоустройств
            InitializeDeviceNotifications();
            
            InitializeTranslation();
            InitializeTimer();
            InitializeProcessingMode();
            InitializeStreamingComponents();
            
            // Set default threshold
            voiceThreshold = (float)numThreshold.Value;
            numThreshold.ValueChanged += (s, e) => {
                voiceThreshold = (float)numThreshold.Value;
                userSettings.VoiceThreshold = voiceThreshold;
                OnSettingChanged();
            };
            
            // Подписываемся на события изменения настроек
            SubscribeToSettingsEvents();
            
            // Применяем сохраненные настройки к элементам управления
            ApplySettingsAfterInitialization();
            
            LogMessage("✅ Приложение готово к работе (стабильная архитектура активна)");
        }

        private bool CheckWhisperModel()
        {
            try
            {
                if (File.Exists(WhisperModelPath))
                {
                    var fileInfo = new FileInfo(WhisperModelPath);
                    LogMessage($"✅ Whisper модель найдена: {fileInfo.Length / 1024 / 1024:F1} MB");
                    return true;
                }
                else
                {
                    LogMessage($"❌ Whisper модель не найдена: {WhisperModelPath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка проверки Whisper модели: {ex.Message}");
                return false;
            }
        }

        private void InitializeAudioDevices()
        {
            LogMessage("🔍 Поиск аудиоустройств...");
            
            cbSpeakerDevices.Items.Clear();
            
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            foreach (var device in devices)
            {
                string deviceInfo = $"{device.FriendlyName}";
                cbSpeakerDevices.Items.Add(new AudioDevice 
                { 
                    Name = deviceInfo, 
                    Device = device 
                });
                LogMessage($"   📻 {deviceInfo}");
            }
            
            if (cbSpeakerDevices.Items.Count > 0)
            {
                cbSpeakerDevices.SelectedIndex = 0;
                LogMessage($"✅ Найдено {cbSpeakerDevices.Items.Count} аудиоустройств");
            }
            else
            {
                LogMessage("❌ Аудиоустройства не найдены!");
            }
        }

        private void InitializeLanguages()
        {
            // Добавляем "Автоопределение" как первый вариант для исходного языка
            cbSourceLang.Items.Add("Автоопределение");
            cbSourceLang.Items.AddRange(languageCodes.Keys.ToArray());
            cbTargetLang.Items.AddRange(languageCodes.Keys.ToArray());
            
            cbSourceLang.SelectedItem = "Автоопределение";  // По умолчанию автоопределение
            cbTargetLang.SelectedItem = "Русский";
        }

        private void InitializeSmartAudioManager()
        {
            try
            {
                smartAudioManager = new SmartAudioManager();
                
                // Подписываемся на события
                smartAudioManager.LogMessage += LogMessage;
                smartAudioManager.ProcessAudioSegment += ProcessAudioSegmentFromQueue;
                smartAudioManager.TTSStateChanged += OnTTSStateChanged;
                smartAudioManager.CaptureStateChanged += OnCaptureStateChanged;
                
                LogMessage("✅ SmartAudioManager инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации SmartAudioManager: {ex.Message}");
            }
        }

        private void OnTTSStateChanged(bool isActive)
        {
            // Обновляем UI индикаторы состояния TTS
            Invoke(() => {
                // Можем добавить индикатор состояния TTS в будущем
                LogMessage($"🔊 TTS состояние: {(isActive ? "активен" : "неактивен")}");
            });
        }

        private void OnCaptureStateChanged(bool isActive)
        {
            // Обновляем UI индикаторы состояния захвата
            Invoke(() => {
                LogMessage($"🎤 Захват: {(isActive ? "активен" : "приостановлен")}");
            });
        }

        private async Task ProcessAudioSegmentFromQueue(AudioSegment segment)
        {
            try
            {
                LogMessage($"🔄 Обработка аудио сегмента из очереди: {segment.AudioData.Length} байт (источник: {segment.Source})");
                
                // Конвертируем и обрабатываем аудио ПОСЛЕДОВАТЕЛЬНО
                await ProcessAudioSequentially(segment.AudioData);
                
                LogMessage($"✅ Сегмент из очереди обработан успешно");
                
                // 📚 Показываем статистику аудиокниги каждые 10 обработанных сегментов
                totalProcessedFrames++;
                if (totalProcessedFrames % 10 == 0)
                {
                    ShowAudiobookStatistics();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки сегмента из очереди: {ex.Message}");
            }
        }

        private void InitializeTTS()
        {
            try
            {
                speechSynthesizer = new SpeechSynthesizer();
                speechSynthesizer.Volume = 100;
                speechSynthesizer.Rate = 0;
                
                // Инициализируем менеджер голосов с автоматическим переключением
                ttsVoiceManager = new TtsVoiceManager(speechSynthesizer);
                
                LogMessage("✅ Legacy TTS инициализирован с автоматическим выбором голосов");
                LogMessage($"📢 Доступные голоса: {ttsVoiceManager.GetVoiceInfo()}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации Legacy TTS: {ex.Message}");
            }
        }

        #region 🚀 Новая Стабильная Архитектура

        private void InitializeStableComponents()
        {
            try
            {
                LogMessage("🏗️ Инициализация стабильных компонентов...");
                
                // Инициализация стабильного TTS Engine
                InitializeStableTtsEngine();
                
                // Инициализация агрегатора скользящего окна
                InitializeSlidingWindowAggregator();
                
                // Инициализация стабильного аудио-захвата
                InitializeStableAudioCapture();
                
                // Инициализация Whisper
                InitializeWhisperComponents();
                
                // Таймер статистики
                InitializeStatisticsTimer();
                
                LogMessage("✅ Все стабильные компоненты инициализированы");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации стабильных компонентов: {ex.Message}");
            }
        }

        private void InitializeStableTtsEngine()
        {
            try
            {
                stableTtsEngine = new StableTtsEngine();
                
                // Подписка на события
                stableTtsEngine.OnSpeechStarted += (text) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"🎙️ Начало озвучки: {text.Substring(0, Math.Min(text.Length, 50))}...");
                        isTTSActive = true;
                    });
                };
                
                stableTtsEngine.OnSpeechCompleted += (text) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"✅ Озвучка завершена: {text.Substring(0, Math.Min(text.Length, 50))}...");
                        isTTSActive = false;
                    });
                };
                
                stableTtsEngine.OnSpeechFailed += (error) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"❌ Ошибка TTS: {error}");
                        isTTSActive = false;
                    });
                };
                
                stableTtsEngine.OnStatusChanged += (status) => 
                {
                    this.BeginInvoke(() => LogMessage(status));
                };
                
                stableTtsEngine.OnStatisticsUpdated += (stats) => 
                {
                    this.BeginInvoke(() => 
                    {
                        // Обновление UI со статистикой TTS можно добавить здесь
                    });
                };
                
                LogMessage("✅ Стабильный TTS Engine инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации стабильного TTS: {ex.Message}");
            }
        }

        private void InitializeSlidingWindowAggregator()
        {
            try
            {
                slidingWindowAggregator = new SlidingWindowAggregator();
                
                // Подписка на события
                slidingWindowAggregator.OnAudioSegmentReady += async (audioData, ct) => 
                {
                    return await ProcessAudioWithWhisper(audioData, ct);
                };
                
                slidingWindowAggregator.OnTextReady += async (text) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"📝 Готов текст для перевода: {text}");
                    });
                    
                    await ProcessRecognizedText(text);
                };
                
                slidingWindowAggregator.OnStatusChanged += (status) => 
                {
                    this.BeginInvoke(() => LogMessage(status));
                };
                
                slidingWindowAggregator.OnAudioAnalysis += (analysis) => 
                {
                    this.BeginInvoke(() => 
                    {
                        // Обновление аудио-анализа в UI
                        currentAudioLevel = analysis.RmsLevel;
                        UpdateAudioLevelUI();
                    });
                };
                
                LogMessage("✅ Агрегатор скользящего окна инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации агрегатора: {ex.Message}");
            }
        }

        private void InitializeStableAudioCapture()
        {
            try
            {
                stableAudioCapture = new StableAudioCapture();
                
                // Подписка на события
                stableAudioCapture.OnTextRecognized += (text) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"🎯 STT результат: {text}");
                    });
                };
                
                stableAudioCapture.OnTextTranslated += (text) => 
                {
                    this.BeginInvoke(() => 
                    {
                        LogMessage($"🌐 Переведено: {text}");
                    });
                };
                
                stableAudioCapture.OnError += (error) => 
                {
                    this.BeginInvoke(() => LogMessage($"❌ Ошибка захвата: {error}"));
                };
                
                stableAudioCapture.OnStatusChanged += (status) => 
                {
                    this.BeginInvoke(() => LogMessage(status));
                };
                
                LogMessage("✅ Стабильный аудио-захват инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации стабильного захвата: {ex.Message}");
            }
        }

        private void InitializeWhisperComponents()
        {
            try
            {
                if (File.Exists(WhisperModelPath))
                {
                    whisperFactory = WhisperFactory.FromPath(WhisperModelPath);
                    whisperProcessor = whisperFactory.CreateBuilder()
                        .WithLanguage("ru") // Фиксированный русский для стабильности
                        .WithProbabilities()
                        .Build();
                    
                    LogMessage("✅ Whisper компоненты инициализированы (русский язык)");
                }
                else
                {
                    LogMessage("⚠️ Whisper модель не найдена, STT недоступен");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации Whisper: {ex.Message}");
            }
        }

        private void InitializeStatisticsTimer()
        {
            try
            {
                statisticsTimer = new System.Windows.Forms.Timer();
                statisticsTimer.Interval = 5000; // Каждые 5 секунд
                statisticsTimer.Tick += StatisticsTimer_Tick;
                
                LogMessage("✅ Таймер статистики инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации таймера статистики: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Инициализация разделенного пайплайна с Bounded Channels
        /// Устраняет блокировки и дает контролируемые дропы при перегрузе
        /// </summary>
        private void InitializeBoundedPipeline()
        {
            try
            {
                LogMessage("🚀 Инициализация Bounded Channels пайплайна...");
                
                // Запускаем воркеры пайплайна
                StartNormalizationWorker();
                StartSttWorker();
                StartTextProcessorWorker();
                
                LogMessage("✅ Bounded Channels пайплайн запущен");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации пайплайна: {ex.Message}");
            }
        }

        /// <summary>
        /// Воркер нормализации: capture → normalize → 16k mono float
        /// </summary>
        private void StartNormalizationWorker()
        {
            _pipelineCts = new CancellationTokenSource();
            var ct = _pipelineCts.Token;
            
            _ = Task.Run(async () =>
            {
                LogMessage("🔄 Воркер нормализации запущен");
                
                await foreach (var rawBuffer in _captureChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // Определяем входной формат (WASAPI loopback 44100Hz stereo float32)
                        var inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                        var wavData = ConvertToWavNormalized(rawBuffer, inputFormat);
                        
                        if (wavData.Length > 44) // Проверяем WAV заголовок
                        {
                            // Извлекаем float32 данные, пропуская WAV заголовок
                            var floatData = new float[(wavData.Length - 44) / 4];
                            Buffer.BlockCopy(wavData, 44, floatData, 0, wavData.Length - 44);
                            
                            // Отправляем в следующий этап с backpressure
                            if (!_mono16kChannel.Writer.TryWrite(floatData))
                            {
                                LogMessage("⚠️ 🔴 ДРОП: Нормализация - канал 16kHz переполнен! Старые данные сброшены");
                            }
                            else
                            {
                                int queueEstimate = _mono16kChannel.Reader.Count;
                                LogMessageDebug($"🔊 16kHz данные отправлены в канал, очередь ≈{queueEstimate}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка нормализации: {ex.Message}");
                    }
                }
                
                LogMessage("🔄 Воркер нормализации остановлен");
            }, ct);
        }

        /// <summary>
        /// STT воркер: 16k mono float → Whisper STT → текст
        /// </summary>
        private void StartSttWorker()
        {
            var ct = _pipelineCts?.Token ?? CancellationToken.None;
            
            _ = Task.Run(async () =>
            {
                LogMessage("🔄 STT воркер запущен");
                EnsureWhisperReady(); // Подготавливаем теплый Whisper
                
                await foreach (var monoFloat in _mono16kChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // Создаем временный WAV файл для Whisper
                        string tempFile = Path.GetTempFileName();
                        try
                        {
                            // Создаем WAV файл с правильным заголовком
                            var wavBytes = CreateWavFromFloats(monoFloat, 16000, 1);
                            await File.WriteAllBytesAsync(tempFile, wavBytes, ct);
                            
                            // STT через теплый Whisper
                            using var fileStream = File.OpenRead(tempFile);
                            var result = new StringBuilder();
                            
                            await foreach (var segment in _whisperProcessor!.ProcessAsync(fileStream))
                            {
                                if (!string.IsNullOrWhiteSpace(segment.Text))
                                {
                                    string cleanText = segment.Text.Trim();
                                    if (!IsPlaceholderToken(cleanText))
                                    {
                                        result.Append(cleanText + " ");
                                    }
                                }
                            }
                            
                            string finalText = result.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(finalText))
                            {
                                // Отправляем в следующий этап с backpressure
                                if (!_sttChannel.Writer.TryWrite(finalText))
                                {
                                    LogMessage($"⚠️ 🔴 ДРОП: STT канал переполнен! Текст сброшен: '{finalText.Substring(0, Math.Min(50, finalText.Length))}...'");
                                }
                                else
                                {
                                    int queueEstimate = _sttChannel.Reader.Count;
                                    LogMessageDebug($"💬 STT текст отправлен в канал, очередь ≈{queueEstimate}");
                                }
                            }
                        }
                        finally
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка STT: {ex.Message}");
                    }
                }
                
                LogMessage("🔄 STT воркер остановлен");
            }, ct);
        }

        /// <summary>
        /// Воркер обработки текста: текст → перевод → TTS
        /// </summary>
        private void StartTextProcessorWorker()
        {
            var ct = _pipelineCts?.Token ?? CancellationToken.None;
            
            _ = Task.Run(async () =>
            {
                LogMessage("🔄 Воркер обработки текста запущен");
                
                await foreach (var recognizedText in _sttChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // Обновляем UI
                        Invoke(() => {
                            txtRecognizedText.Text = recognizedText;
                            LogMessage($"✅ Распознан текст: '{recognizedText}'");
                        });
                        
                        // Автоперевод если включен
                        bool autoTranslate = false;
                        Invoke(() => autoTranslate = chkAutoTranslate.Checked);
                        
                        if (autoTranslate)
                        {
                            await TranslateAndSpeak(recognizedText);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка обработки текста: {ex.Message}");
                    }
                }
                
                LogMessage("🔄 Воркер обработки текста остановлен");
            }, ct);
        }

        /// <summary>
        /// Создает WAV файл из float32 массива
        /// </summary>
        private byte[] CreateWavFromFloats(float[] floats, int sampleRate, int channels)
        {
            var wav = new List<byte>();
            
            // WAV заголовок
            int dataSize = floats.Length * 2; // 16-bit = 2 bytes per sample
            int fileSize = 36 + dataSize;
            
            wav.AddRange(Encoding.ASCII.GetBytes("RIFF"));
            wav.AddRange(BitConverter.GetBytes(fileSize));
            wav.AddRange(Encoding.ASCII.GetBytes("WAVE"));
            wav.AddRange(Encoding.ASCII.GetBytes("fmt "));
            wav.AddRange(BitConverter.GetBytes(16)); // PCM chunk size
            wav.AddRange(BitConverter.GetBytes((short)1)); // PCM format
            wav.AddRange(BitConverter.GetBytes((short)channels));
            wav.AddRange(BitConverter.GetBytes(sampleRate));
            wav.AddRange(BitConverter.GetBytes(sampleRate * channels * 2)); // Byte rate
            wav.AddRange(BitConverter.GetBytes((short)(channels * 2))); // Block align
            wav.AddRange(BitConverter.GetBytes((short)16)); // 16-bit
            wav.AddRange(Encoding.ASCII.GetBytes("data"));
            wav.AddRange(BitConverter.GetBytes(dataSize));
            
            // Конвертируем float32 в int16
            foreach (var sample in floats)
            {
                float clamped = Math.Max(-1.0f, Math.Min(1.0f, sample));
                short intSample = (short)(clamped * 32767f);
                wav.AddRange(BitConverter.GetBytes(intSample));
            }
            
            return wav.ToArray();
        }

        private void InitializeTranslation()
        {
            try
            {
                googleTranslateClient = new RestClient("https://translate.googleapis.com");
                LogMessage("✅ Google Translate клиент инициализирован");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации переводчика: {ex.Message}");
            }
        }

        private void InitializeTimer()
        {
            audioLevelTimer = new System.Windows.Forms.Timer();
            audioLevelTimer.Interval = 250; // 🚀 ОПТИМИЗАЦИЯ: Увеличиваем интервал до 250мс (-60% вызовов)
            audioLevelTimer.Tick += AudioLevelTimer_Tick;
        }

        private void InitializeProcessingMode()
        {
            LogMessage("🔧 Инициализация режимов обработки...");
            
            cbProcessingMode.Items.Clear();
            cbProcessingMode.Items.Add("🔄 Оригинальный (ждет паузы)");
            cbProcessingMode.Items.Add("⚡ Потоковый (каждые 3 сек)");
            cbProcessingMode.Items.Add("🎤 Микрофон (как в MORT)");
            cbProcessingMode.SelectedIndex = 0; // Default to original mode
            currentProcessingMode = 0; // Инициализируем кэшированное значение
            
            cbProcessingMode.SelectedIndexChanged += ProcessingMode_Changed;
            
            LogMessage("✅ Режимы обработки настроены");
        }

        private void ProcessingMode_Changed(object sender, EventArgs e)
        {
            currentProcessingMode = cbProcessingMode.SelectedIndex; // Сохраняем для многопоточного доступа
            isStreamingMode = currentProcessingMode == 1;
            var selectedMode = currentProcessingMode switch
            {
                1 => "Потоковый",
                2 => "Микрофон (MORT)",
                _ => "Оригинальный"
            };
            LogMessage($"🔧 Режим обработки изменен на: {selectedMode}");
            
            if (currentProcessingMode == 1)
            {
                LogMessage("⚡ Включен потоковый режим - обработка каждые 3 секунды без ожидания пауз");
            }
            else if (currentProcessingMode == 2)
            {
                LogMessage("🎤 Включен режим микрофона - как в MORT с WaveInEvent");
            }
            else
            {
                LogMessage("🔄 Включен оригинальный режим - ожидание пауз в речи");
            }
            
            // Сохраняем настройку
            userSettings.ProcessingMode = currentProcessingMode;
            UserSettings.AutoSave(userSettings);
        }

        private async void InitializeStreamingComponents()
        {
            LogMessage("🔧 Инициализация стриминговых компонентов...");
            
            try
            {
                sessionStartTime = DateTime.Now;
                
                // Инициализируем стриминговый процессор Whisper
                streamingProcessor = new StreamingWhisperProcessor();
                streamingProcessor.OnTextRecognized += OnStreamingTextRecognized;
                streamingProcessor.OnError += OnStreamingError;
                streamingProcessor.OnStats += OnStreamingStats;
                
                // Инициализируем Whisper модель асинхронно
                bool whisperInitialized = await streamingProcessor.InitializeAsync(WhisperModelPath);
                if (!whisperInitialized)
                {
                    LogMessage("❌ Не удалось инициализировать стриминговый Whisper");
                    return;
                }
                
                LogMessage("✅ Стриминговые компоненты инициализированы");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации стриминговых компонентов: {ex.Message}");
            }
        }

        #endregion

        #region Streaming Event Handlers
        
        private void OnStreamingTextRecognized(string text, double confidence)
        {
            // 🛡️ Множественные проверки на остановку системы
            if (isDisposed || string.IsNullOrWhiteSpace(text) || !isCapturing || !isCollectingAudio)
            {
                LogMessage($"⚠️ Пропуск OnStreamingTextRecognized: isDisposed={isDisposed}, isCapturing={isCapturing}, text='{text?.Substring(0, Math.Min(20, text?.Length ?? 0))}...'");
                return;
            }
                
            try
            {
                LogMessage($"🎯 WHISPER РЕЗУЛЬТАТ (RAW): '{text}' [confidence: {confidence:P1}]");
                
                // 🧹 Очищаем эмоциональные маркеры для отображения
                var cleanText = AdvancedSpeechFilter.CleanEmotionalMarkers(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    LogMessage($"🚫 Пропущен текст с только эмоциональными маркерами: '{text}' -> '{cleanText}'");
                    return;
                }

                LogMessage($"✨ ОЧИЩЕННЫЙ ТЕКСТ: '{cleanText}'");

                BeginInvoke(() =>
                {
                    LogMessage($"🎤 Распознано ({confidence:P1}): {cleanText}");
                    
                    // Добавляем очищенный текст к исходному тексту
                    txtRecognizedText.Text += (txtRecognizedText.Text.Length > 0 ? " " : "") + cleanText;
                    txtRecognizedText.SelectionStart = txtRecognizedText.Text.Length;
                    txtRecognizedText.ScrollToCaret();
                    
                    // Переводим асинхронно (используем очищенный текст)
                    LogMessage($"🔄 Отправляем на перевод: '{cleanText}'");
                    _ = Task.Run(async () => {
                        try
                        {
                            await TranslateStreamingText(cleanText);
                            LogMessage($"✅ Перевод завершен для: '{cleanText}'");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"❌ Ошибка в TranslateStreamingText: {ex.Message}");
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки распознанного текста: {ex.Message}");
            }
        }
        
        private void OnStreamingError(Exception error)
        {
            if (isDisposed) return;
            
            BeginInvoke(() =>
            {
                LogMessage($"❌ Ошибка стриминга: {error.Message}");
            });
        }
        
        private void OnStreamingStats(StreamingStats stats)
        {
            if (isDisposed) return;
            
            BeginInvoke(() =>
            {
                // Обновляем статистику в UI
                lblStats.Text = $"Окон: {stats.ProcessedWindows} | " +
                               $"Очередь: {stats.QueueSize} | " +
                               $"Буфер: {stats.BufferFillLevel:P1} | " +
                               $"Время: {stats.AverageProcessingTime:F0}мс";
                
                totalProcessedFrames = stats.ProcessedWindows;
            });
        }
        
        private async Task TranslateStreamingText(string text)
        {
            try
            {
                LogMessage($"🔄 TranslateStreamingText НАЧАЛО для: '{text}'");
                
                // Текст уже очищен в OnStreamingTextRecognized
                if (string.IsNullOrWhiteSpace(text))
                {
                    LogMessage($"🚫 Пустой текст для перевода");
                    return;
                }

                string sourceLanguage = "";
                string targetLanguage = "";
                
                // Получаем значения языков в UI потоке
                if (InvokeRequired)
                {
                    Invoke(() =>
                    {
                        sourceLanguage = GetLanguageCode(cbSourceLang.SelectedItem?.ToString() ?? "Автоопределение");
                        targetLanguage = GetLanguageCode(cbTargetLang.SelectedItem?.ToString() ?? "Русский");
                    });
                }
                else
                {
                    sourceLanguage = GetLanguageCode(cbSourceLang.SelectedItem?.ToString() ?? "Автоопределение");
                    targetLanguage = GetLanguageCode(cbTargetLang.SelectedItem?.ToString() ?? "Русский");
                }
                
                LogMessage($"📝 Языки: {sourceLanguage} -> {targetLanguage}");
                
                if (sourceLanguage == targetLanguage)
                {
                    LogMessage($"⚠️ Исходный и целевой языки одинаковы ({sourceLanguage}), перевод не требуется");
                    BeginInvoke(() =>
                    {
                        txtTranslatedText.Text += (txtTranslatedText.Text.Length > 0 ? " " : "") + text;
                        txtTranslatedText.SelectionStart = txtTranslatedText.Text.Length;
                        txtTranslatedText.ScrollToCaret();
                    });
                    
                    // Озвучиваем если включено
                    if (chkAutoTranslate.Checked)
                    {
                        LogMessage($"🔊 Озвучиваем без перевода: '{text}'");
                        await SpeakText(text);
                    }
                    else
                    {
                        LogMessage("🔇 AutoTranslate отключен, озвучивания не будет");
                    }
                    return;
                }
                
                LogMessage($"🌐 Вызываем TranslateText для: '{text}'");
                var translatedText = await TranslateText(text, sourceLanguage, targetLanguage);
                
                if (!string.IsNullOrWhiteSpace(translatedText))
                {
                    LogMessage($"✅ ПЕРЕВОД ПОЛУЧЕН: '{text}' -> '{translatedText}'");
                    
                    BeginInvoke(() =>
                    {
                        txtTranslatedText.Text += (txtTranslatedText.Text.Length > 0 ? " " : "") + translatedText;
                        txtTranslatedText.SelectionStart = txtTranslatedText.Text.Length;
                        txtTranslatedText.ScrollToCaret();
                        
                        // TTS если включен
                        if (chkAutoTranslate.Checked) // Используем chkAutoTranslate вместо chkEnableTTS
                        {
                            LogMessage($"🔊 Озвучиваем перевод: '{translatedText}'");
                            _ = Task.Run(async () => {
                                try
                                {
                                    await SpeakText(translatedText);
                                    LogMessage($"✅ Озвучивание завершено для: '{translatedText}'");
                                }
                                catch (Exception ex)
                                {
                                    LogMessage($"❌ Ошибка озвучивания: {ex.Message}");
                                }
                            });
                        }
                        else
                        {
                            LogMessage("🔇 AutoTranslate отключен, озвучивания не будет");
                        }
                    });
                }
                else
                {
                    LogMessage($"❌ TranslateText вернул пустой результат для: '{text}'");
                }
                
                LogMessage($"🎯 TranslateStreamingText ЗАВЕРШЕНО для: '{text}'");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка перевода стримингового текста: {ex.Message}");
            }
        }

        #endregion

        #region User Settings

        private void LoadUserSettings()
        {
            try
            {
                userSettings = UserSettings.LoadSettings();
                userSettings.ApplySafeDefaults();
                
                LogMessage("📁 Настройки загружены");
                ApplySettingsToUI();
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка загрузки настроек: {ex.Message}");
                userSettings = new UserSettings();
            }
        }

        private void ApplySettingsToUI()
        {
            try
            {
                // Применяем настройки к форме
                if (userSettings.WindowLocationX >= 0 && userSettings.WindowLocationY >= 0)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    this.Location = new Point(userSettings.WindowLocationX, userSettings.WindowLocationY);
                }
                
                this.Size = new Size(userSettings.WindowWidth, userSettings.WindowHeight);
                
                // 🔧 Принудительная установка минимального размера для предотвращения "скукоживания"
                this.MinimumSize = new Size(800, 570);
                if (this.Width < 800 || this.Height < 570)
                {
                    this.Size = new Size(800, 570);
                    LogMessage("⚠️ Размер окна был скорректирован до минимального");
                }
                
                // Применяем пороговое значение
                numThreshold.Value = (decimal)userSettings.VoiceThreshold;
                voiceThreshold = userSettings.VoiceThreshold;
                
                LogMessage($"✅ Настройки применены к интерфейсу");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка применения настроек: {ex.Message}");
            }
        }

        private void ApplySettingsAfterInitialization()
        {
            try
            {
                // Выбираем аудиоустройство
                if (!string.IsNullOrEmpty(userSettings.SelectedAudioDevice))
                {
                    for (int i = 0; i < cbSpeakerDevices.Items.Count; i++)
                    {
                        if (cbSpeakerDevices.Items[i] is AudioDevice device && 
                            device.Name.Contains(userSettings.SelectedAudioDevice))
                        {
                            cbSpeakerDevices.SelectedIndex = i;
                            break;
                        }
                    }
                }

                // Выбираем языки
                if (cbSourceLang.Items.Contains(userSettings.SourceLanguage))
                    cbSourceLang.SelectedItem = userSettings.SourceLanguage;
                
                if (cbTargetLang.Items.Contains(userSettings.TargetLanguage))
                    cbTargetLang.SelectedItem = userSettings.TargetLanguage;

                // Выбираем режим обработки
                if (userSettings.ProcessingMode >= 0 && userSettings.ProcessingMode < cbProcessingMode.Items.Count)
                {
                    cbProcessingMode.SelectedIndex = userSettings.ProcessingMode;
                    currentProcessingMode = userSettings.ProcessingMode; // Обновляем кэш
                }

                // Включаем автоперевод
                chkAutoTranslate.Checked = userSettings.AutoTranslateAndTTS;

                LogMessage($"🔧 Настройки применены к элементам управления");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка применения настроек к элементам: {ex.Message}");
            }
        }

        private void SaveCurrentSettings()
        {
            try
            {
                // Сохраняем текущие настройки UI
                userSettings.WindowWidth = this.Width;
                userSettings.WindowHeight = this.Height;
                userSettings.WindowLocationX = this.Location.X;
                userSettings.WindowLocationY = this.Location.Y;

                if (cbSpeakerDevices.SelectedItem is AudioDevice selectedDevice)
                    userSettings.SelectedAudioDevice = selectedDevice.Name;

                userSettings.SourceLanguage = cbSourceLang.SelectedItem?.ToString() ?? "Автоопределение";
                userSettings.TargetLanguage = cbTargetLang.SelectedItem?.ToString() ?? "Русский";
                userSettings.VoiceThreshold = voiceThreshold;
                userSettings.ProcessingMode = currentProcessingMode; // Используем кэшированное значение
                userSettings.AutoTranslateAndTTS = chkAutoTranslate.Checked;

                UserSettings.SaveSettings(userSettings);
                LogMessage("💾 Настройки сохранены");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка сохранения настроек: {ex.Message}");
            }
        }

        private void OnSettingChanged()
        {
            // Автоматическое сохранение при изменении настроек
            UserSettings.AutoSave(userSettings, 2000); // Сохраняем через 2 секунды
        }

        private void SubscribeToSettingsEvents()
        {
            try
            {
                // Подписываемся на изменения ComboBox'ов
                cbSpeakerDevices.SelectedIndexChanged += (s, e) => {
                    if (cbSpeakerDevices.SelectedItem is AudioDevice selectedDevice)
                    {
                        userSettings.SelectedAudioDevice = selectedDevice.Name;
                        OnSettingChanged();
                    }
                };

                cbSourceLang.SelectedIndexChanged += (s, e) => {
                    userSettings.SourceLanguage = cbSourceLang.SelectedItem?.ToString() ?? "Автоопределение";
                    OnSettingChanged();
                };

                cbTargetLang.SelectedIndexChanged += (s, e) => {
                    userSettings.TargetLanguage = cbTargetLang.SelectedItem?.ToString() ?? "Русский";
                    OnSettingChanged();
                };

                // Подписываемся на изменения CheckBox'ов
                chkAutoTranslate.CheckedChanged += (s, e) => {
                    userSettings.AutoTranslateAndTTS = chkAutoTranslate.Checked;
                    OnSettingChanged();
                };

                // Подписываемся на изменения размера и положения окна
                this.LocationChanged += (s, e) => {
                    if (this.WindowState == FormWindowState.Normal)
                    {
                        userSettings.WindowLocationX = this.Location.X;
                        userSettings.WindowLocationY = this.Location.Y;
                        OnSettingChanged();
                    }
                };

                this.SizeChanged += (s, e) => {
                    if (this.WindowState == FormWindowState.Normal)
                    {
                        userSettings.WindowWidth = this.Width;
                        userSettings.WindowHeight = this.Height;
                        OnSettingChanged();
                    }
                };

                LogMessage("📡 Подписка на события настроек завершена");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка подписки на события настроек: {ex.Message}");
            }
        }

        #endregion

        #region Audio Capture

        private void btnStartCapture_Click(object sender, EventArgs e)
        {
            // 🚀 НОВАЯ СТАБИЛЬНАЯ АРХИТЕКТУРА
            StartStableAudioCapture();
        }

        private void btnStopCapture_Click(object sender, EventArgs e)
        {
            // Проверяем, нужен ли обычный стоп или полный сброс
            if (isCapturing)
            {
                // Немедленно отключаем кнопку чтобы предотвратить множественные нажатия
                btnStopCapture.Enabled = false;
                
                // Асинхронная остановка без блокировки UI
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 🚀 НОВАЯ СТАБИЛЬНАЯ АРХИТЕКТУРА
                        await StopStableAudioCapture();
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка остановки: {ex.Message}");
                    }
                    finally
                    {
                        // Восстанавливаем состояние UI в главном потоке
                        if (!IsDisposed && IsHandleCreated)
                        {
                            try
                            {
                                Invoke(() =>
                                {
                                    btnStopCapture.Enabled = true;
                                    btnStartCapture.Enabled = true;
                                });
                            }
                            catch (ObjectDisposedException)
                            {
                                // Форма закрыта - игнорируем
                            }
                        }
                    }
                });
            }
            else
            {
                // Если уже остановлено, делаем полный сброс системы
                ResetSystemToInitialState();
            }
        }

        /// <summary>
        /// 🚀 Запуск стабильной системы аудио-захвата
        /// </summary>
        private async void StartStableAudioCapture()
        {
            try
            {
                if (isCapturing)
                {
                    LogMessage("⚠️ Захват уже активен");
                    return;
                }

                LogMessage("🚀 Запуск новой стабильной архитектуры захвата...");

                // Отключение кнопок
                btnStartCapture.Enabled = false;
                btnStopCapture.Enabled = true;

                // Запуск стабильного захвата
                if (stableAudioCapture != null)
                {
                    await stableAudioCapture.StartCaptureAsync();
                }

                // Запуск таймера статистики
                if (statisticsTimer != null)
                {
                    statisticsTimer.Start();
                }

                // Запуск аудио таймера для UI
                if (audioLevelTimer != null)
                {
                    audioLevelTimer.Start();
                }

                isCapturing = true;
                LogMessage("✅ Стабильная система захвата запущена");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка запуска стабильного захвата: {ex.Message}");
                
                // Восстановление UI при ошибке
                btnStartCapture.Enabled = true;
                btnStopCapture.Enabled = false;
                isCapturing = false;
            }
        }

        /// <summary>
        /// 🚀 Остановка стабильной системы аудио-захвата
        /// </summary>
        private async Task StopStableAudioCapture()
        {
            try
            {
                LogMessage("⏹️ Остановка стабильной системы захвата...");

                // Остановка таймеров
                audioLevelTimer?.Stop();
                statisticsTimer?.Stop();

                // Остановка стабильного захвата
                if (stableAudioCapture != null)
                {
                    await stableAudioCapture.StopCaptureAsync();
                }

                // Финализация буферов
                if (slidingWindowAggregator != null)
                {
                    await slidingWindowAggregator.FlushAsync();
                }

                // Очистка очереди TTS если нужно
                if (stableTtsEngine != null)
                {
                    await stableTtsEngine.ClearQueueAsync();
                }

                isCapturing = false;
                
                this.BeginInvoke(() =>
                {
                    btnStartCapture.Enabled = true;
                    btnStopCapture.Enabled = false;
                    if (progressBarAudio != null)
                        progressBarAudio.Value = 0;
                });

                LogMessage("✅ Стабильная система захвата остановлена");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка остановки стабильного захвата: {ex.Message}");
            }
        }

        private void StartAudioCapture()
        {
            try
            {
                if (cbSpeakerDevices.SelectedItem is not AudioDevice selectedDevice)
                {
                    LogMessage("❌ Выберите аудиоустройство!");
                    return;
                }

                if (!File.Exists(WhisperModelPath))
                {
                    LogMessage($"❌ Whisper модель не найдена: {WhisperModelPath}");
                    return;
                }

                LogMessage("🎧 Запуск захвата аудио...");
                LogMessage($"🔄 Состояние перед запуском: isCapturing={isCapturing}, isCollectingAudio={isCollectingAudio}");
                
                // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: сброс SmartAudioManager для корректного перезапуска
                smartAudioManager?.ResetForNewStart();
                
                // Проверяем режим обработки
                int processingMode = currentProcessingMode; // Используем кэшированное значение
                
                if (processingMode == 2) // Микрофонный режим
                {
                    // Используем WaveInEvent для захвата с микрофона
                    waveInCapture = new WaveInEvent();
                    waveInCapture.WaveFormat = new WaveFormat(44100, 1); // 44.1kHz mono
                    waveInCapture.BufferMilliseconds = 100;
                    waveInCapture.DataAvailable += OnMicrophoneDataAvailable;
                    waveInCapture.RecordingStopped += OnMicrophoneRecordingStopped;
                    waveInCapture.StartRecording();
                    LogMessage("🎤 Режим микрофона: WaveInEvent активирован");
                }
                else
                {
                    // Используем WASAPI Loopback для системного аудио
                    wasapiCapture = new WasapiLoopbackCapture(selectedDevice.Device);
                    wasapiCapture.DataAvailable += OnAudioDataAvailable;
                    wasapiCapture.RecordingStopped += OnRecordingStopped;
                    wasapiCapture.StartRecording();
                    LogMessage("🔊 Режим системного аудио: WASAPI активирован");
                }
                
                audioLevelTimer?.Start();
                
                // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: сброс всех состояний для корректного перезапуска
                isCapturing = true;
                isCollectingAudio = false; // ОБЯЗАТЕЛЬНО сбрасываем для нового цикла записи
                audioBuffer.Clear();
                audioLogCount = 0; // Сброс счетчика логов для отладки;
                
                LogMessage($"✅ Состояние после установки: isCapturing={isCapturing}, isCollectingAudio={isCollectingAudio}");
                LogMessage($"📊 Буфер очищен, размер: {audioBuffer.Count}");
                
                // Update UI
                btnStartCapture.Enabled = false;
                btnStopCapture.Enabled = true;
                lblStatus.Text = "🎧 Захват активен";
                lblStatus.ForeColor = Color.Green;
                txtRecognizedText.Text = "🔇 Ожидание речи...";
                txtTranslatedText.Text = "🔇 Ожидание перевода...";
                
                LogMessage($"✅ Захват запущен: {selectedDevice.Name}");
                LogMessage($"🎚️ Порог активации: {voiceThreshold:F3}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка запуска захвата: {ex.Message}");
            }
        }

        private async Task StopAudioCapture()
        {
            try
            {
                LogMessage("⏹️ ПОЛНАЯ ОСТАНОВКА СИСТЕМЫ...");
                
                // 🛡️ Защита от повторного вызова
                if (!isCapturing && !isCollectingAudio)
                {
                    LogMessage("⚠️ Система уже остановлена");
                    return;
                }
                
                // 1. Останавливаем захват аудио
                isCapturing = false;
                isCollectingAudio = false;
                isTTSActive = false; // Принудительно сбрасываем TTS флаг
                audioLevelTimer?.Stop();
                
                // 2. Очищаем все буферы
                audioBuffer.Clear();
                LogMessage("🗑️ Аудио буфер очищен");
                
                // 3. Полная остановка и очистка SmartAudioManager
                if (smartAudioManager != null)
                {
                    smartAudioManager.ClearQueue();
                    smartAudioManager.PauseCapture("full_stop");
                    LogMessage("🗑️ SmartAudioManager: очередь очищена, захват приостановлен");
                }
                
                // 4. Принудительная остановка всех TTS операций
                if (speechSynthesizer != null)
                {
                    try
                    {
                        speechSynthesizer.SpeakAsyncCancelAll();
                        
                        // Ждем небольшое время для завершения отмены
                        await Task.Delay(200);
                        
                        LogMessage("🛑 Все TTS операции отменены");
                    }
                    catch (OperationCanceledException)
                    {
                        LogMessage("🛑 TTS операции уже отменены");
                    }
                    catch (Exception ttsEx)
                    {
                        LogMessage($"❌ Ошибка остановки TTS: {ttsEx.Message}");
                    }
                }
                
                // 5. Остановка и очистка потоковых процессоров
                try
                {
                    if (streamingProcessor != null)
                    {
                        // 🔌 Отписываемся от событий ПЕРЕД остановкой процессора
                        try
                        {
                            streamingProcessor.OnTextRecognized -= OnStreamingTextRecognized;
                            streamingProcessor.OnError -= OnStreamingError;
                            streamingProcessor.OnStats -= OnStreamingStats;
                            LogMessage("🔌 События StreamingProcessor отключены");
                        }
                        catch (Exception eventEx)
                        {
                            LogMessage($"⚠️ Ошибка отписки от событий: {eventEx.Message}");
                        }
                        
                        // Агрессивная остановка с таймаутом
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        {
                            await streamingProcessor.DisposeAsync().AsTask().WaitAsync(timeoutCts.Token);
                        }
                        streamingProcessor = null;
                        LogMessage("🔇 StreamingWhisperProcessor остановлен");
                    }
                }
                catch (OperationCanceledException)
                {
                    LogMessage("⚠️ StreamingProcessor принудительно остановлен по таймауту");
                    streamingProcessor = null; // Принудительно обнуляем
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Ошибка остановки StreamingProcessor: {ex.Message}");
                    streamingProcessor = null; // Принудительно обнуляем для предотвращения утечек
                }
                
                try
                {
                    if (audioResampler != null)
                    {
                        audioResampler.Dispose();
                        audioResampler = null;
                        LogMessage("🔇 AudioResampler остановлен");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Ошибка остановки AudioResampler: {ex.Message}");
                }
                
                // 6. Остановка аудиоустройств с отпиской от событий
                try
                {
                    if (wasapiCapture != null)
                    {
                        // Отписываемся от событий перед остановкой
                        try
                        {
                            wasapiCapture.DataAvailable -= OnAudioDataAvailable;
                            LogMessage("🔌 WASAPI события отключены");
                        }
                        catch (Exception eventEx)
                        {
                            LogMessage($"⚠️ Ошибка отписки WASAPI событий: {eventEx.Message}");
                        }
                        
                        wasapiCapture.StopRecording();
                        wasapiCapture.Dispose();
                        wasapiCapture = null;
                        LogMessage("🔇 WASAPI захват остановлен");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Ошибка остановки WASAPI: {ex.Message}");
                    wasapiCapture = null; // Принудительно обнуляем
                }
                
                try
                {
                    if (waveInCapture != null)
                    {
                        // Отписываемся от событий перед остановкой
                        try
                        {
                            waveInCapture.DataAvailable -= OnAudioDataAvailable;
                            LogMessage("🔌 WaveIn события отключены");
                        }
                        catch (Exception eventEx)
                        {
                            LogMessage($"⚠️ Ошибка отписки WaveIn событий: {eventEx.Message}");
                        }
                        
                        waveInCapture.StopRecording();
                        waveInCapture.Dispose();
                        waveInCapture = null;
                        LogMessage("🎤 Микрофон остановлен");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Ошибка остановки микрофона: {ex.Message}");
                    waveInCapture = null; // Принудительно обнуляем
                }
                
                // 7. Принудительная сборка мусора для освобождения ресурсов
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                // 8. Обновление UI
                Invoke(() => {
                    btnStartCapture.Enabled = true;
                    btnStopCapture.Enabled = false;
                    lblStatus.Text = "🔇 Полностью остановлен";
                    lblStatus.ForeColor = Color.Red;
                    progressAudioLevel.Value = 0;
                    lblAudioLevel.Text = "📊 Уровень: 0%";
                    txtRecognizedText.Text = "⏹️ Система остановлена";
                    txtTranslatedText.Text = "⏹️ Очереди очищены";
                });
                
                LogMessage("✅ СИСТЕМА ПОЛНОСТЬЮ ОСТАНОВЛЕНА И ОЧИЩЕНА");
                LogMessage("🔄 Готов к новому запуску");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ КРИТИЧЕСКАЯ ОШИБКА при остановке: {ex.Message}");
                
                // Экстренная очистка в случае ошибки
                try
                {
                    isCapturing = false;
                    isCollectingAudio = false;
                    audioBuffer.Clear();
                    wasapiCapture?.Dispose();
                    waveInCapture?.Dispose();
                    wasapiCapture = null;
                    waveInCapture = null;
                    
                    Invoke(() => {
                        btnStartCapture.Enabled = true;
                        btnStopCapture.Enabled = false;
                        lblStatus.Text = "❌ Ошибка остановки";
                        lblStatus.ForeColor = Color.Red;
                    });
                }
                catch
                {
                    // Если даже экстренная очистка не работает, просто логируем
                    LogMessage("💀 Критическая ошибка: невозможно очистить ресурсы");
                }
            }
        }

        /// <summary>
        /// Экстренная остановка всех процессов (для критических ситуаций)
        /// </summary>
        private void EmergencyStop()
        {
            try
            {
                LogMessage("🚨 ЭКСТРЕННАЯ ОСТАНОВКА ВСЕХ ПРОЦЕССОВ!");
                
                // Останавливаем все флаги
                isCapturing = false;
                isCollectingAudio = false;
                isStreamingMode = false;
                
                // Очищаем все буферы
                audioBuffer.Clear();
                
                // Экстренная остановка SmartAudioManager
                try 
                { 
                    smartAudioManager?.EmergencyStop(); 
                    LogMessage("✅ SmartAudioManager экстренно остановлен");
                } 
                catch (Exception ex) 
                { 
                    LogMessage($"⚠️ Ошибка остановки SmartAudioManager: {ex.Message}"); 
                }
                
                // Принудительная остановка всех аудио устройств
                try { wasapiCapture?.StopRecording(); } catch { }
                try { wasapiCapture?.Dispose(); } catch { }
                try { waveInCapture?.StopRecording(); } catch { }
                try { waveInCapture?.Dispose(); } catch { }
                wasapiCapture = null;
                waveInCapture = null;
                
                // Остановка всех TTS
                try { speechSynthesizer?.SpeakAsyncCancelAll(); } catch { }
                
                // Остановка таймеров
                try { audioLevelTimer?.Stop(); } catch { }
                
                // Принудительная сборка мусора
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                LogMessage("✅ Экстренная остановка завершена");
            }
            catch (Exception ex)
            {
                LogMessage($"💀 Критическая ошибка экстренной остановки: {ex.Message}");
            }
        }

        /// <summary>
        /// Полный сброс системы к начальному состоянию
        /// </summary>
        private void ResetSystemToInitialState()
        {
            try
            {
                LogMessage("🔄 СБРОС СИСТЕМЫ К НАЧАЛЬНОМУ СОСТОЯНИЮ...");
                
                // Экстренная остановка
                EmergencyStop();
                
                // Сброс переменных состояния
                currentAudioLevel = 0f;
                lastVoiceActivity = DateTime.Now;
                recordingStartTime = DateTime.Now;
                
                // Обновление UI к начальному состоянию
                Invoke(() => {
                    btnStartCapture.Enabled = true;
                    btnStopCapture.Enabled = false;
                    lblStatus.Text = "🔄 Система сброшена";
                    lblStatus.ForeColor = Color.Green;
                    progressAudioLevel.Value = 0;
                    lblAudioLevel.Text = "📊 Уровень: 0%";
                    txtRecognizedText.Text = "🔄 Готов к новому запуску";
                    txtTranslatedText.Text = "🔄 Система сброшена";
                    progressBar.Visible = false;
                });
                
                LogMessage("✅ Система успешно сброшена и готова к работе");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка сброса системы: {ex.Message}");
            }
        }

        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            // Проверяем, что форма не была освобождена
            if (IsDisposed || !IsHandleCreated || isDisposed)
            {
                return; // Форма закрыта, прекращаем обработку аудио
            }
            
            // 🚀 THROTTLING АУДИООБРАБОТКИ: Ограничиваем частоту обработки
            DateTime now = DateTime.Now;
            if ((now - lastAudioProcessTime).TotalMilliseconds < AUDIO_THROTTLE_MS)
            {
                return; // Пропускаем слишком частые вызовы
            }
            lastAudioProcessTime = now;
            
            if (!isCapturing) 
            {
                LogMessageDebug("⚠️ OnAudioDataAvailable: isCapturing=false, игнорируем данные");
                return;
            }

            // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Отправляем в Bounded Channels пайплайн
            try
            {
                // Calculate audio level для UI
                float level = CalculateAudioLevel(e.Buffer, e.BytesRecorded);
                currentAudioLevel = level;

                // Проверяем голосовую активность
                if (level > voiceThreshold)
                {
                    lastVoiceActivity = DateTime.Now;
                    
                    if (!isCollectingAudio)
                    {
                        isCollectingAudio = true;
                        audioBuffer.Clear();
                        LogMessageDebug($"🎤 Начало записи аудио, уровень: {level:F3}");
                    }
                    
                    // Копируем буфер для отправки в канал
                    byte[] audioChunk = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, audioChunk, e.BytesRecorded);
                    audioBuffer.AddRange(audioChunk);
                    
                    // 🚀 ОТПРАВЛЯЕМ В КАНАЛ ВМЕСТО ПРЯМОЙ ОБРАБОТКИ
                    if (_captureChannel.Writer.TryWrite(audioChunk))
                    {
                        // Получаем приблизительную статистику канала
                        int queueEstimate = _captureChannel.Reader.Count;
                        LogMessageDebug($"📊 Аудио отправлено в канал: {audioChunk.Length} байт, очередь ≈{queueEstimate}");
                    }
                    else
                    {
                        LogMessage("⚠️ 🔴 ДРОП: Канал захвата переполнен! Аудиоданные сброшены из-за backpressure");
                        // Статистика дропов для мониторинга
                        if (DateTime.Now.Subtract(_lastDropLogTime).TotalSeconds > 5)
                        {
                            LogMessage($"📈 СТАТИСТИКА: Дропы в аудиоканале за последние 5 сек");
                            _lastDropLogTime = DateTime.Now;
                        }
                    }
                }
                else if (isCollectingAudio)
                {
                    // Проверяем паузу для завершения записи
                    if ((DateTime.Now - lastVoiceActivity).TotalSeconds > 2.0)
                    {
                        LogMessageDebug($"🔇 Пауза обнаружена, завершение записи. Буфер: {audioBuffer.Count} байт");
                        
                        // Отправляем накопленный буфер если он достаточно большой
                        if (audioBuffer.Count > 16000) // Минимум для обработки
                        {
                            byte[] finalBuffer = audioBuffer.ToArray();
                            if (_captureChannel.Writer.TryWrite(finalBuffer))
                            {
                                LogMessage($"📝 Финальный буфер отправлен: {finalBuffer.Length} байт");
                            }
                        }
                        
                        isCollectingAudio = false;
                        audioBuffer.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка в канальном обработчике аудио: {ex.Message}");
            }
        }

        #endregion

        #region Microphone Audio Processing
        
        private void ProcessStreamingAudio(byte[] buffer, int bytesRecorded, float level)
        {
            try
            {
                // Инициализируем ресэмплер если нужно
                if (audioResampler == null)
                {
                    var currentWaveFormat = wasapiCapture?.WaveFormat ?? new WaveFormat(44100, 16, 2);
                    audioResampler = new AudioResampler(currentWaveFormat.SampleRate, currentWaveFormat.Channels);
                    LogMessage($"🔧 Ресэмплер инициализирован: {currentWaveFormat.SampleRate}Hz, {currentWaveFormat.Channels}ch");
                }

                // Конвертируем byte array в float array и ресэмплируем
                var processingWaveFormat = wasapiCapture?.WaveFormat ?? new WaveFormat(44100, 16, 2);
                var resampledAudio = audioResampler.ResampleFromBytes(buffer.Take(bytesRecorded).ToArray(), processingWaveFormat);
                
                if (resampledAudio.Length > 0)
                {
                    // Отправляем в стриминговый процессор
                    streamingProcessor?.AddAudioSamples(resampledAudio);
                    
                    // Обновляем UI
                    totalProcessedFrames++;
                    if (totalProcessedFrames % 10 == 0) // Каждые 10 фреймов
                    {
                        Invoke(() => {
                            txtRecognizedText.Text = $"🌊 Стриминг активен (уровень: {level:F3})";
                            progressBar.Visible = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка стримингового аудио: {ex.Message}");
            }
        }
        
        private void ProcessOriginalModeAudio(byte[] buffer, int bytesRecorded, float level)
        {
            // Voice activity detection
            bool isVoiceDetected = level > voiceThreshold;

            if (isVoiceDetected)
            {
                if (!isCollectingAudio)
                {
                    isCollectingAudio = true;
                    audioBuffer.Clear();
                    recordingStartTime = DateTime.Now; // Запомним время начала записи
                    LogMessage($"🎤 Начат захват речи (уровень: {level:F3})");
                    
                    Invoke(() => {
                        txtRecognizedText.Text = "🎤 Записываю речь...";
                        progressBar.Visible = true;
                    });
                }

                // Check for max recording time FIRST (даже при активном звуке)
                var recordingDuration = DateTime.Now - recordingStartTime;
                if (recordingDuration.TotalMilliseconds > maxRecordingMs)
                {
                    isCollectingAudio = false;
                    LogMessage($"⏰ Принудительная остановка записи (максимум {maxRecordingMs}мс достигнут)");
                    
                    if (audioBuffer.Count > 16000)
                    {
                        LogMessage($"⏹️ Принудительная обработка (данных: {audioBuffer.Count} байт)");
                        
                        Invoke(() => {
                            txtRecognizedText.Text = "🔄 Обрабатываю аудио...";
                        });
                        
                        byte[] recordedAudio = audioBuffer.ToArray();
                        _ = Task.Run(() => ProcessAudioSequentially(recordedAudio));
                    }
                    
                    Invoke(() => {
                        progressBar.Visible = false;
                    });
                    return;
                }

                // Add audio data to buffer
                byte[] audioData = new byte[bytesRecorded];
                Array.Copy(buffer, audioData, bytesRecorded);
                audioBuffer.AddRange(audioData);
                
                lastVoiceActivity = DateTime.Now;
            }
            else if (isCollectingAudio)
            {
                // Check for max recording time
                var recordingDuration = DateTime.Now - recordingStartTime;
                if (recordingDuration.TotalMilliseconds > maxRecordingMs)
                {
                    isCollectingAudio = false;
                    LogMessage($"⏰ Принудительная остановка записи (максимум {maxRecordingMs}мс достигнут)");
                    
                    if (audioBuffer.Count > 16000)
                    {
                        LogMessage($"⏹️ Принудительная обработка (данных: {audioBuffer.Count} байт)");
                        
                        Invoke(() => {
                            txtRecognizedText.Text = "🔄 Обрабатываю аудио...";
                        });
                        
                        byte[] timeoutAudio = audioBuffer.ToArray();
                        _ = Task.Run(() => ProcessAudioSequentially(timeoutAudio));
                    }
                    
                    Invoke(() => {
                        progressBar.Visible = false;
                    });
                    return;
                }
                
                // Check for silence timeout
                var silenceDuration = DateTime.Now - lastVoiceActivity;
                if (silenceDuration.TotalMilliseconds > silenceDurationMs)
                {
                    isCollectingAudio = false;
                    
                    if (audioBuffer.Count > 16000) // Minimum audio data
                    {
                        LogMessage($"⏹️ Конец речи (тишина: {silenceDuration.TotalMilliseconds:F0}мс, данных: {audioBuffer.Count} байт)");
                        
                        Invoke(() => {
                            txtRecognizedText.Text = "🔄 Обрабатываю аудио...";
                        });
                        
                        // Process collected audio in background
                        var audioDataCopy = audioBuffer.ToArray();
                        Task.Run(() => ProcessAudioSequentially(audioDataCopy));
                    }
                    
                    audioBuffer.Clear();
                }
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                LogMessage($"❌ Запись остановлена с ошибкой: {e.Exception.Message}");
            }
        }

        // Обработчики для микрофонного ввода
        private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!isCapturing) return;

            // ЛОГИКА ДЛЯ МИКРОФОНА: игнорируем нашу собственную речь во время TTS
            // (Микрофон = наша речь, не имеет смысла накапливать во время нашего же TTS)
            if (isTTSActive || (speechSynthesizer?.State == System.Speech.Synthesis.SynthesizerState.Speaking))
            {
                return; // Простое игнорирование во время TTS
            }

            try
            {
                // Calculate audio level for 16-bit samples from microphone
                float level = CalculateMicrophoneLevel(e.Buffer, e.BytesRecorded);
                currentAudioLevel = level;

                // Voice activity detection
                bool isVoiceDetected = level > voiceThreshold;

                if (isVoiceDetected)
                {
                    if (!isCollectingAudio)
                    {
                        isCollectingAudio = true;
                        audioBuffer.Clear();
                        recordingStartTime = DateTime.Now;
                        LogMessage($"🎤 Начат захват речи с микрофона (уровень: {level:F3})");
                        
                        Invoke(() => {
                            txtRecognizedText.Text = "🎤 Записываю речь с микрофона...";
                            progressBar.Visible = true;
                        });
                    }
                    
                    lastVoiceActivity = DateTime.Now;
                }

                if (isCollectingAudio)
                {
                    // Convert 16-bit to 32-bit float for consistency
                    byte[] convertedBuffer = ConvertMicrophoneData(e.Buffer, e.BytesRecorded);
                    audioBuffer.AddRange(convertedBuffer);
                    
                    // Check for max recording time
                    var recordingDuration = DateTime.Now - recordingStartTime;
                    if (recordingDuration.TotalMilliseconds > maxRecordingMs)
                    {
                        isCollectingAudio = false;
                        LogMessage($"⏰ Принудительная остановка записи микрофона (максимум {maxRecordingMs}мс достигнут)");
                        
                        if (audioBuffer.Count > 16000)
                        {
                            LogMessage($"⏹️ Принудительная обработка микрофона (данных: {audioBuffer.Count} байт)");
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "🔄 Обрабатываю аудио с микрофона...";
                            });
                            
                            byte[] timeoutAudio = audioBuffer.ToArray();
                            _ = Task.Run(() => ProcessAudioSequentially(timeoutAudio));
                        }
                        
                        Invoke(() => {
                            progressBar.Visible = false;
                        });
                        return;
                    }
                    
                    // Check for silence timeout
                    var silenceDuration = DateTime.Now - lastVoiceActivity;
                    if (silenceDuration.TotalMilliseconds > silenceDurationMs)
                    {
                        isCollectingAudio = false;
                        
                        if (audioBuffer.Count > 16000)
                        {
                            LogMessage($"⏹️ Конец речи с микрофона (тишина: {silenceDuration.TotalMilliseconds:F0}мс, данных: {audioBuffer.Count} байт)");
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "🔄 Обрабатываю аудио с микрофона...";
                            });
                            
                            byte[] silenceAudio = audioBuffer.ToArray();
                            _ = Task.Run(() => ProcessAudioSequentially(silenceAudio));
                        }
                        
                        Invoke(() => {
                            progressBar.Visible = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки микрофонных данных: {ex.Message}");
            }
        }

        private void OnMicrophoneRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                LogMessage($"❌ Запись микрофона остановлена с ошибкой: {e.Exception.Message}");
            }
        }

        private float CalculateMicrophoneLevel(byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded == 0) return 0f;

            float sum = 0f;
            int sampleCount = 0;

            // Process 16-bit samples from microphone
            for (int i = 0; i < bytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float normalizedSample = sample / 32768.0f; // Normalize to [-1, 1]
                sum += Math.Abs(normalizedSample);
                sampleCount++;
            }

            float avgLevel = sampleCount > 0 ? sum / sampleCount : 0f;
            
            // Amplify the level for better visualization
            return avgLevel * 5f;
        }

        private byte[] ConvertMicrophoneData(byte[] buffer, int bytesRecorded)
        {
            // Convert 16-bit samples to 32-bit float
            List<byte> converted = new List<byte>();
            
            for (int i = 0; i < bytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float floatSample = sample / 32768.0f; // Normalize to [-1, 1]
                byte[] floatBytes = BitConverter.GetBytes(floatSample);
                converted.AddRange(floatBytes);
            }
            
            return converted.ToArray();
        }

        private float CalculateAudioLevel(byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded == 0) return 0f;

            float sum = 0f;
            int sampleCount = 0;

            // 🚀 ОПТИМИЗАЦИЯ СЕМПЛОВ: Анализируем каждый 4-й семпл для снижения CPU нагрузки (-75%)
            const int SKIP_SAMPLES = 4; // Анализируем каждый 4-й семпл
            
            for (int i = 0; i < bytesRecorded - 3; i += 4 * SKIP_SAMPLES)
            {
                if (i + 3 < bytesRecorded)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    sum += Math.Abs(sample);
                    sampleCount++;
                }
            }

            float avgLevel = sampleCount > 0 ? sum / sampleCount : 0f;
            
            // Amplify the level for better visualization (multiply by 10)
            return avgLevel * 10f;
        }

        private void AudioLevelTimer_Tick(object? sender, EventArgs e)
        {
            if (isCapturing)
            {
                int percentage = (int)(currentAudioLevel * 100);
                percentage = Math.Min(100, percentage);
                
                // 🚀 УМНОЕ UI ОБНОВЛЕНИЕ: Обновляем только при изменениях или по таймауту
                DateTime now = DateTime.Now;
                bool shouldUpdate = (percentage != lastAudioPercentage) || 
                                   (now - lastUIUpdate).TotalMilliseconds > UI_UPDATE_INTERVAL_MS;

                if (shouldUpdate)
                {
                    // Обновляем UI только при реальных изменениях или по таймауту
                    progressAudioLevel.Value = percentage;
                    lblAudioLevel.Text = $"📊 Уровень: {percentage}%";
                    lblAudioLevel.ForeColor = percentage > (voiceThreshold * 100) ? Color.Green : Color.Gray;
                    
                    lastAudioPercentage = percentage;
                    lastUIUpdate = now;
                }
            }
        }

        #endregion

        #region STT Processing

        /// <summary>
        /// Последовательная обработка аудио сегментов для гарантированного хронологического порядка
        /// </summary>
        private async Task ProcessAudioSequentially(byte[] audioData)
        {
            int sequenceNum = Interlocked.Increment(ref audioSequenceNumber);
            await audioProcessingSemaphore.WaitAsync(); // Ждем очереди
            try
            {
                LogMessage($"🔢 Обработка аудио сегмента #{sequenceNum} в хронологическом порядке");
                await ProcessAudioDataInternal(audioData, sequenceNum);
            }
            finally
            {
                audioProcessingSemaphore.Release(); // Освобождаем для следующего
            }
        }

        private async Task ProcessAudioDataInternal(byte[] audioData, int sequenceNumber = 0)
        {
            try
            {
                // 🔧 ВРЕМЕННОЕ ИСПРАВЛЕНИЕ: Уменьшаем минимальный размер для тестирования
                const int MIN_AUDIO_SIZE = 16000; // 16KB минимум для тестирования (было 64KB)
                
                if (audioData.Length < MIN_AUDIO_SIZE)
                {
                    LogMessage($"⚠️ Аудио сегмент слишком мал для обработки: {audioData.Length} байт < {MIN_AUDIO_SIZE} байт");
                    
                    Invoke(() => {
                        txtRecognizedText.Text = "⚠️ Недостаточно аудиоданных для распознавания";
                        progressBar.Visible = false;
                    });
                    
                    return; // Прекращаем обработку
                }
                
                LogMessage($"🎯 Сегмент #{sequenceNumber} - Начало STT обработки ({audioData.Length} байт)");
                
                // Анализируем качество аудио данных
                AnalyzeAudioQuality(audioData, sequenceNumber);
                
                // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Нормализация аудио с MediaFoundationResampler
                // Определяем входной формат (предполагаем WASAPI loopback 44100Hz stereo float32)
                var inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                var wavData = ConvertToWavNormalized(audioData, inputFormat);
                
                if (wavData.Length == 0)
                {
                    LogMessage($"❌ Сегмент #{sequenceNumber} - Нормализация аудио неудачна");
                    
                    Invoke(() => {
                        txtRecognizedText.Text = "❌ Ошибка нормализации аудио";
                        progressBar.Visible = false;
                    });
                    
                    return;
                }
                
                LogMessage($"🔄 Сегмент #{sequenceNumber} - Нормализовано до WAV: {wavData.Length} байт");

                // Perform STT with Whisper.NET
                string recognizedText = await PerformWhisperSTT(wavData);
                
                if (!string.IsNullOrEmpty(recognizedText) && IsValidSpeech(recognizedText))
                {
                    LogMessage($"✅ Сегмент #{sequenceNumber} - Распознан текст: '{recognizedText}'");
                    
                    Invoke(() => {
                        txtRecognizedText.Text = recognizedText;
                        progressBar.Visible = false;
                    });

                    // Auto-translate if enabled
                    if (chkAutoTranslate.Checked)
                    {
                        await TranslateAndSpeak(recognizedText);
                    }
                }
                else
                {
                    DebugLogSpeechValidation("⚠️ Текст не распознан или отфильтрован как заглушка");
                    Invoke(() => {
                        txtRecognizedText.Text = "❌ Текст не распознан";
                        progressBar.Visible = false;
                    });
                }
                
                // Reset capture state for continuous listening
                isCollectingAudio = false;
                audioBuffer.Clear();
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка STT обработки: {ex.Message}");
                Invoke(() => {
                    txtRecognizedText.Text = $"❌ Ошибка: {ex.Message}";
                    progressBar.Visible = false;
                });
                
                // Reset capture state even on error
                isCollectingAudio = false;
                audioBuffer.Clear();
            }
        }

        /// <summary>
        /// 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Инициализация "теплого" Whisper instance
        /// Убирает overhead создания WhisperFactory/CreateBuilder на каждый сегмент
        /// </summary>
        private void EnsureWhisperReady()
        {
            if (_whisperProcessor != null) return;
            
            lock (_whisperLock)
            {
                if (_whisperProcessor != null) return;
                
                try
                {
                    LogMessage("🚀 Инициализация теплого Whisper instance...");
                    
                    _whisperFactory = WhisperFactory.FromPath(WhisperModelPath);
                    _whisperProcessor = _whisperFactory.CreateBuilder()
                        .WithLanguage("ru") // Фиксированный русский язык для стабильности
                        .WithPrompt("Это человеческая речь на русском языке") // Русская подсказка
                        .WithProbabilities() // Включаем вероятности для фильтрации
                        .WithTemperature(0.1f) // Немного увеличим для лучшего распознавания
                        .Build();
                    
                    LogMessage("✅ Теплый Whisper instance готов к использованию!");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Ошибка инициализации Whisper: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 🚀 КРИТИЧЕСКАЯ ОЧИСТКА: Безопасная очистка теплого Whisper instance
        /// </summary>
        private static void CleanupWhisperResources()
        {
            lock (_whisperLock)
            {
                try
                {
                    if (_whisperProcessor != null)
                    {
                        (_whisperProcessor as IDisposable)?.Dispose();
                        _whisperProcessor = null;
                        Debug.WriteLine("✅ Whisper processor очищен");
                    }
                    
                    if (_whisperFactory != null)
                    {
                        _whisperFactory.Dispose();
                        _whisperFactory = null;
                        Debug.WriteLine("✅ Whisper factory очищен");
                    }
                    
                    // 🚀 ОЧИСТКА MediaFoundation
                    try
                    {
                        MediaFoundationApi.Shutdown();
                        Debug.WriteLine("✅ MediaFoundation очищен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка очистки MediaFoundation: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Ошибка очистки Whisper: {ex.Message}");
                }
            }
        }

        private async Task<string> PerformWhisperSTT(byte[] wavData)
        {
            try
            {
                // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Используем теплый Whisper instance
                EnsureWhisperReady();
                
                LogMessage("🔄 Обработка аудио через теплый Whisper...");
                
                // Create temporary WAV file
                string tempFile = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tempFile, wavData);
                
                try
                {
                    // 🚀 ИСПОЛЬЗУЕМ ТЕПЛЫЙ INSTANCE ВМЕСТО СОЗДАНИЯ НОВОГО
                    using var fileStream = File.OpenRead(tempFile);
                    var result = new StringBuilder();
                    
                    await foreach (var segment in _whisperProcessor!.ProcessAsync(fileStream))
                    {
                        LogMessage($"🎯 Whisper сегмент: '{segment.Text}'");
                        
                        if (!string.IsNullOrWhiteSpace(segment.Text))
                        {
                            string cleanText = segment.Text.Trim();
                            
                            // Проверяем на заглушки перед добавлением
                            if (!IsPlaceholderToken(cleanText))
                            {
                                result.Append(cleanText + " ");
                            }
                            else
                            {
                                DebugLogSpeechValidation($"🚫 Пропущен сегмент-заглушка: '{cleanText}'");
                            }
                        }
                    }
                    
                    string finalResult = result.ToString().Trim();
                    
                    // Финальная проверка результата
                    if (string.IsNullOrWhiteSpace(finalResult))
                    {
                        LogMessage("⚠️ Whisper вернул пустой результат");
                        return string.Empty;
                    }
                    
                    // Проверяем на мусор в финальном результате
                    if (IsPlaceholderToken(finalResult))
                    {
                        LogMessage($"🚫 Финальный результат отфильтрован как мусор: '{finalResult}'");
                        return string.Empty;
                    }
                    
                    LogMessage($"✅ Whisper результат принят: '{finalResult}'");
                    return finalResult;
                }
                finally
                {
                    // Clean up temp file
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка Whisper.NET: {ex.Message}");
                return string.Empty;
            }
        }

        private bool IsValidSpeech(string text)
        {
            // 🚀 НОВЫЙ ПРОДВИНУТЫЙ ФИЛЬТР из MORT с европейской поддержкой и debug логированием
            DebugLogSpeechValidation($"🔍 Проверка валидности речи: '{text}'");
            
            // Используем европейский фильтр для более точной проверки
            bool isEuropeanValid = EuropeanLanguageFilter.IsValidEuropeanSpeech(text);
            bool hasExtremeDuplication = AdvancedSpeechFilter.HasExtremeDuplication(text);
            
            DebugLogSpeechValidation($"📊 Фильтр: EuropeanValid={isEuropeanValid}, ExtremeDuplication={hasExtremeDuplication}");
            
            bool finalResult = isEuropeanValid && !hasExtremeDuplication;
            DebugLogSpeechValidation($"✅ Итоговый результат валидации: {finalResult}");
            
            return finalResult;
        }

        private bool IsPlaceholderToken(string text)
        {
            // Быстрая проверка на пустоту
            if (string.IsNullOrWhiteSpace(text))
                return true;
                
            text = text.Trim();
            
            // 🚀 УЛУЧШЕННЫЙ ФИЛЬТР: Менее агрессивная фильтрация
            
            // Проверка на специальные маркеры (четкие заглушки)
            string[] definiteTokens = {
                "[Music]", "[Музыка]", "[музыка]", 
                "[BLANK_AUDIO]", "[Sound]", "[Звук]",
                "[Bell rings]", "[звук колокола]",
                "[Sounds of a camera]", "[звук камеры]",
                "[BIRDS CHIRPING]", "[пение птиц]",
                "This is human speech", "Это человеческая речь",
                "(snoring)", "(храп)", "(음악)", "♪"
            };
            
            foreach (var token in definiteTokens)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLogSpeechValidation($"🚫 Обнаружен специальный токен: '{token}' в '{text}'");
                    return true;
                }
            }
            
            // 🔥 УЛУЧШЕННАЯ МЕТРИКА: главная - доля букв (по рекомендации анализа)
            int totalChars = text.Length;
            int letterCount = text.Count(char.IsLetter);
            float letterShare = (float)letterCount / totalChars;
            
            // Первичная проверка: минимум букв и их доля
            if (letterCount < 3 || letterShare < 0.5f)
            {
                DebugLogSpeechValidation($"🚫 Недостаточно букв: {letterCount} букв, доля {letterShare:P} в '{text}'");
                return true;
            }
            
            // Вторичная проверка: мусорные символы (повышен порог как вспомогательный)
            int nonAlphaCount = text.Count(c => !char.IsLetter(c) && !char.IsWhiteSpace(c) && !char.IsPunctuation(c));
            float nonAlphaRatio = (float)nonAlphaCount / totalChars;
            
            if (nonAlphaRatio > 0.45f) // Вспомогательный порог, повышен как рекомендовано
            {
                DebugLogSpeechValidation($"🚫 Слишком много мусорных символов: {nonAlphaRatio:P} в '{text}'");
                return true;
            }
            
            // 🔥 УЛУЧШЕННАЯ проверка Unicode: разрешаем больше языков
            if (ContainsDefinitelyInvalidUnicode(text))
            {
                DebugLogSpeechValidation($"🚫 Обнаружены явно некорректные символы в '{text}'");
                return true;
            }
            
            // � МЕНЕЕ СТРОГАЯ проверка через европейский фильтр
            DebugLogSpeechValidation($"🔍 Проверка валидности речи: '{text}'");
            
            // Если текст длинный (>15 символов), применяем менее строгие критерии
            bool isLongText = text.Length > 15;
            bool isValid;
            
            if (isLongText)
            {
                // Для длинного текста - менее строгая проверка
                isValid = EuropeanLanguageFilter.IsValidEuropeanSpeech(text) || 
                         ContainsValidWords(text);
            }
            else
            {
                // Для короткого текста - обычная проверка
                isValid = EuropeanLanguageFilter.IsValidEuropeanSpeech(text);
            }
            
            bool isPlaceholder = !isValid;
            
            DebugLogSpeechValidation($"📊 Результат фильтра: IsValid={isValid}, IsPlaceholder={isPlaceholder}, Length={text.Length}");
            
            return isPlaceholder;
        }
        
        // 🚀 НОВЫЙ МЕТОД: Проверка на наличие валидных слов для длинного текста
        private bool ContainsValidWords(string text)
        {
            // Простая проверка - есть ли последовательности букв (возможные слова)
            var words = text.Split(new char[] { ' ', '\t', '\n', '\r', ',', '.', '!', '?' }, 
                                 StringSplitOptions.RemoveEmptyEntries);
            
            int validWords = 0;
            foreach (var word in words)
            {
                // Слово валидно если состоит в основном из букв и имеет разумную длину
                if (word.Length >= 2 && word.Count(char.IsLetter) >= word.Length * 0.7)
                {
                    validWords++;
                }
            }
            
            // Считаем текст валидным если есть хотя бы 2 валидных слова
            return validWords >= 2;
        }
        
        private bool ContainsDefinitelyInvalidUnicode(string text)
        {
            // 🔥 УЛУЧШЕННАЯ Unicode проверка: только явно неправильные символы
            
            int invalidCount = 0;
            int totalChars = text.Length;
            
            foreach (char c in text)
            {
                // Пропускаем обычные символы
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
                    continue;
                    
                int code = (int)c;
                
                // Разрешаем широкий спектр языков
                bool isValidChar = false;
                
                // Базовая и расширенная латиница (большинство европейских языков)
                if ((code >= 0x0041 && code <= 0x005A) || // A-Z
                    (code >= 0x0061 && code <= 0x007A) || // a-z
                    (code >= 0x00C0 && code <= 0x024F) || // Расширенная латиница
                    (code >= 0x1E00 && code <= 0x1EFF))   // Дополнительная расширенная латиница
                {
                    isValidChar = true;
                }
                    
                // Кириллица (русский, украинский, белорусский, болгарский и др.)
                if (code >= 0x0400 && code <= 0x04FF)
                {
                    isValidChar = true;
                }
                
                // Арабские цифры и символы
                if (code >= 0x0600 && code <= 0x06FF)
                {
                    isValidChar = true;
                }
                
                // Греческий алфавит
                if (code >= 0x0370 && code <= 0x03FF)
                {
                    isValidChar = true;
                }
                
                // Если символ не распознан как валидный
                if (!isValidChar)
                {
                    invalidCount++;
                }
            }
            
            // Считаем текст невалидным только если БОЛЬШЕ 50% символов явно неправильные
            float invalidRatio = (float)invalidCount / totalChars;
            
            if (invalidRatio > 0.5f)
            {
                DebugLogSpeechValidation($"🚫 Слишком много неправильных Unicode: {invalidRatio:P} в '{text}'");
                return true;
            }
            
            return false;
        }
        
        private void AnalyzeAudioQuality(byte[] audioData, int sequenceNumber)
        {
            try
            {
                if (audioData.Length < 4)
                {
                    LogMessage($"⚠️ Сегмент #{sequenceNumber} - Слишком короткий для анализа");
                    return;
                }
                
                // Анализируем 32-bit float данные
                float maxLevel = 0f;
                float sumSquares = 0f;
                int sampleCount = audioData.Length / 4;
                int silentSamples = 0;
                
                for (int i = 0; i < audioData.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(audioData, i);
                    float absLevel = Math.Abs(sample);
                    
                    maxLevel = Math.Max(maxLevel, absLevel);
                    sumSquares += sample * sample;
                    
                    if (absLevel < 0.001f) // Практически тишина
                        silentSamples++;
                }
                
                float rms = (float)Math.Sqrt(sumSquares / sampleCount);
                float silenceRatio = (float)silentSamples / sampleCount;
                float durationSeconds = sampleCount / 44100f;
                
                LogMessage($"📊 Сегмент #{sequenceNumber} - Качество аудио:");
                LogMessage($"   └ Длительность: {durationSeconds:F2}с, Семплов: {sampleCount}");
                LogMessage($"   └ Max уровень: {maxLevel:F3}, RMS: {rms:F3}");
                LogMessage($"   └ Тишина: {silenceRatio:P1} ({silentSamples}/{sampleCount})");
                
                // Предупреждения о проблемах
                if (silenceRatio > 0.8f)
                    LogMessage($"⚠️ Сегмент #{sequenceNumber} - Слишком много тишины!");
                    
                if (maxLevel < 0.01f)
                    LogMessage($"⚠️ Сегмент #{sequenceNumber} - Очень тихий сигнал!");
                    
                if (maxLevel > 0.9f)
                    LogMessage($"⚠️ Сегмент #{sequenceNumber} - Возможны искажения!");
                    
                if (durationSeconds < 0.5f)
                    LogMessage($"⚠️ Сегмент #{sequenceNumber} - Очень короткий для качественного распознавания!");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка анализа аудио: {ex.Message}");
            }
        }

        private byte[] ConvertToWav(byte[] audioData)
        {
            try
            {
                // Предполагаем, что входные данные в формате 32-bit float 44100Hz mono
                // Whisper.NET требует 16kHz 16-bit mono WAV
                
                const int targetSampleRate = 16000;
                const int sourceSampleRate = 44100; // Исходная частота WASAPI
                const int channels = 1;
                const int bitsPerSample = 16;
                
                // Проверяем минимальную длину аудио
                if (audioData.Length < 4000) // Менее 250мс при 16кГц
                {
                    LogMessage($"⚠️ Слишком короткий аудиосегмент: {audioData.Length} байт");
                    return new byte[0];
                }
                
                // Конвертируем float32 в int16 с улучшенным ресамплингом
                var samples = new List<short>();
                
                // Улучшенный linear interpolation для ресамплинга
                float ratio = (float)sourceSampleRate / targetSampleRate;
                int sourceLength = audioData.Length / 4;
                int targetLength = (int)(sourceLength / ratio);
                
                for (int i = 0; i < targetLength; i++)
                {
                    float srcIndex = i * ratio;
                    int srcIndexInt = (int)srcIndex;
                    float fraction = srcIndex - srcIndexInt;
                    
                    if (srcIndexInt >= sourceLength - 1)
                        break;
                        
                    // Получаем два семпла для интерполяции
                    float sample1 = BitConverter.ToSingle(audioData, srcIndexInt * 4);
                    float sample2 = srcIndexInt + 1 < sourceLength ? 
                        BitConverter.ToSingle(audioData, (srcIndexInt + 1) * 4) : sample1;
                    
                    // Linear interpolation
                    float interpolated = sample1 + (sample2 - sample1) * fraction;
                    
                    // Ограничиваем диапазон и конвертируем в 16-bit
                    interpolated = Math.Max(-1.0f, Math.Min(1.0f, interpolated));
                    short intSample = (short)(interpolated * 32767f);
                    
                    samples.Add(intSample);
                }
                
                // Проверяем результат ресамплинга
                if (samples.Count == 0)
                {
                    LogMessage("❌ Ресамплинг не дал результатов");
                    return new byte[0];
                }
                
                // Создаем WAV файл с правильным заголовком
                var wav = new List<byte>();
                
                // RIFF header
                wav.AddRange(Encoding.ASCII.GetBytes("RIFF"));
                wav.AddRange(BitConverter.GetBytes(36 + samples.Count * 2)); // File size - 8
                wav.AddRange(Encoding.ASCII.GetBytes("WAVE"));
                
                // fmt chunk
                wav.AddRange(Encoding.ASCII.GetBytes("fmt "));
                wav.AddRange(BitConverter.GetBytes(16)); // PCM chunk size
                wav.AddRange(BitConverter.GetBytes((short)1)); // PCM format
                wav.AddRange(BitConverter.GetBytes((short)channels)); // Mono
                wav.AddRange(BitConverter.GetBytes(targetSampleRate)); // 16kHz
                wav.AddRange(BitConverter.GetBytes(targetSampleRate * channels * bitsPerSample / 8)); // Byte rate
                wav.AddRange(BitConverter.GetBytes((short)(channels * bitsPerSample / 8))); // Block align
                wav.AddRange(BitConverter.GetBytes((short)bitsPerSample)); // 16-bit
                
                // data chunk
                wav.AddRange(Encoding.ASCII.GetBytes("data"));
                wav.AddRange(BitConverter.GetBytes(samples.Count * 2)); // Data size
                
                // Audio data
                foreach (var sample in samples)
                {
                    wav.AddRange(BitConverter.GetBytes(sample));
                }
                
                LogMessage($"✅ WAV конвертация: {audioData.Length} → {wav.Count} байт, {samples.Count} семплов");
                
                return wav.ToArray();
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка конвертации WAV: {ex.Message}");
                return new byte[0]; // Возвращаем пустой массив вместо исходных данных
            }
        }

        #endregion

        #region Audio Conversion

        /// <summary>
        /// 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Гарантированная нормализация аудио до 16kHz mono float
        /// Использует MediaFoundationResampler для качественного ресемплинга и downmix
        /// </summary>
        private byte[] ConvertToWavNormalized(byte[] inputPcm, WaveFormat inputFormat)
        {
            try
            {
                LogMessage($"🔄 Нормализация аудио: {inputFormat.SampleRate}Hz {inputFormat.Channels}ch → 16kHz mono");
                
                // Проверяем минимальную длину
                if (inputPcm.Length < 4000)
                {
                    LogMessage($"⚠️ Слишком короткий аудиосегмент: {inputPcm.Length} байт");
                    return new byte[0];
                }
                
                using var srcStream = new RawSourceWaveStream(
                    new MemoryStream(inputPcm, writable: false), inputFormat);
                
                // Если стерео — сначала приводим к mono через downmix
                IWaveProvider monoProvider;
                if (inputFormat.Channels > 1)
                {
                    // Конвертируем в float32 для качественного downmix
                    var floatProvider = new Wave16ToFloatProvider(srcStream);
                    var sampleProvider = floatProvider.ToSampleProvider();
                    var monoSampleProvider = new StereoToMonoSampleProvider(sampleProvider);
                    monoProvider = monoSampleProvider.ToWaveProvider();
                }
                else
                {
                    monoProvider = srcStream;
                }
                
                // Целевой формат: 16kHz mono float32
                var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
                
                // MediaFoundation высококачественный ресемплинг
                using var resampler = new MediaFoundationResampler(monoProvider, targetFormat)
                {
                    ResamplerQuality = 60 // Максимальное качество
                };
                
                // Читаем все данные
                using var outputStream = new MemoryStream();
                using var writer = new WaveFileWriter(outputStream, targetFormat);
                
                var buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    writer.Write(buffer, 0, bytesRead);
                }
                
                writer.Flush();
                var result = outputStream.ToArray();
                
                // 🔍 ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ ФОРМАТА (по рекомендации анализа)
                LogMessage($"✅ Нормализация завершена: {inputPcm.Length} → {result.Length} байт");
                LogMessage($"📊 Выходной формат перед Whisper: {targetFormat.SampleRate}Hz, {targetFormat.Channels}ch, {targetFormat.BitsPerSample}bit, Encoding={targetFormat.Encoding}");
                
                return result;
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка нормализации аудио: {ex.Message}");
                return new byte[0];
            }
        }

        /// <summary>
        /// Переводит текст по предложениям для лучшего качества и предотвращения ошибок
        /// </summary>
        private async Task TranslateTextInSentences(string text, string sourceLang, string targetLang)
        {
            try
            {
                // Разбиваем текст на предложения
                var sentences = SmartTextSplitter.SplitIntoSentences(text);
                SmartTextSplitter.SplitStats.LogSplitResults(text, sentences);

                var translatedParts = new List<string>();
                
                Invoke(() => {
                    txtTranslatedText.Text = $"🔄 Переводим {sentences.Count} предложений...";
                });

                // Переводим каждое предложение отдельно
                for (int i = 0; i < sentences.Count; i++)
                {
                    string sentence = sentences[i].Trim();
                    
                    if (string.IsNullOrWhiteSpace(sentence))
                        continue;

                    LogMessage($"🔄 Переводим предложение {i + 1}/{sentences.Count}: '{sentence.Substring(0, Math.Min(50, sentence.Length))}{(sentence.Length > 50 ? "..." : "")}'");

                    Invoke(() => {
                        txtTranslatedText.Text = $"🔄 Переводим {i + 1}/{sentences.Count}: {sentence.Substring(0, Math.Min(30, sentence.Length))}{(sentence.Length > 30 ? "..." : "")}";
                    });

                    try
                    {
                        string partResult = await TranslateText(sentence, sourceLang, targetLang);
                        
                        if (!string.IsNullOrEmpty(partResult) && !partResult.Contains("❌"))
                        {
                            translatedParts.Add(partResult.Trim());
                            LogMessage($"✅ Предложение {i + 1} переведено: '{partResult.Substring(0, Math.Min(50, partResult.Length))}{(partResult.Length > 50 ? "..." : "")}'");
                        }
                        else
                        {
                            LogMessage($"❌ Предложение {i + 1} не переведено, используем оригинал");
                            translatedParts.Add(sentence); // Добавляем оригинал если перевод не удался
                        }

                        // Небольшая задержка между запросами для предотвращения rate limiting
                        await Task.Delay(200);
                    }
                    catch (Exception partEx)
                    {
                        LogMessage($"❌ Ошибка перевода предложения {i + 1}: {partEx.Message}");
                        translatedParts.Add(sentence); // Добавляем оригинал при ошибке
                    }
                }

                // Объединяем переведенные части
                string finalTranslation = string.Join(" ", translatedParts.Where(p => !string.IsNullOrWhiteSpace(p)));

                if (!string.IsNullOrEmpty(finalTranslation))
                {
                    LogMessage($"✅ Составной перевод завершен: {finalTranslation.Length} символов");
                    
                    Invoke(() => {
                        txtTranslatedText.Text = finalTranslation;
                    });
                    
                    // Озвучиваем переведенный текст
                    await SpeakText(finalTranslation);
                }
                else
                {
                    LogMessage("❌ Составной перевод не удался");
                    Invoke(() => {
                        txtTranslatedText.Text = "❌ Ошибка составного перевода";
                    });
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage("🛑 Операция составного перевода/озвучивания отменена");
                Invoke(() => {
                    txtTranslatedText.Text = "🛑 Составная операция отменена";
                });
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при переводе по предложениям: {ex.Message}");
                Invoke(() => {
                    txtTranslatedText.Text = $"❌ Ошибка: {ex.Message}";
                });
            }
        }

        private async Task<string> TranslateText(string text, string sourceLang, string targetLang)
        {
            try
            {
                if (googleTranslateClient == null) return string.Empty;
                
                // 📚 УМНАЯ РАЗБИВКА НА ПРЕДЛОЖЕНИЯ (адаптировано из MORT)
                // ⚠️ ВАЖНО: Whisper.NET расставляет знаки препинания в конце предложений
                // Предложения - это неделимые смысловые единицы, их нельзя разрывать при переводе
                
                // Подсчитываем предложения по знакам препинания от Whisper
                var sentenceEndings = new char[] { '.', '!', '?' };
                int sentenceCount = text.Split(sentenceEndings, StringSplitOptions.RemoveEmptyEntries).Length;
                
                // Используем разбивку только для:
                // 1. Длинных текстов (>500 символов) 
                // 2. Содержащих 3+ полных предложения
                // 3. Это обеспечивает сохранение контекста внутри предложений
                bool shouldUseSplitting = text.Length > 500 && sentenceCount >= 3;
                
                if (shouldUseSplitting)
                {
                    LogMessage($"📖 Длинный многопредложенческий текст ({text.Length} символов, {sentenceCount} предложений) - разбиваем на смысловые группы");
                    
                    // Создаем функцию для перевода групп предложений
                    Func<string, string, string, Task<string>> translateFunction = async (sentenceGroup, srcLang, tgtLang) =>
                    {
                        return await TranslateSingleTextPart(sentenceGroup, srcLang, tgtLang);
                    };
                    
                    // Используем SmartTextSplitter для группировки полных предложений
                    return await SmartTextSplitter.TranslateLongTextInParts(text, translateFunction, sourceLang, targetLang);
                }
                
                // Обычный перевод для коротких текстов и одиночных предложений
                LogMessage($"📝 Обычный перевод: {text.Length} символов");
                return await TranslateSingleTextPart(text, sourceLang, targetLang);
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка в TranslateText: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Переводит отдельную часть текста (извлечено из основного метода)
        /// </summary>
        private async Task<string> TranslateSingleTextPart(string text, string sourceLang, string targetLang)
        {
            try
            {
                if (googleTranslateClient == null) return string.Empty;
                
                // Use Google Translate API (public endpoint)
                var request = new RestRequest("translate_a/single", Method.Get);
                request.AddParameter("client", "gtx");
                request.AddParameter("sl", sourceLang);
                request.AddParameter("tl", targetLang);
                request.AddParameter("dt", "t");
                request.AddParameter("q", text);
                request.AddParameter("ie", "UTF-8");
                request.AddParameter("oe", "UTF-8");
                
                var response = await googleTranslateClient.ExecuteAsync(request);
                
                if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    LogMessage($"🔍 Google Translate ответ: {response.Content.Substring(0, Math.Min(200, response.Content.Length))}...");
                    
                    // Parse Google Translate response - собираем ВСЕ сегменты перевода
                    try
                    {
                        // Google возвращает массив массивов, где [0] содержит все сегменты перевода
                        var jsonArray = JsonConvert.DeserializeObject<dynamic>(response.Content);
                        
                        if (jsonArray is Newtonsoft.Json.Linq.JArray outerArray && outerArray.Count > 0)
                        {
                            var firstGroup = outerArray[0];
                            if (firstGroup is Newtonsoft.Json.Linq.JArray firstArray && firstArray.Count > 0)
                            {
                                var translatedSegments = new List<string>();
                                
                                // Собираем ВСЕ сегменты перевода, а не только первый
                                foreach (var segment in firstArray)
                                {
                                    if (segment is Newtonsoft.Json.Linq.JArray segmentArray && segmentArray.Count > 0)
                                    {
                                        string segmentText = segmentArray[0]?.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(segmentText) && segmentText.Trim() != "")
                                        {
                                            translatedSegments.Add(segmentText);
                                            LogMessage($"🧩 Сегмент перевода: '{segmentText}'");
                                        }
                                    }
                                }
                                
                                // Объединяем все сегменты в единый перевод
                                if (translatedSegments.Count > 0)
                                {
                                    string fullTranslation = string.Join("", translatedSegments);
                                    LogMessage($"✅ Полный перевод из {translatedSegments.Count} сегментов: '{fullTranslation}'");
                                    return fullTranslation;
                                }
                            }
                        }
                        
                        LogMessage("❌ Не удалось извлечь перевод из JSON ответа");
                        return string.Empty;
                    }
                    catch (JsonException jsonEx)
                    {
                        LogMessage($"❌ Ошибка парсинга JSON: {jsonEx.Message}");
                        
                        // Fallback: улучшенный regex парсинг всех сегментов
                        var matches = System.Text.RegularExpressions.Regex.Matches(response.Content, @"""([^""]+)"",""[^""]*""");
                        if (matches.Count > 0)
                        {
                            var allSegments = new List<string>();
                            foreach (System.Text.RegularExpressions.Match regexMatch in matches)
                            {
                                if (regexMatch.Groups.Count > 1)
                                {
                                    string segment = regexMatch.Groups[1].Value;
                                    if (!string.IsNullOrEmpty(segment) && segment.Trim() != "")
                                    {
                                        allSegments.Add(segment);
                                        LogMessage($"🧩 Regex сегмент: '{segment}'");
                                    }
                                }
                            }
                            
                            if (allSegments.Count > 0)
                            {
                                string combinedResult = string.Join("", allSegments);
                                LogMessage($"✅ Fallback парсинг {allSegments.Count} сегментов: '{combinedResult}'");
                                return combinedResult;
                            }
                        }
                        
                        // Простой fallback если сложный не сработал
                        var simpleMatch = System.Text.RegularExpressions.Regex.Match(response.Content, @"\[\[\[""([^""]+)""");
                        if (simpleMatch.Success && simpleMatch.Groups.Count > 1)
                        {
                            string simpleResult = simpleMatch.Groups[1].Value;
                            LogMessage($"✅ Простой fallback парсинг: '{simpleResult}'");
                            return simpleResult;
                        }
                        
                        return string.Empty;
                    }
                }
                
                LogMessage($"❌ Перевод не удался: {response.ErrorMessage ?? "Неизвестная ошибка"}, StatusCode: {response.StatusCode}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка API перевода: {ex.Message}");
                return string.Empty;
            }
        } // Конец TranslateSingleTextPart

        /// <summary>
        /// 🔊 НОВЫЙ МЕТОД: Последовательное TTS с ожиданием завершения предыдущего озвучивания
        /// </summary>
        private async Task SpeakTextSequentially(string text)
        {
            // Получаем уникальный номер последовательности для этого TTS
            int ttsSequenceNum = Interlocked.Increment(ref ttsSequenceNumber);
            
            try
            {
                // Ждем своей очереди (только одно TTS выполняется одновременно)
                await ttsProcessingSemaphore.WaitAsync();
                
                LogMessage($"🔢 TTS операция #{ttsSequenceNum} начата (ждем завершения предыдущей)");
                
                // Выполняем TTS
                await SpeakTextInternal(text, ttsSequenceNum);
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка последовательного TTS #{ttsSequenceNum}: {ex.Message}");
            }
            finally
            {
                // Освобождаем семафор для следующего TTS
                ttsProcessingSemaphore.Release();
                LogMessage($"✅ TTS операция #{ttsSequenceNum} завершена, семафор освобожден");
            }
        }

        private async Task SpeakText(string text)
        {
            // Перенаправляем на последовательное выполнение
            await SpeakTextSequentially(text);
        }

        private async Task SpeakTextInternal(string text, int ttsSequenceNumber = 0)
        {
            try
            {
                if (speechSynthesizer == null || ttsVoiceManager == null) return;
                
                // 🔊 ИСПРАВЛЕНО: Убираем принудительную отмену - теперь ждем в очереди
                LogMessage($"🔊 TTS #{ttsSequenceNumber} начинает озвучивание: '{text}'");
                
                // Дополнительная проверка состояния синтезатора
                if (speechSynthesizer.State == System.Speech.Synthesis.SynthesizerState.Speaking)
                {
                    LogMessage($"⚠️ TTS #{ttsSequenceNumber}: Синтезатор занят, ждем освобождения...");
                    // Ждем, пока синтезатор освободится (максимум 10 секунд)
                    for (int i = 0; i < 100; i++)
                    {
                        if (speechSynthesizer.State != System.Speech.Synthesis.SynthesizerState.Speaking)
                            break;
                        await Task.Delay(100);
                    }
                }
                
                // Дополнительная пауза для предотвращения конфликтов
                await Task.Delay(50);
                
                isTTSActive = true; // Устанавливаем флаг активности
                LogMessage($"🔊 Озвучивание: '{text}'");
                
                // Уведомляем SmartAudioManager о начале TTS
                smartAudioManager?.NotifyTTSStarted();
                
                // АВТОМАТИЧЕСКИЙ ВЫБОР ГОЛОСА НА ОСНОВЕ ЯЗЫКА ТЕКСТА
                ttsVoiceManager.SelectVoiceForText(text);
                
                // Используем асинхронный подход для корректной отмены
                var completionSource = new TaskCompletionSource<bool>();
                System.Speech.Synthesis.Prompt prompt = null;
                
                try
                {
                    // Дополнительная проверка перед вызовом Speak
                    if (speechSynthesizer?.State != System.Speech.Synthesis.SynthesizerState.Ready)
                    {
                        LogMessage("⚠️ Синтезатор не готов, пропускаем озвучивание");
                        return;
                    }
                    
                    // Обработчики событий для асинхронного TTS
                    EventHandler<System.Speech.Synthesis.SpeakCompletedEventArgs> onCompleted = null;
                    EventHandler<System.Speech.Synthesis.SpeakProgressEventArgs> onProgress = null;
                    
                    onCompleted = (s, e) => {
                        speechSynthesizer.SpeakCompleted -= onCompleted;
                        speechSynthesizer.SpeakProgress -= onProgress;
                        isTTSActive = false; // Сбрасываем флаг активности
                        
                        if (e.Cancelled)
                        {
                            LogMessage("🛑 TTS операция отменена асинхронно");
                            completionSource.SetCanceled();
                        }
                        else if (e.Error != null)
                        {
                            LogMessage($"❌ Ошибка TTS: {e.Error.Message}");
                            completionSource.SetException(e.Error);
                        }
                        else
                        {
                            completionSource.SetResult(true);
                        }
                    };
                    
                    onProgress = (s, e) => {
                        // Дополнительная проверка на отмену во время выполнения
                        if (speechSynthesizer?.State == System.Speech.Synthesis.SynthesizerState.Ready)
                        {
                            speechSynthesizer.SpeakAsyncCancelAll();
                        }
                    };
                    
                    speechSynthesizer.SpeakCompleted += onCompleted;
                    speechSynthesizer.SpeakProgress += onProgress;
                    
                    // Запускаем асинхронное озвучивание
                    prompt = speechSynthesizer.SpeakAsync(text);
                    
                    // Ожидаем завершения с возможностью отмены
                    await completionSource.Task;
                }
                catch (OperationCanceledException)
                {
                    isTTSActive = false; // Сбрасываем флаг при отмене
                    LogMessage("🛑 TTS операция отменена");
                    if (prompt != null)
                    {
                        speechSynthesizer?.SpeakAsyncCancel(prompt);
                    }
                    throw; // Пробрасываем для обработки во внешнем catch
                }
                catch (Exception ex)
                {
                    isTTSActive = false; // Сбрасываем флаг при ошибке
                    LogMessage($"❌ Внутренняя ошибка TTS: {ex.Message}");
                    if (prompt != null)
                    {
                        speechSynthesizer?.SpeakAsyncCancel(prompt);
                    }
                    throw;
                }
                
                // Уведомляем SmartAudioManager о завершении TTS
                smartAudioManager?.NotifyTTSCompleted();
                
                LogMessage($"✅ TTS #{ttsSequenceNumber} озвучивание завершено");
            }
            catch (OperationCanceledException)
            {
                // Специальная обработка отмены TTS
                isTTSActive = false; // Гарантированно сбрасываем флаг
                smartAudioManager?.NotifyTTSCompleted();
                LogMessage($"🛑 TTS #{ttsSequenceNumber} отменен пользователем");
            }
            catch (Exception ex)
            {
                // В случае других ошибок также уведомляем о завершении TTS
                isTTSActive = false; // Гарантированно сбрасываем флаг
                smartAudioManager?.NotifyTTSCompleted();
                LogMessage($"❌ Ошибка TTS #{ttsSequenceNumber}: {ex.Message}");
            }
        }

        private string GetLanguageCode(string languageName)
        {
            // Для автоопределения возвращаем "auto"
            if (languageName == "Автоопределение")
                return "auto";
                
            return languageCodes.TryGetValue(languageName, out string? code) ? code : "en";
        }

        // 🚀 МЕТОД ДЛЯ АВТОМАТИЧЕСКОГО ПЕРЕВОДА И ОЗВУЧИВАНИЯ
        private async Task TranslateAndSpeak(string text)
        {
            try
            {
                string sourceLang = "";
                string targetLang = "";
                
                // Безопасное получение значений из UI потока
                Invoke(() => {
                    sourceLang = GetLanguageCode(cbSourceLang.SelectedItem?.ToString() ?? "Автоопределение");
                    targetLang = GetLanguageCode(cbTargetLang.SelectedItem?.ToString() ?? "Русский");
                });
                
                LogMessage($"🌐 Перевод: {sourceLang} → {targetLang}");
                
                Invoke(() => {
                    txtTranslatedText.Text = "🔄 Переводим...";
                });

                // Проверяем, нужно ли разбивать текст на предложения
                if (SmartTextSplitter.ShouldSplit(text))
                {
                    LogMessage($"📝 Текст длинный ({text.Length} символов), разбиваем на предложения");
                    await TranslateTextInSentences(text, sourceLang, targetLang);
                }
                else
                {
                    // Переводим как обычно
                    string translatedText = await TranslateText(text, sourceLang, targetLang);
                    
                    if (!string.IsNullOrEmpty(translatedText))
                    {
                        // Анализируем качество перевода
                        string qualityInfo = AnalyzeTranslationQuality(text, translatedText);
                        LogMessage($"✅ Переведено{qualityInfo}: '{translatedText}'");
                        
                        Invoke(() => {
                            txtTranslatedText.Text = translatedText;
                        });
                        
                        // Speak translated text
                        await SpeakText(translatedText);
                    }
                    else
                    {
                        LogMessage("❌ Перевод не удался");
                        Invoke(() => {
                            txtTranslatedText.Text = "❌ Ошибка перевода";
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage("🛑 Операция перевода/озвучивания отменена");
                Invoke(() => {
                    txtTranslatedText.Text = "🛑 Операция отменена";
                });
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка перевода: {ex.Message}");
                Invoke(() => {
                    txtTranslatedText.Text = $"❌ Ошибка: {ex.Message}";
                });
            }
        }

        #endregion

        #region UI Events

        private async void btnTestTTS_Click(object sender, EventArgs e)
        {
            try
            {
                string testText = "";
                
                // Безопасное получение значения из UI потока
                Invoke(() => {
                    testText = cbTargetLang.SelectedItem?.ToString() == "Русский" 
                        ? "Тест системы озвучивания текста" 
                        : "Text to speech system test";
                });
                    
                await SpeakText(testText);
            }
            catch (OperationCanceledException)
            {
                LogMessage("🛑 Тест TTS отменен");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка теста TTS: {ex.Message}");
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                LogMessage("🔄 Закрытие приложения...");
                
                // Сохраняем настройки
                SaveCurrentSettings();
                
                // Остановка захвата
                try
                {
                    StopAudioCapture().Wait(3000); // Ждем максимум 3 секунды
                }
                catch (Exception stopEx)
                {
                    LogMessage($"❌ Ошибка остановки при закрытии: {stopEx.Message}");
                }
                
                // Очистка ресурсов
                speechSynthesizer?.Dispose();
                audioLevelTimer?.Dispose();
                smartAudioManager?.Dispose();
                googleTranslateClient?.Dispose();
                
                // 🚀 Очистка мониторинга устройств
                CleanupDeviceNotifications();
                
                // 🚀 Очистка MediaFoundation
                MediaFoundationApi.Shutdown();
                
                LogMessage("✅ Приложение закрыто");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при закрытии: {ex.Message}");
            }
        }

        #endregion

        #region Logging

        /// <summary>
        /// 🚀 ОПТИМИЗИРОВАННОЕ ЛОГИРОВАНИЕ: Выбирает между полным UI логированием и только Debug
        /// </summary>
        private void LogMessageDebug(string message)
        {
            if (enableDetailedLogging)
            {
                LogMessage(message); // Полное логирование с UI
            }
            else
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"); // Только Debug консоль
            }
        }

        public void LogMessage(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] {message}";
            
            Debug.WriteLine(logEntry);
            
            // Проверяем, что форма не была освобождена и еще существует
            if (IsDisposed || !IsHandleCreated)
            {
                return; // Форма закрыта, не пытаемся обновить UI
            }
            
            try
            {
                if (InvokeRequired)
                {
                    Invoke(() => AddLogEntry(logEntry));
                }
                else
                {
                    AddLogEntry(logEntry);
                }
            }
            catch (ObjectDisposedException)
            {
                // Форма была освобождена между проверкой и вызовом - игнорируем
                Debug.WriteLine($"[{timestamp}] ⚠️ Форма закрыта, пропускаем лог: {message}");
            }
            catch (InvalidOperationException)
            {
                // Handle тоже может быть недоступен
                Debug.WriteLine($"[{timestamp}] ⚠️ Handle недоступен, пропускаем лог: {message}");
            }
        }

        private void AddLogEntry(string logEntry)
        {
            txtLogs.AppendText(logEntry + Environment.NewLine);
            txtLogs.SelectionStart = txtLogs.Text.Length;
            txtLogs.ScrollToCaret();
        }

        /// <summary>
        /// 📚 Отображает статистику режима аудиокниги
        /// </summary>
        private void ShowAudiobookStatistics()
        {
            try
            {
                if (smartAudioManager != null)
                {
                    string stats = smartAudioManager.GetAudiobookStatistics();
                    LogMessage("📊 СТАТИСТИКА АУДИОКНИГИ:");
                    
                    // Разбиваем статистику на отдельные строки для лучшей читаемости
                    var lines = stats.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.Contains("СТАТИСТИКА РЕЖИМА АУДИОКНИГИ"))
                        {
                            LogMessage($"   {line.Trim()}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при получении статистики аудиокниги: {ex.Message}");
            }
        }

        #endregion

        #region 🚀 Новые методы для стабильной архитектуры

        /// <summary>
        /// Обработка аудио с помощью Whisper в новой архитектуре
        /// </summary>
        private async Task<string?> ProcessAudioWithWhisper(float[] audioData, CancellationToken ct)
        {
            try
            {
                if (whisperProcessor == null || audioData == null || audioData.Length == 0)
                    return null;

                LogMessage($"🎯 Обработка аудио сегмента: {audioData.Length} семплов ({(float)audioData.Length / 16000:F2}с)");

                // Качественный анализ аудио
                var analysisResult = AnalyzeAudioQuality(audioData);
                LogMessage($"📊 Анализ качества: {analysisResult}");

                // Проверка на достаточную активность
                if (analysisResult.RmsLevel < 0.001f)
                {
                    LogMessage("🔇 Аудио слишком тихое, пропускаем STT");
                    return null;
                }

                // Whisper STT с improved настройками
                using var audioStream = new MemoryStream();
                WriteWavHeader(audioStream, audioData.Length, 16000, 1);
                
                // Конвертация float[] в PCM bytes
                var pcmBytes = new byte[audioData.Length * 2];
                for (int i = 0; i < audioData.Length; i++)
                {
                    var sample = (short)(audioData[i] * 32767f);
                    pcmBytes[i * 2] = (byte)(sample & 0xFF);
                    pcmBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
                audioStream.Write(pcmBytes, 0, pcmBytes.Length);
                audioStream.Position = 0;

                // STT обработка
                var segments = new List<string>();
                await foreach (var segment in whisperProcessor.ProcessAsync(audioStream, ct))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        var cleanedText = CleanWhisperText(segment.Text);
                        if (!string.IsNullOrWhiteSpace(cleanedText) && 
                            !IsPlaceholderToken(cleanedText))
                        {
                            segments.Add(cleanedText);
                            LogMessage($"📝 STT сегмент: '{cleanedText}' (conf: {segment.Probability:F3})");
                        }
                    }
                }

                var finalText = string.Join(" ", segments).Trim();
                
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    LogMessage($"✅ STT результат: '{finalText}'");
                    return finalText;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка Whisper STT: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Обработка распознанного текста в новой архитектуре
        /// </summary>
        private async Task ProcessRecognizedText(string recognizedText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(recognizedText))
                    return;

                // Отображение распознанного текста
                this.BeginInvoke(() => 
                {
                    if (txtRecognized != null)
                        txtRecognized.Text = recognizedText;
                    LogMessage($"🎯 Распознан текст: {recognizedText}");
                });

                // Определение языков
                string sourceLanguage = GetSelectedLanguage(cbSourceLanguage);
                string targetLanguage = GetSelectedLanguage(cbTargetLanguage);

                if (sourceLanguage == targetLanguage)
                {
                    LogMessage("⚠️ Исходный и целевой языки одинаковы, пропускаем перевод");
                    await ProcessTtsOutput(recognizedText, targetLanguage);
                    return;
                }

                // Перевод
                var translatedText = await TranslateTextAsync(recognizedText, sourceLanguage, targetLanguage);
                
                if (!string.IsNullOrWhiteSpace(translatedText))
                {
                    this.BeginInvoke(() => 
                    {
                        if (txtTranslated != null)
                            txtTranslated.Text = translatedText;
                        LogMessage($"🌐 Переведено: {translatedText}");
                    });

                    // TTS озвучка
                    await ProcessTtsOutput(translatedText, targetLanguage);
                }
                else
                {
                    LogMessage("⚠️ Перевод не удался, озвучиваем оригинал");
                    await ProcessTtsOutput(recognizedText, sourceLanguage);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки распознанного текста: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработка TTS вывода в новой архитектуре
        /// </summary>
        private async Task ProcessTtsOutput(string text, string language)
        {
            try
            {
                if (stableTtsEngine == null || string.IsNullOrWhiteSpace(text))
                    return;

                // Определение языка для TTS
                var ttsLanguage = GetTtsLanguageCode(language);
                
                // Установка языка если нужно
                if (!string.IsNullOrEmpty(ttsLanguage))
                {
                    stableTtsEngine.SetLanguage(ttsLanguage);
                }

                // Озвучка через стабильный TTS Engine
                var success = await stableTtsEngine.SpeakAsync(text, ttsLanguage);
                
                if (!success)
                {
                    LogMessage($"⚠️ Не удалось добавить в очередь TTS: {text}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка TTS обработки: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение кода языка для TTS
        /// </summary>
        private string GetTtsLanguageCode(string language)
        {
            return language.ToLowerInvariant() switch
            {
                "ru" or "русский" => "ru-RU",
                "en" or "английский" => "en-US",
                "de" or "немецкий" => "de-DE",
                "fr" or "французский" => "fr-FR",
                "es" or "испанский" => "es-ES",
                "it" or "итальянский" => "it-IT",
                "ja" or "японский" => "ja-JP",
                "zh" or "китайский" => "zh-CN",
                _ => "en-US"
            };
        }

        /// <summary>
        /// Обработчик таймера статистики
        /// </summary>
        private void StatisticsTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (slidingWindowAggregator != null)
                {
                    var aggStats = slidingWindowAggregator.GetStatistics();
                    LogMessage($"📊 Агрегатор: {aggStats}");
                }

                if (stableTtsEngine != null)
                {
                    var ttsStats = stableTtsEngine.GetStatistics();
                    LogMessage($"📊 TTS: {ttsStats}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка получения статистики: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление UI индикатора аудио уровня
        /// </summary>
        private void UpdateAudioLevelUI()
        {
            try
            {
                var percentage = Math.Min(100, (int)(currentAudioLevel * 1000));
                
                if (Math.Abs(percentage - lastAudioPercentage) > 5 || 
                    DateTime.Now.Subtract(lastUIUpdate).TotalMilliseconds > UI_UPDATE_INTERVAL_MS)
                {
                    if (progressBarAudio != null)
                        progressBarAudio.Value = percentage;
                    lastAudioPercentage = percentage;
                    lastUIUpdate = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обновления UI аудио уровня: {ex.Message}");
            }
        }

        /// <summary>
        /// Анализ качества аудио для новой архитектуры
        /// </summary>
        private AudioQualityAnalysis AnalyzeAudioQuality(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return new AudioQualityAnalysis();

            double sum = 0;
            double maxAmplitude = 0;
            int clippedSamples = 0;

            for (int i = 0; i < audioData.Length; i++)
            {
                var sample = Math.Abs(audioData[i]);
                sum += sample * sample;
                maxAmplitude = Math.Max(maxAmplitude, sample);
                
                if (sample > 0.95)
                    clippedSamples++;
            }

            var rms = Math.Sqrt(sum / audioData.Length);
            var clippingRate = (double)clippedSamples / audioData.Length;

            // Простой VAD на основе RMS и спектральной энергии
            double spectralEnergy = 0;
            for (int i = 1; i < audioData.Length; i++)
            {
                var diff = audioData[i] - audioData[i - 1];
                spectralEnergy += diff * diff;
            }
            spectralEnergy = Math.Sqrt(spectralEnergy / (audioData.Length - 1));

            return new AudioQualityAnalysis
            {
                RmsLevel = (float)rms,
                MaxAmplitude = (float)maxAmplitude,
                ClippingRate = (float)clippingRate,
                SpectralEnergy = (float)spectralEnergy,
                Duration = (float)audioData.Length / 16000f,
                HasSpeech = rms > 0.001 && spectralEnergy > 0.0005
            };
        }

        #endregion

        #region Helper Classes
        
        #endregion

        #region Form Cleanup

        private void Form1_OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                Debug.WriteLine("🔄 Начало очистки ресурсов при закрытии формы...");
                
                // Останавливаем захват аудио
                isCapturing = false;
                isDisposed = true;
                
                // 🚀 ОЧИСТКА НОВЫХ СТАБИЛЬНЫХ КОМПОНЕНТОВ
                CleanupStableComponents().GetAwaiter().GetResult();
                
                // 🚀 КРИТИЧЕСКАЯ ОЧИСТКА: Теплый Whisper instance
                CleanupWhisperResources();
                
                // Останавливаем и освобождаем WASAPI захват (legacy)
                if (wasapiCapture != null)
                {
                    try
                    {
                        wasapiCapture.DataAvailable -= OnAudioDataAvailable;
                        wasapiCapture.StopRecording();
                        wasapiCapture.Dispose();
                        wasapiCapture = null;
                        Debug.WriteLine("✅ WASAPI захват остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки WASAPI: {ex.Message}");
                    }
                }
                
                // Останавливаем и освобождаем WaveIn захват (legacy)
                if (waveInCapture != null)
                {
                    try
                    {
                        waveInCapture.DataAvailable -= OnAudioDataAvailable;
                        waveInCapture.StopRecording();
                        waveInCapture.Dispose();
                        waveInCapture = null;
                        Debug.WriteLine("✅ WaveIn захват остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки WaveIn: {ex.Message}");
                    }
                }
                
                // Останавливаем таймеры
                if (audioLevelTimer != null)
                {
                    try
                    {
                        audioLevelTimer.Stop();
                        audioLevelTimer.Dispose();
                        audioLevelTimer = null;
                        Debug.WriteLine("✅ Таймер остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки таймера: {ex.Message}");
                    }
                }
                
                // Останавливаем TTS
                if (speechSynthesizer != null)
                {
                    try
                    {
                        speechSynthesizer.SpeakAsyncCancelAll();
                        speechSynthesizer.Dispose();
                        speechSynthesizer = null;
                        Debug.WriteLine("✅ TTS остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки TTS: {ex.Message}");
                    }
                }
                
                // Освобождаем TTS Voice Manager
                if (ttsVoiceManager != null)
                {
                    try
                    {
                        ttsVoiceManager.Dispose();
                        ttsVoiceManager = null;
                        Debug.WriteLine("✅ TTS Voice Manager освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка освобождения TTS Voice Manager: {ex.Message}");
                    }
                }
                
                // Освобождаем стриминговые компоненты
                if (streamingProcessor != null)
                {
                    try
                    {
                        streamingProcessor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                        streamingProcessor = null;
                        Debug.WriteLine("✅ StreamingWhisperProcessor остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки StreamingProcessor: {ex.Message}");
                    }
                }
                
                if (audioResampler != null)
                {
                    try
                    {
                        audioResampler.Dispose();
                        audioResampler = null;
                        Debug.WriteLine("✅ AudioResampler остановлен и освобожден");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки AudioResampler: {ex.Message}");
                    }
                }
                
                // Освобождаем семафор последовательной обработки
                try
                {
                    audioProcessingSemaphore?.Dispose();
                    Debug.WriteLine("✅ AudioProcessingSemaphore освобожден");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Ошибка освобождения AudioProcessingSemaphore: {ex.Message}");
                }
                
                // Освобождаем семафор TTS
                try
                {
                    ttsProcessingSemaphore?.Dispose();
                    Debug.WriteLine("✅ TtsProcessingSemaphore освобожден");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Ошибка освобождения TtsProcessingSemaphore: {ex.Message}");
                }
                
                // Устанавливаем флаг освобождения
                isDisposed = true;
                
                Debug.WriteLine("✅ Очистка ресурсов завершена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Критическая ошибка при очистке ресурсов: {ex.Message}");
            }
        }

        /// <summary>
        /// 🚀 Очистка новых стабильных компонентов
        /// </summary>
        private async Task CleanupStableComponents()
        {
            try
            {
                Debug.WriteLine("🔄 Очистка стабильных компонентов...");

                // Остановка таймера статистики
                if (statisticsTimer != null)
                {
                    try
                    {
                        statisticsTimer.Stop();
                        statisticsTimer.Dispose();
                        statisticsTimer = null;
                        Debug.WriteLine("✅ Таймер статистики остановлен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки таймера статистики: {ex.Message}");
                    }
                }

                // Остановка стабильного аудио-захвата
                if (stableAudioCapture != null)
                {
                    try
                    {
                        await stableAudioCapture.StopCaptureAsync();
                        stableAudioCapture.Dispose();
                        stableAudioCapture = null;
                        Debug.WriteLine("✅ Стабильный аудио-захват остановлен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки стабильного захвата: {ex.Message}");
                    }
                }

                // Остановка агрегатора скользящего окна
                if (slidingWindowAggregator != null)
                {
                    try
                    {
                        await slidingWindowAggregator.FlushAsync();
                        slidingWindowAggregator.Dispose();
                        slidingWindowAggregator = null;
                        Debug.WriteLine("✅ Агрегатор скользящего окна остановлен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки агрегатора: {ex.Message}");
                    }
                }

                // Остановка стабильного TTS Engine
                if (stableTtsEngine != null)
                {
                    try
                    {
                        await stableTtsEngine.ClearQueueAsync();
                        stableTtsEngine.Dispose();
                        stableTtsEngine = null;
                        Debug.WriteLine("✅ Стабильный TTS Engine остановлен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка остановки стабильного TTS: {ex.Message}");
                    }
                }

                // Очистка Whisper компонентов
                if (whisperProcessor != null)
                {
                    try
                    {
                        whisperProcessor.Dispose();
                        whisperProcessor = null;
                        Debug.WriteLine("✅ Whisper Processor очищен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка очистки Whisper Processor: {ex.Message}");
                    }
                }

                if (whisperFactory != null)
                {
                    try
                    {
                        whisperFactory.Dispose();
                        whisperFactory = null;
                        Debug.WriteLine("✅ Whisper Factory очищен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Ошибка очистки Whisper Factory: {ex.Message}");
                    }
                }

                Debug.WriteLine("✅ Все стабильные компоненты очищены");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Критическая ошибка очистки стабильных компонентов: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Логирование для отладки валидации речи (только в Debug режиме)
        /// </summary>
        private void DebugLogSpeechValidation(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[SPEECH_DEBUG] {message}");
        }

        private class AudioDevice
        {
            public string Name { get; set; } = string.Empty;
            public MMDevice Device { get; set; } = null!;
            
            public override string ToString() => Name;
        }

        /// <summary>
        /// Анализирует качество перевода и возвращает информацию о нем
        /// </summary>
        private string AnalyzeTranslationQuality(string original, string translated)
        {
            try
            {
                var indicators = new List<string>();
                
                // Проверяем соответствие длины
                double lengthRatio = (double)translated.Length / original.Length;
                if (lengthRatio > 1.5) indicators.Add("📏+");  // Заметно длиннее
                else if (lengthRatio < 0.5) indicators.Add("📏-");  // Заметно короче
                
                // Проверяем сохранение знаков препинания
                int originalPunct = original.Count(c => char.IsPunctuation(c));
                int translatedPunct = translated.Count(c => char.IsPunctuation(c));
                if (Math.Abs(originalPunct - translatedPunct) > 2) indicators.Add("❓");
                
                // Проверяем на потенциальные проблемы в переводе
                if (translated.Contains("...") && !original.Contains("...")) indicators.Add("🔍");
                if (translated.Contains("[") || translated.Contains("]")) indicators.Add("⚠️");
                
                // Определяем тип перевода по языку
                bool isRussianSource = System.Text.RegularExpressions.Regex.IsMatch(original, @"[а-яё]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool isEnglishSource = System.Text.RegularExpressions.Regex.IsMatch(original, @"[a-z]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                if (isRussianSource && original == translated) indicators.Add("🔄RU→RU");
                else if (isEnglishSource) indicators.Add("🔄EN→RU");
                else if (isRussianSource) indicators.Add("🔄RU→?");
                
                return indicators.Count > 0 ? $" ({string.Join("", indicators)})" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Анализ качества аудио для новой стабильной архитектуры
        /// </summary>
        public class AudioQualityAnalysis
        {
            public float RmsLevel { get; set; }
            public float MaxAmplitude { get; set; }
            public float ClippingRate { get; set; }
            public float SpectralEnergy { get; set; }
            public float Duration { get; set; }
            public bool HasSpeech { get; set; }
            
            public override string ToString()
            {
                return $"RMS: {RmsLevel:F6}, Max: {MaxAmplitude:F3}, Clipping: {ClippingRate:P1}, " +
                       $"Spectral: {SpectralEnergy:F6}, Duration: {Duration:F2}s, Speech: {HasSpeech}";
            }
        }

        #region Вспомогательные методы для новой архитектуры

        /// <summary>
        /// Создание WAV заголовка для аудио данных
        /// </summary>
        private void WriteWavHeader(MemoryStream stream, int dataLength, int sampleRate, int channels)
        {
            var bytesPerSample = 2; // 16-bit
            var blockAlign = channels * bytesPerSample;
            var averageBytesPerSecond = sampleRate * blockAlign;
            var dataSize = dataLength * bytesPerSample;
            
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            
            // RIFF header
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + dataSize);
            writer.Write("WAVE".ToCharArray());
            
            // fmt chunk
            writer.Write("fmt ".ToCharArray());
            writer.Write(16); // fmt chunk size
            writer.Write((short)1); // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(averageBytesPerSecond);
            writer.Write((short)blockAlign);
            writer.Write((short)(bytesPerSample * 8));
            
            // data chunk
            writer.Write("data".ToCharArray());
            writer.Write(dataSize);
        }

        /// <summary>
        /// Очистка текста от Whisper для стабильной архитектуры
        /// </summary>
        private string CleanWhisperText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Базовая очистка
            text = text.Trim();
            
            // Удаление повторяющихся символов
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(.)\1{3,}", "$1$1");
            
            // Удаление лишних пробелов
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            
            return text;
        }

        /// <summary>
        /// Получение выбранного языка из ComboBox
        /// </summary>
        private string GetSelectedLanguage(System.Windows.Forms.ComboBox? comboBox)
        {
            if (comboBox?.SelectedItem is string selectedLanguage)
            {
                return languageCodes.TryGetValue(selectedLanguage, out var code) ? code : "ru";
            }
            return "ru";
        }

        /// <summary>
        /// Асинхронный перевод текста
        /// </summary>
        private async Task<string?> TranslateTextAsync(string text, string sourceLanguage, string targetLanguage)
        {
            try
            {
                if (googleTranslateClient == null)
                    return null;

                // Простая заглушка для перевода
                // В реальном приложении здесь будет вызов Google Translate API
                await Task.Delay(100); // Имитация сетевого запроса
                
                return $"[TRANSLATED from {sourceLanguage} to {targetLanguage}] {text}";
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка перевода: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Device Notification Handler
        
        // 🚀 АВТОМАТИЧЕСКОЕ ПЕРЕПОДКЛЮЧЕНИЕ устройств при HDMI/Bluetooth переключении
        private MMDeviceEnumerator? deviceEnumerator;
        private AudioDeviceNotificationClient? notificationClient;
        
        private void InitializeDeviceNotifications()
        {
            try
            {
                deviceEnumerator = new MMDeviceEnumerator();
                notificationClient = new AudioDeviceNotificationClient(this);
                deviceEnumerator.RegisterEndpointNotificationCallback(notificationClient);
                
                LogMessage("🔔 Инициализирован мониторинг аудиоустройств");
            }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Не удалось инициализировать мониторинг устройств: {ex.Message}");
            }
        }
        
        public void OnDeviceChanged()
        {
            // Вызывается при изменении аудиоустройств
            LogMessage("🔄 Обнаружено изменение аудиоустройств - переподключение...");
            
            // Валидация: проверяем, что мы не в UI потоке (вызывается из уведомлений системы)
            if (InvokeRequired)
            {
                LogMessage("⚠️ OnDeviceChanged вызван из не-UI потока - переносим в UI поток");
                Invoke(new Action(OnDeviceChanged));
                return;
            }
            
            Task.Run(async () =>
            {
                await Task.Delay(1000); // Короткая пауза для стабилизации
                
                try
                {
                    Invoke(() =>
                    {
                        // Валидация: проверяем состояние перед переподключением
                        bool wasCapturing = isCapturing;
                        string currentDeviceName = cbSpeakerDevices.SelectedItem is AudioDevice currentDevice 
                            ? currentDevice.Name 
                            : "Не выбрано";
                        
                        LogMessage($"📊 Состояние до переподключения: запись={wasCapturing}, устройство={currentDeviceName}");
                        
                        StopRecording(); // Остановить текущую запись
                        RefreshAudioDevices(); // Обновить список устройств
                        
                        // Автоматически переподключиться к лучшему доступному устройству
                        if (availableSpeakerDevices.Count > 0)
                        {
                            var bestDevice = availableSpeakerDevices.First();
                            SetSpeakerDevice(bestDevice);
                            LogMessage($"🔄 Автоматически переподключен к: {bestDevice.FriendlyName}");
                            
                            // Валидация: если запись была активна, автоматически возобновляем
                            if (wasCapturing)
                            {
                                LogMessage("🎤 Возобновляем запись после переподключения устройства");
                                Task.Delay(500).ContinueWith(_ => Invoke(() => StartAudioCapture()));
                            }
                        }
                        else
                        {
                            LogMessage("⚠️ Нет доступных аудиоустройств после изменения!");
                        }
                    });
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Ошибка автоматического переподключения: {ex.Message}");
                }
            });
        }
        
        private void CleanupDeviceNotifications()
        {
            try
            {
                if (deviceEnumerator != null && notificationClient != null)
                {
                    deviceEnumerator.UnregisterEndpointNotificationCallback(notificationClient);
                }
                
                deviceEnumerator?.Dispose();
                notificationClient = null;
                deviceEnumerator = null;
            }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Ошибка очистки мониторинга устройств: {ex.Message}");
            }
        }
        
        // 🚀 ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ для device notifications
        private void StopRecording()
        {
            // Остановка аудио записи
            try { wasapiCapture?.StopRecording(); } catch { }
            try { waveInCapture?.StopRecording(); } catch { }
            LogMessage("🛑 Запись остановлена для переподключения устройства");
        }
        
        private void RefreshAudioDevices()
        {
            // Обновление списков аудиоустройств
            try
            {
                LogMessage("🔄 Обновление списка аудиоустройств...");
                // Здесь можно добавить логику обновления устройств
                // Пока просто логируем
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обновления устройств: {ex.Message}");
            }
        }
        
        private List<MMDevice> availableSpeakerDevices = new List<MMDevice>();
        
        private void SetSpeakerDevice(MMDevice device)
        {
            // Установка нового аудиоустройства
            try
            {
                LogMessage($"🔄 Переключение на устройство: {device.FriendlyName}");
                // Здесь можно добавить логику установки устройства
                // Пока просто логируем
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка установки устройства: {ex.Message}");
            }
        }

        #endregion
    }
    
    // 🚀 КЛАСС для уведомлений об изменении аудиоустройств  
    public class AudioDeviceNotificationClient : IMMNotificationClient
    {
        private readonly Form1 form;
        
        public AudioDeviceNotificationClient(Form1 form)
        {
            this.form = form;
        }
        
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            try
            {
                // Устройство по умолчанию изменилось
                form?.Invoke(new Action(() => {
                    form.LogMessage($"🔄 Устройство по умолчанию изменено: {flow} роль {role}, ID: {defaultDeviceId ?? "null"}");
                    form.OnDeviceChanged();
                }));
            }
            catch (Exception ex)
            {
                // Безопасное логирование ошибок без вызова UI из другого потока
                System.Diagnostics.Debug.WriteLine($"OnDefaultDeviceChanged error: {ex.Message}");
            }
        }
        
        public void OnDeviceAdded(string pwstrDeviceId)
        {
            try
            {
                // Добавлено новое устройство
                form?.Invoke(new Action(() => {
                    form.LogMessage($"➕ Устройство добавлено: ID {pwstrDeviceId ?? "null"}");
                    form.OnDeviceChanged();
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnDeviceAdded error: {ex.Message}");
            }
        }
        
        public void OnDeviceRemoved(string pwstrDeviceId)
        {
            try
            {
                // Устройство удалено
                form?.Invoke(new Action(() => {
                    form.LogMessage($"➖ Устройство удалено: ID {pwstrDeviceId ?? "null"}");
                    form.OnDeviceChanged();
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnDeviceRemoved error: {ex.Message}");
            }
        }
        
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            try
            {
                // Состояние устройства изменилось
                form?.Invoke(new Action(() => {
                    form.LogMessage($"🔧 Состояние устройства изменено: ID {deviceId ?? "null"} → {newState}");
                    form.OnDeviceChanged();
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnDeviceStateChanged error: {ex.Message}");
            }
        }
        
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Свойства устройства изменились (можно игнорировать)
        }
    }
}
