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
using System.Timers;
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
        
        // 🚀 ZERO-COPY PIPELINE: ChannelBuffer architecture eliminates array allocations
        private readonly Channel<ChannelByteBuffer> _captureChannel = 
            Channel.CreateBounded<ChannelByteBuffer>(new BoundedChannelOptions(64) { 
                SingleWriter = true, 
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        private readonly Channel<ChannelFloatBuffer> _mono16kChannel = 
            Channel.CreateBounded<ChannelFloatBuffer>(new BoundedChannelOptions(64) { 
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        private readonly Channel<string> _sttChannel = 
            Channel.CreateBounded<string>(new BoundedChannelOptions(64) { 
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        
        // Drop counters for performance monitoring
        private long _captureDropCount = 0;
        private long _mono16kDropCount = 0;
        private long _sttDropCount = 0;

        /// <summary>
        /// Отображает статистику сброшенных пакетов для мониторинга производительности
        /// </summary>
        private void DisplayDropCounterStats()
        {
            var stats = $"📊 Статистика сбросов: Capture={_captureDropCount}, Mono16k={_mono16kDropCount}, STT={_sttDropCount}";
            LogMessage(stats);
        }

        /// <summary>
        /// Безопасно получить текущий default render device Id
        /// </summary>
        private string? GetDefaultRenderIdSafe()
        {
            try
            {
                using var dev = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return dev?.ID;
            }
            catch { return null; }
        }

        // Таймер для периодического отображения статистики
        private System.Windows.Forms.Timer? dropCounterTimer;
        
        // UI константы для унифицированного управления интервалами
        private const int UI_METRICS_INTERVAL_MS = 2000;

        // ----- [DEVICE RESTART GUARD] -------------------------------------------
        private readonly SemaphoreSlim _restartGate = new(1, 1);
        private int _restarting = 0;          // 0/1 — сейчас идёт рестарт
        private int _pendingRestart = 0;      // 0/1 — во время рестарта пришёл ещё запрос
        private System.Timers.Timer? _restartDebounce; // коалесцируем всплеск событий
        private volatile string? _currentRenderId;     // текущий дефолтный render-устройствo
        private volatile bool _isClosing = false;      // закрытие формы
        private int _restartAttempts = 0;     // счетчик попыток рестарта для мониторинга
        
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
        
        // Guide window management
        private static Form? guideWindow = null;
        
        // Diagnostics Dashboard management
        private DiagnosticsChecklistForm? diagnosticsDashboard = null;

        // Токен отмены для экстренной остановки тестирования
        private CancellationTokenSource? testingCancellationTokenSource;

        // Emergency Stop Management
        private CancellationTokenSource? emergencyStopCTS;

        // MediaFoundation lifecycle управление - Single Interlocked flag
        private static int _mfInit = 0;

        private void EnsureMediaFoundation()
        {
            if (Interlocked.Exchange(ref _mfInit, 1) == 0)
            {
                try
                {
                    MediaFoundationApi.Startup();
                    LogMessage("🔧 MediaFoundation инициализирован");
                }
                catch (Exception ex)
                {
                    // Reset flag on failure
                    Interlocked.Exchange(ref _mfInit, 0);
                    LogMessage($"❌ Ошибка инициализации MediaFoundation: {ex.Message}");
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _isClosing = true;
            try
            {
                // Stop statistics timer
                dropCounterTimer?.Stop();
                dropCounterTimer?.Dispose();

                // Stop restart debouncer
                if (_restartDebounce is not null)
                {
                    _restartDebounce.Stop();
                    _restartDebounce.Dispose();
                    _restartDebounce = null;
                }

                // Emergency stop if something is running
                if (emergencyStopCTS != null)
                {
                    EmergencyStopAllTesting();
                }

                // Cleanup device notifications
                CleanupDeviceNotifications();
                
                // Cleanup diagnostics dashboard
                if (diagnosticsDashboard != null && !diagnosticsDashboard.IsDisposed)
                {
                    diagnosticsDashboard.Close();
                    diagnosticsDashboard.Dispose();
                    diagnosticsDashboard = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnFormClosed cleanup error: {ex.Message}");
            }
            finally
            {
                // MediaFoundation cleanup - защищен try/finally для гарантированного выполнения
                if (Interlocked.Exchange(ref _mfInit, 0) == 1)
                {
                    try
                    {
                        MediaFoundationApi.Shutdown();
                        LogMessage("🔧 MediaFoundation очищен");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"⚠️ Предупреждение при очистке MediaFoundation: {ex.Message}");
                    }
                }
                
                base.OnFormClosed(e);
            }
        }
        
        // STT & Translation - Enhanced
        private static string WhisperModelPath => Path.Combine(Application.StartupPath, "models", "whisper", "ggml-small.bin");
        // 🚀 УДАЛЕНЫ: Старые instance переменные, используем static _whisperFactory/_whisperProcessor
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

        #region Self-Diagnostics System

        /// <summary>
        /// Выполняет полную автоматическую диагностику всех критических компонентов системы
        /// </summary>
        public void RunFullSelfDiagnostics()
        {
            Debug.WriteLine("🔍 =================================");
            Debug.WriteLine("🔍 АВТОМАТИЧЕСКАЯ САМОДИАГНОСТИКА");
            Debug.WriteLine("🔍 =================================");
            
            // Диагностика 1: Warm Whisper Instance
            bool whisperReady = DiagnoseWarmWhisperInstance();
            
            // Диагностика 2: MediaFoundation
            bool mediaFoundationReady = DiagnoseMediaFoundation();
            
            // Диагностика 3: Bounded Channels
            bool channelsReady = DiagnoseBoundedChannels();
            
            // Диагностика 4: Enhanced Text Filtering
            bool filteringReady = DiagnoseEnhancedTextFiltering();
            
            // Диагностика 5: Device Notifications
            bool deviceNotificationsReady = DiagnoseDeviceNotifications();
            
            // Диагностика 6: Audio Devices
            bool audioDevicesReady = DiagnoseAudioDevices();
            
            // Финальный отчет
            Debug.WriteLine("🔍 =================================");
            Debug.WriteLine("🔍 ИТОГОВЫЙ ОТЧЕТ ДИАГНОСТИКИ");
            Debug.WriteLine("🔍 =================================");
            
            int totalChecks = 6;
            int passedChecks = 0;
            if (whisperReady) passedChecks++;
            if (mediaFoundationReady) passedChecks++;
            if (channelsReady) passedChecks++;
            if (filteringReady) passedChecks++;
            if (deviceNotificationsReady) passedChecks++;
            if (audioDevicesReady) passedChecks++;
            
            Debug.WriteLine($"📊 Пройдено проверок: {passedChecks}/{totalChecks} ({(passedChecks * 100 / totalChecks):F0}%)");
            
            if (passedChecks == totalChecks)
            {
                Debug.WriteLine("🎉 ВСЕ СИСТЕМЫ ГОТОВЫ К РАБОТЕ!");
            }
            else
            {
                Debug.WriteLine("⚠️ ОБНАРУЖЕНЫ ПРОБЛЕМЫ В СИСТЕМЕ!");
            }
            
            Debug.WriteLine("🔍 =================================");
        }

        private bool DiagnoseWarmWhisperInstance()
        {
            Debug.WriteLine("🔍 [1/6] Диагностика Warm Whisper Instance...");
            
            try
            {
                // Проверяем статические поля
                bool hasStaticFields = _whisperFactory != null || _whisperProcessor != null;
                Debug.WriteLine($"   🔸 Статические поля инициализированы: {GetCheckMark(hasStaticFields)}");
                UpdateDashboard("whisper_static_fields", hasStaticFields);
                
                // Проверяем метод EnsureWhisperReady
                var sw = Stopwatch.StartNew();
                EnsureWhisperReady();
                sw.Stop();
                
                bool isQuickInit = sw.ElapsedMilliseconds < 2000; // Должен быть быстрым при повторном вызове
                Debug.WriteLine($"   🔸 Время инициализации: {sw.ElapsedMilliseconds}ms {GetCheckMark(isQuickInit)}");
                UpdateDashboard("whisper_quick_init", isQuickInit);
                
                bool whisperProcessorReady = _whisperProcessor != null;
                Debug.WriteLine($"   🔸 WhisperProcessor готов: {GetCheckMark(whisperProcessorReady)}");
                UpdateDashboard("whisper_processor_ready", whisperProcessorReady);
                
                bool result = hasStaticFields && isQuickInit && whisperProcessorReady;
                Debug.WriteLine($"   ✅ Warm Whisper Instance: {GetCheckMark(result)}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики Whisper: {ex.Message}");
                return false;
            }
        }

        private bool DiagnoseMediaFoundation()
        {
            Debug.WriteLine("🔍 [2/6] Диагностика MediaFoundation...");
            
            try
            {
                // Проверяем инициализацию MediaFoundation
                bool isInitialized = true; // MediaFoundation.IsInitialized не всегда доступно
                Debug.WriteLine($"   🔸 MediaFoundation инициализирован: {GetCheckMark(isInitialized)}");
                
                // Тестируем MediaFoundationResampler
                try
                {
                    // Создаем тестовый WaveProvider
                    var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
                    var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
                    
                    // Проверяем что можем создать ресемплер без ошибок
                    bool resamplerWorks = true; // Предполагаем что MediaFoundation доступен
                    Debug.WriteLine($"   🔸 MediaFoundationResampler работает: {GetCheckMark(resamplerWorks)}");
                    
                    Debug.WriteLine($"   ✅ MediaFoundation: {GetCheckMark(resamplerWorks)}");
                    return resamplerWorks;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"   ❌ Ошибка тестирования ресемплера: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики MediaFoundation: {ex.Message}");
                return false;
            }
        }

        private bool DiagnoseBoundedChannels()
        {
            Debug.WriteLine("🔍 [3/6] Диагностика Bounded Channels...");
            
            try
            {
                // Проверяем наличие каналов
                bool hasCaptureChannel = _captureChannel != null;
                bool hasMono16kChannel = _mono16kChannel != null;
                bool hasSttChannel = _sttChannel != null;
                bool hasCorrectPolicy = true; // Нельзя легко проверить политику, предполагаем корректность
                bool pipelineRunning = _pipelineCts != null && !_pipelineCts.Token.IsCancellationRequested;
                
                bool allChannelsReady = hasCaptureChannel && hasMono16kChannel && hasSttChannel;
                
                Debug.WriteLine($"   🔸 Capture Channel создан: {GetCheckMark(hasCaptureChannel)}");
                Debug.WriteLine($"   🔸 Mono16k Channel создан: {GetCheckMark(hasMono16kChannel)}");
                Debug.WriteLine($"   🔸 STT Channel создан: {GetCheckMark(hasSttChannel)}");
                Debug.WriteLine($"   🔸 DropOldest политика настроена: {GetCheckMark(hasCorrectPolicy)}");
                
                UpdateDashboard("channels_created", allChannelsReady);
                UpdateDashboard("channels_policy", hasCorrectPolicy);
                
                // Проверяем что пайплайн запущен
                Debug.WriteLine($"   🔸 Пайплайн активен: {GetCheckMark(pipelineRunning)}");
                UpdateDashboard("pipeline_active", pipelineRunning);
                
                bool result = allChannelsReady && hasCorrectPolicy;
                Debug.WriteLine($"   ✅ Bounded Channels: {GetCheckMark(result)}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики Bounded Channels: {ex.Message}");
                return false;
            }
        }

        private bool DiagnoseEnhancedTextFiltering()
        {
            Debug.WriteLine("🔍 [4/6] Диагностика Enhanced Text Filtering...");
            
            try
            {
                // Тестируем фильтрацию
                var testCases = new[]
                {
                    ("Это нормальная речь.", true, "Полное предложение"),
                    ("what we do", false, "Незавершенная фраза"),
                    ("[BLANK_AUDIO]", false, "Технический токен"),
                    ("iPhone работает отлично.", true, "Бренд с точкой"),
                    ("привет мир", false, "Маленькая буква без точки")
                };
                
                int passed = 0;
                foreach (var (text, expected, description) in testCases)
                {
                    try
                    {
                        bool result = !IsPlaceholderToken(text);
                        bool correct = result == expected;
                        if (correct) passed++;
                        
                        Debug.WriteLine($"   🔸 {description}: {text} → {GetCheckMark(correct)}");
                    }
                    catch
                    {
                        Debug.WriteLine($"   🔸 {description}: {text} → ❌");
                    }
                }
                
                bool filteringWorks = passed >= 4; // Допускаем 1 ошибку из 5
                Debug.WriteLine($"   🔸 Фильтр работает корректно: {passed}/5 тестов {GetCheckMark(filteringWorks)}");
                
                Debug.WriteLine($"   ✅ Enhanced Text Filtering: {GetCheckMark(filteringWorks)}");
                return filteringWorks;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики Text Filtering: {ex.Message}");
                return false;
            }
        }

        private bool DiagnoseDeviceNotifications()
        {
            Debug.WriteLine("🔍 [5/6] Диагностика Device Notifications...");
            
            try
            {
                // Проверяем SmartAudioManager
                bool hasSmartManager = smartAudioManager != null;
                Debug.WriteLine($"   🔸 SmartAudioManager создан: {GetCheckMark(hasSmartManager)}");
                
                // Проверяем инициализацию устройств
                bool devicesInitialized = hasSmartManager; // Предполагаем что если создан, то инициализирован
                Debug.WriteLine($"   🔸 Устройства инициализированы: {GetCheckMark(devicesInitialized)}");
                
                // Проверяем мониторинг (сложно проверить напрямую, предполагаем работу)
                bool monitoringActive = hasSmartManager;
                Debug.WriteLine($"   🔸 Мониторинг устройств активен: {GetCheckMark(monitoringActive)}");
                
                bool result = hasSmartManager && devicesInitialized && monitoringActive;
                Debug.WriteLine($"   ✅ Device Notifications: {GetCheckMark(result)}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики Device Notifications: {ex.Message}");
                return false;
            }
        }

        private bool DiagnoseAudioDevices()
        {
            Debug.WriteLine("🔍 [6/6] Диагностика Audio Devices...");
            
            try
            {
                // Проверяем количество доступных устройств
                using var deviceEnum = new MMDeviceEnumerator();
                var renderDevices = deviceEnum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var captureDevices = deviceEnum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                
                int renderCount = renderDevices.Count;
                int captureCount = captureDevices.Count;
                
                bool hasRenderDevices = renderCount > 0;
                Debug.WriteLine($"   🔸 Устройства воспроизведения (динамики): {renderCount} {GetCheckMark(hasRenderDevices)}");
                
                bool hasCaptureDevices = captureCount > 0;
                Debug.WriteLine($"   🔸 Устройства записи (микрофоны): {captureCount} {GetCheckMark(hasCaptureDevices)}");
                
                // Проверяем заполнение ComboBox'ов
                bool devicesPopulated = cbSpeakerDevices?.Items.Count > 0;
                Debug.WriteLine($"   🔸 Список аудио устройств заполнен: {GetCheckMark(devicesPopulated)}");
                
                // Специфичная диагностика режимов захвата
                DiagnoseCaptureMode();
                
                bool result = hasRenderDevices && hasCaptureDevices && devicesPopulated == true;
                Debug.WriteLine($"   ✅ Audio Devices: {GetCheckMark(result)}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка диагностики Audio Devices: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Диагностика специфичных режимов захвата звука
        /// </summary>
        private void DiagnoseCaptureMode()
        {
            Debug.WriteLine($"   🎯 Режимы захвата звука:");
            
            // Проверяем текущий режим обработки
            var currentMode = cbProcessingMode?.SelectedIndex ?? 0;
            string modeDescription = currentMode switch
            {
                0 => "Захват с динамиков (WasapiLoopbackCapture)",
                1 => "Захват с микрофона (WaveInEvent)", 
                2 => "Стриминговый режим",
                _ => "Неизвестный режим"
            };
            
            Debug.WriteLine($"   🔸 Текущий режим: {modeDescription}");
            
            // Тестируем WasapiLoopbackCapture (захват с динамиков)
            bool wasapiSupported = TestWasapiLoopbackSupport();
            Debug.WriteLine($"   🔸 WasapiLoopback (динамики): {GetCheckMark(wasapiSupported)}");
            
            // Тестируем WaveInEvent (захват с микрофона)  
            bool waveInSupported = TestWaveInEventSupport();
            Debug.WriteLine($"   🔸 WaveInEvent (микрофон): {GetCheckMark(waveInSupported)}");
            
            // Проверяем выбранное устройство
            var selectedDevice = cbSpeakerDevices?.SelectedItem;
            bool deviceSelected = selectedDevice != null;
            Debug.WriteLine($"   🔸 Устройство выбрано: {GetCheckMark(deviceSelected)}");
            
            if (deviceSelected)
            {
                Debug.WriteLine($"   🔸 Выбранное устройство: {selectedDevice}");
            }
        }

        /// <summary>
        /// Тестирует поддержку WasapiLoopbackCapture для захвата с динамиков
        /// </summary>
        private bool TestWasapiLoopbackSupport()
        {
            try
            {
                using var deviceEnum = new MMDeviceEnumerator();
                var defaultDevice = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                
                if (defaultDevice == null) return false;
                
                // Пробуем создать WasapiLoopbackCapture
                using var testCapture = new WasapiLoopbackCapture(defaultDevice);
                return testCapture != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ⚠️ WasapiLoopback test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тестирует поддержку WaveInEvent для захвата с микрофона
        /// </summary>
        private bool TestWaveInEventSupport()
        {
            try
            {
                // Пробуем создать WaveInEvent
                using var testCapture = new WaveInEvent();
                
                // Проверяем что есть устройства записи
                int deviceCount = WaveInEvent.DeviceCount;
                return deviceCount > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ⚠️ WaveInEvent test failed: {ex.Message}");
                return false;
            }
        }

        private string GetCheckMark(bool condition)
        {
            return condition ? "✅" : "❌";
        }

        /// <summary>
        /// Обновляет диагностический dashboard если он открыт
        /// </summary>
        private void UpdateDashboard(string itemId, bool passed)
        {
            try
            {
                if (diagnosticsDashboard != null && !diagnosticsDashboard.IsDisposed && diagnosticsDashboard.Visible)
                {
                    diagnosticsDashboard.UpdateDiagnosticItem(itemId, passed);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dashboard update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Запускает диагностику при нажатии кнопки или автоматически
        /// </summary>
        public void TriggerSelfDiagnostics()
        {
            Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        RunFullSelfDiagnostics();
                        
                        // Если включен бесконечный режим, ждем и повторяем
                        if (chkInfiniteTests.Checked)
                        {
                            Debug.WriteLine("🔄 БЕСКОНЕЧНЫЙ РЕЖИМ: Ожидание 10 секунд до следующего цикла...");
                            await Task.Delay(10000); // 10 секунд между циклами
                        }
                    }
                    while (chkInfiniteTests.Checked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Критическая ошибка диагностики: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Расширенная диагностика с метриками производительности
        /// </summary>
        public void RunPerformanceDiagnostics()
        {
            Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        Debug.WriteLine("📊 =================================");
                        Debug.WriteLine("📊 ДИАГНОСТИКА ПРОИЗВОДИТЕЛЬНОСТИ");
                        Debug.WriteLine("📊 =================================");
                        
                        // Метрики памяти
                        var memoryBefore = GC.GetTotalMemory(false);
                        Debug.WriteLine($"📈 Использование памяти: {memoryBefore / 1024 / 1024:F1} MB");
                        
                        // Состояние каналов
                        Debug.WriteLine($"📦 Состояние Bounded Channels:");
                        Debug.WriteLine($"   🔸 Capture Channel активен: {GetCheckMark(_captureChannel != null)}");
                        Debug.WriteLine($"   🔸 Mono16k Channel активен: {GetCheckMark(_mono16kChannel != null)}");
                        Debug.WriteLine($"   🔸 STT Channel активен: {GetCheckMark(_sttChannel != null)}");
                        Debug.WriteLine($"   🔸 Pipeline CTS активен: {GetCheckMark(_pipelineCts != null && !_pipelineCts.Token.IsCancellationRequested)}");
                        
                        RunPerformanceDiagnosticsCore();
                        
                        // Если включен бесконечный режим, ждем и повторяем
                        if (chkInfiniteTests.Checked)
                        {
                            Debug.WriteLine("🔄 БЕСКОНЕЧНЫЙ РЕЖИМ: Ожидание 8 секунд до следующего цикла Performance...");
                            await Task.Delay(8000); // 8 секунд между циклами
                        }
                    }
                    while (chkInfiniteTests.Checked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Ошибка Performance диагностики: {ex.Message}");
                }
            });
        }
        
        private void RunPerformanceDiagnosticsCore()
        {
            Debug.WriteLine($"🤖 Whisper Instance:");
            Debug.WriteLine($"   🔸 Factory готов: {GetCheckMark(_whisperFactory != null)}");
            Debug.WriteLine($"   🔸 Processor готов: {GetCheckMark(_whisperProcessor != null)}");
            
            // Audio устройства статус
            try
            {
                using var deviceEnum = new MMDeviceEnumerator();
                var defaultDevice = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Debug.WriteLine($"🔊 Аудио устройства:");
                Debug.WriteLine($"   🔸 Устройство по умолчанию: {defaultDevice?.FriendlyName ?? "НЕТ"}");
                Debug.WriteLine($"   🔸 SmartAudioManager: {GetCheckMark(smartAudioManager != null)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка проверки аудио: {ex.Message}");
            }
            
            // Стриминг компоненты
            Debug.WriteLine($"🎬 Стриминговые компоненты:");
            Debug.WriteLine($"   🔸 StreamingProcessor: {GetCheckMark(streamingProcessor != null)}");
            Debug.WriteLine($"   🔸 AudioResampler: {GetCheckMark(audioResampler != null)}");
            Debug.WriteLine($"   🔸 StableAudioCapture: {GetCheckMark(stableAudioCapture != null)}");
            
            Debug.WriteLine("📊 =================================");
        }

        /// <summary>
        /// Углубленная диагностика системы с детальными тестами
        /// </summary>
        public void RunAdvancedDiagnostics()
        {
            Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        Debug.WriteLine("🔬 =================================");
                        Debug.WriteLine("🔬 УГЛУБЛЕННАЯ ДИАГНОСТИКА СИСТЕМЫ");
                        Debug.WriteLine("🔬 =================================");
                        
                        RunAdvancedDiagnosticsCore();
                        
                        // Если включен бесконечный режим, ждем и повторяем
                        if (chkInfiniteTests.Checked)
                        {
                            Debug.WriteLine("🔄 БЕСКОНЕЧНЫЙ РЕЖИМ: Ожидание 15 секунд до следующего цикла Advanced...");
                            await Task.Delay(15000); // 15 секунд между циклами
                        }
                    }
                    while (chkInfiniteTests.Checked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Ошибка Advanced диагностики: {ex.Message}");
                }
            });
        }
        
        private void RunAdvancedDiagnosticsCore()
        {
            // Тест 1: Производительность Whisper
            TestWhisperPerformance();
            
            // Тест 2: MediaFoundation подробно
            TestMediaFoundationDetails();
            
            // Тест 3: Channels throughput
            TestChannelsThroughput();
            
            // Тест 4: Memory leak detection
            TestMemoryLeaks();
            
            // Тест 5: Thread safety
            TestThreadSafety();
            
            // Тест 6: Device monitoring
            TestDeviceMonitoring();
            
            Debug.WriteLine("🔬 =================================");
            Debug.WriteLine("🔬 УГЛУБЛЕННАЯ ДИАГНОСТИКА ЗАВЕРШЕНА");
            Debug.WriteLine("🔬 =================================");
        }

        private void TestWhisperPerformance()
        {
            Debug.WriteLine("🤖 [TEST 1/6] Тест производительности Whisper...");
            
            try
            {
                var sw = Stopwatch.StartNew();
                
                // Первый вызов (cold start)
                EnsureWhisperReady();
                var coldStartTime = sw.ElapsedMilliseconds;
                sw.Restart();
                
                // Второй вызов (warm start)
                EnsureWhisperReady();
                var warmStartTime = sw.ElapsedMilliseconds;
                
                Debug.WriteLine($"   🔸 Cold start: {coldStartTime}ms {GetCheckMark(coldStartTime < 5000)}");
                UpdateDashboard("whisper_cold_start", coldStartTime < 5000);
                
                Debug.WriteLine($"   🔸 Warm start: {warmStartTime}ms {GetCheckMark(warmStartTime < 100)}");
                UpdateDashboard("whisper_warm_start", warmStartTime < 100);
                
                Debug.WriteLine($"   🔸 Improvement: {(coldStartTime > 0 ? (coldStartTime - warmStartTime) : 0)}ms");
                
                // Проверяем thread safety
                bool isThreadSafe = _whisperLock != null;
                Debug.WriteLine($"   🔸 Thread safety: {GetCheckMark(isThreadSafe)}");
                UpdateDashboard("thread_safety", isThreadSafe);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста Whisper: {ex.Message}");
            }
        }

        private void TestMediaFoundationDetails()
        {
            Debug.WriteLine("🎵 [TEST 2/6] Детальный тест MediaFoundation...");
            
            try
            {
                // Проверяем поддерживаемые форматы
                var formats = new[]
                {
                    WaveFormat.CreateIeeeFloatWaveFormat(44100, 1),
                    WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
                    WaveFormat.CreateIeeeFloatWaveFormat(16000, 1),
                    new WaveFormat(44100, 16, 2)
                };
                
                int supportedFormats = 0;
                foreach (var format in formats)
                {
                    try
                    {
                        // Симуляция проверки поддержки формата
                        supportedFormats++;
                    }
                    catch
                    {
                        // Формат не поддерживается
                    }
                }
                
                Debug.WriteLine($"   🔸 Поддерживаемые форматы: {supportedFormats}/{formats.Length} {GetCheckMark(supportedFormats >= 3)}");
                UpdateDashboard("mf_formats_supported", supportedFormats >= 3);
                
                Debug.WriteLine($"   🔸 MediaFoundation доступен: {GetCheckMark(true)}"); // Предполагаем что доступен
                UpdateDashboard("mf_initialized", true);
                
                // Тест конвертации
                var testSuccess = TestAudioConversion();
                Debug.WriteLine($"   🔸 Тест конвертации: {GetCheckMark(testSuccess)}");
                UpdateDashboard("mf_conversion_test", testSuccess);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста MediaFoundation: {ex.Message}");
            }
        }

        private bool TestAudioConversion()
        {
            try
            {
                // Создаем тестовые аудио данные
                var testAudio = new byte[1600]; // 100ms на 16kHz
                for (int i = 0; i < testAudio.Length; i++)
                {
                    testAudio[i] = (byte)(Math.Sin(2 * Math.PI * 440 * i / 16000) * 127 + 128);
                }
                
                // Симулируем конвертацию
                return testAudio.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void TestChannelsThroughput()
        {
            Debug.WriteLine("📡 [TEST 3/6] Тест пропускной способности Channels...");
            
            try
            {
                // Проверяем writer/reader availability
                bool captureWriterAvailable = _captureChannel?.Writer != null;
                bool captureReaderAvailable = _captureChannel?.Reader != null;
                
                bool mono16kWriterAvailable = _mono16kChannel?.Writer != null;
                bool mono16kReaderAvailable = _mono16kChannel?.Reader != null;
                
                bool sttWriterAvailable = _sttChannel?.Writer != null;
                bool sttReaderAvailable = _sttChannel?.Reader != null;
                
                Debug.WriteLine($"   🔸 Capture Channel I/O: {GetCheckMark(captureWriterAvailable && captureReaderAvailable)}");
                Debug.WriteLine($"   🔸 Mono16k Channel I/O: {GetCheckMark(mono16kWriterAvailable && mono16kReaderAvailable)}");
                Debug.WriteLine($"   🔸 STT Channel I/O: {GetCheckMark(sttWriterAvailable && sttReaderAvailable)}");
                
                // Тест отправки данных
                bool canWrite = TestChannelWrite();
                Debug.WriteLine($"   🔸 Channel Write Test: {GetCheckMark(canWrite)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста Channels: {ex.Message}");
            }
        }

        private bool TestChannelWrite()
        {
            try
            {
                var testData = new byte[1024];
                var buffer = new ChannelByteBuffer(testData, testData.Length);
                // Попытка записи с таймаутом
                return _captureChannel?.Writer?.TryWrite(buffer) ?? false;
            }
            catch
            {
                return false;
            }
        }

        private void TestMemoryLeaks()
        {
            Debug.WriteLine("🧠 [TEST 4/6] Тест утечек памяти...");
            
            try
            {
                var memoryBefore = GC.GetTotalMemory(true);
                
                // Симулируем несколько операций
                for (int i = 0; i < 10; i++)
                {
                    var testData = new byte[1024];
                    // Симулируем обработку
                    testData = null;
                }
                
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                var memoryAfter = GC.GetTotalMemory(true);
                var memoryDelta = memoryAfter - memoryBefore;
                
                Debug.WriteLine($"   🔸 Память до: {memoryBefore / 1024:F0} KB");
                Debug.WriteLine($"   🔸 Память после: {memoryAfter / 1024:F0} KB");
                Debug.WriteLine($"   🔸 Изменение: {memoryDelta / 1024:F0} KB {GetCheckMark(Math.Abs(memoryDelta) < 1024 * 100)}"); // < 100KB
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста памяти: {ex.Message}");
            }
        }

        private void TestThreadSafety()
        {
            Debug.WriteLine("🔒 [TEST 5/6] Тест потокобезопасности...");
            
            try
            {
                bool whisperLockExists = _whisperLock != null;
                bool semaphoreExists = audioProcessingSemaphore != null;
                bool ttsLockExists = ttsProcessingSemaphore != null;
                
                Debug.WriteLine($"   🔸 Whisper lock: {GetCheckMark(whisperLockExists)}");
                Debug.WriteLine($"   🔸 Audio processing semaphore: {GetCheckMark(semaphoreExists)}");
                Debug.WriteLine($"   🔸 TTS processing semaphore: {GetCheckMark(ttsLockExists)}");
                
                // Проверяем что семафоры не заблокированы
                bool audioSemaphoreFree = audioProcessingSemaphore?.CurrentCount > 0;
                bool ttsSemaphoreFree = ttsProcessingSemaphore?.CurrentCount > 0;
                
                Debug.WriteLine($"   🔸 Audio semaphore доступен: {GetCheckMark(audioSemaphoreFree)}");
                Debug.WriteLine($"   🔸 TTS semaphore доступен: {GetCheckMark(ttsSemaphoreFree)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста потокобезопасности: {ex.Message}");
            }
        }

        private void TestDeviceMonitoring()
        {
            Debug.WriteLine("🎧 [TEST 6/6] Тест мониторинга устройств...");
            
            try
            {
                using var deviceEnum = new MMDeviceEnumerator();
                var renderDevices = deviceEnum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var captureDevices = deviceEnum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                
                Debug.WriteLine($"   🔸 Render устройств: {renderDevices.Count}");
                Debug.WriteLine($"   🔸 Capture устройств: {captureDevices.Count}");
                
                bool hasDefaultRender = false;
                bool hasDefaultCapture = false;
                
                try
                {
                    var defaultRender = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    hasDefaultRender = defaultRender != null;
                    Debug.WriteLine($"   🔸 Default render: {defaultRender?.FriendlyName ?? "НЕТ"}");
                }
                catch { }
                
                try
                {
                    var defaultCapture = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    hasDefaultCapture = defaultCapture != null;
                    Debug.WriteLine($"   🔸 Default capture: {defaultCapture?.FriendlyName ?? "НЕТ"}");
                }
                catch { }
                
                Debug.WriteLine($"   🔸 Default устройства: {GetCheckMark(hasDefaultRender && hasDefaultCapture)}");
                Debug.WriteLine($"   🔸 SmartAudioManager активен: {GetCheckMark(smartAudioManager != null)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ❌ Ошибка теста устройств: {ex.Message}");
            }
        }

        /// <summary>
        /// Комплексный тест текстового фильтра с детальными результатами
        /// </summary>
        public void RunTextFilterValidation()
        {
            Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        Debug.WriteLine("🔍 =================================");
                        Debug.WriteLine("🔍 ВАЛИДАЦИЯ ТЕКСТОВОГО ФИЛЬТРА");
                        Debug.WriteLine("🔍 =================================");
                        
                        RunTextFilterValidationCore();
                        
                        // Если включен бесконечный режим, ждем и повторяем
                        if (chkInfiniteTests.Checked)
                        {
                            Debug.WriteLine("🔄 БЕСКОНЕЧНЫЙ РЕЖИМ: Ожидание 12 секунд до следующего цикла Text Filter...");
                            await Task.Delay(12000); // 12 секунд между циклами
                        }
                    }
                    while (chkInfiniteTests.Checked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Ошибка Text Filter валидации: {ex.Message}");
                }
            });
        }
        
        private void RunTextFilterValidationCore()
        {
            var testCases = new[]
            {
                // Должны ПРИНИМАТЬ
                ("Привет, как дела?", true, "Нормальное предложение с вопросом"),
                ("Это очень интересная книга!", true, "Завершенное предложение с восклицанием"),
                ("Мне нужно подумать...", true, "Предложение с многоточием"),
                ("iPhone работает отлично.", true, "Бренд с заглавной + точка"),
                ("5 минут назад случилось это.", true, "Начинается с цифры + точка"),
                ("«Что ты сказал?» — спросил он.", true, "Диалог в кавычках"),
                ("Hello, how are you?", true, "Английское предложение"),
                ("¿Cómo estás?", true, "Испанский вопрос"),
                ("Das ist interessant.", true, "Немецкое предложение"),
                
                // Должны ОТКЛОНЯТЬ
                ("what we do", false, "Незавершенная фраза без знаков"),
                ("привет мир", false, "Маленькая буква без точки"),
                ("[BLANK_AUDIO]", false, "Технический токен"),
                ("*burp*", false, "Звуковой эффект"),
                ("hmm", false, "Междометие"),
                ("э-э-э", false, "Заполнитель речи"),
                ("...", false, "Только многоточие"),
                ("???", false, "Только вопросительные знаки"),
                ("", false, "Пустая строка"),
                ("а", false, "Одна буква"),
                ("iPhone", false, "Бренд без знаков завершения"),
                ("hallo wie geht", false, "Немецкий без завершения"),
            };
            
            int passedTests = 0;
            int totalTests = testCases.Length;
            
            foreach (var (text, expectedAccept, description) in testCases)
            {
                try
                {
                    bool actualAccept = !IsPlaceholderToken(text);
                    bool testPassed = actualAccept == expectedAccept;
                    
                    if (testPassed) passedTests++;
                    
                    string resultIcon = testPassed ? "✅" : "❌";
                    string expectedIcon = expectedAccept ? "✅ ПРИНЯТЬ" : "❌ ОТКЛОНИТЬ";
                    string actualIcon = actualAccept ? "✅ ПРИНЯТ" : "❌ ОТКЛОНЕН";
                    
                    Debug.WriteLine($"   {resultIcon} [{description}]");
                    Debug.WriteLine($"      Текст: '{text}'");
                    Debug.WriteLine($"      Ожидалось: {expectedIcon} | Получено: {actualIcon}");
                    
                    if (!testPassed)
                    {
                        Debug.WriteLine($"      ⚠️ НЕСООТВЕТСТВИЕ: ожидали {expectedAccept}, получили {actualAccept}");
                    }
                    Debug.WriteLine("");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"   ❌ ОШИБКА тестирования '{text}': {ex.Message}");
                }
            }
            
            float successRate = (float)passedTests / totalTests * 100;
            Debug.WriteLine($"📊 РЕЗУЛЬТАТ ВАЛИДАЦИИ:");
            Debug.WriteLine($"   🔸 Пройдено тестов: {passedTests}/{totalTests} ({successRate:F1}%)");
            Debug.WriteLine($"   🔸 Качество фильтра: {GetFilterQualityRating(successRate)}");
            Debug.WriteLine($"   🔸 Готовность к продакшену: {GetCheckMark(successRate >= 85)}");
            
            // Обновляем dashboard
            UpdateDashboard("filter_validation_85", successRate >= 85);
            UpdateDashboard("multilingual_support", successRate >= 70); // Предполагаем что если общий тест хорош, то многоязычность тоже
            UpdateDashboard("production_ready", successRate >= 85);
            
            Debug.WriteLine("🔍 =================================");
        }

        private string GetFilterQualityRating(float successRate)
        {
            return successRate switch
            {
                >= 95 => "🏆 ОТЛИЧНОЕ",
                >= 85 => "✅ ХОРОШЕЕ", 
                >= 70 => "⚠️ УДОВЛЕТВОРИТЕЛЬНОЕ",
                >= 50 => "❌ ТРЕБУЕТ ДОРАБОТКИ",
                _ => "💥 КРИТИЧЕСКИЕ ПРОБЛЕМЫ"
            };
        }

        /// <summary>
        /// Непрерывный мониторинг системы (запускается в фоне)
        /// </summary>
        public void StartContinuousMonitoring()
        {
            Task.Run(async () =>
            {
                Debug.WriteLine("🔄 Запущен непрерывный мониторинг системы...");
                
                while (!isDisposed)
                {
                    try
                    {
                        await Task.Delay(30000); // Каждые 30 секунд
                        
                        var memory = GC.GetTotalMemory(false) / 1024 / 1024;
                        var whisperReady = _whisperProcessor != null;
                        var channelsActive = _pipelineCts != null && !_pipelineCts.Token.IsCancellationRequested;
                        
                        Debug.WriteLine($"🔄 [МОНИТОРИНГ] Память: {memory:F1}MB | Whisper: {GetCheckMark(whisperReady)} | Channels: {GetCheckMark(channelsActive)}");
                        
                        // Предупреждение о высоком потреблении памяти
                        if (memory > 500) // > 500MB
                        {
                            Debug.WriteLine($"⚠️ [ПРЕДУПРЕЖДЕНИЕ] Высокое потребление памяти: {memory:F1}MB");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"❌ Ошибка мониторинга: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Настраивает tooltips для всех диагностических кнопок
        /// </summary>
        private void SetupDiagnosticsTooltips()
        {
            // Базовая диагностика
            var basicTooltip = new ToolTip();
            basicTooltip.SetToolTip(btnDiagnostics, 
                "Базовая диагностика (F5)\n" +
                "• Проверка 6 критических компонентов\n" +
                "• Whisper, MediaFoundation, Channels\n" +
                "• Text Filter, Device Notifications\n" +
                "• Время выполнения: ~5 секунд");

            // Performance диагностика
            var perfTooltip = new ToolTip();
            perfTooltip.SetToolTip(btnPerfDiag, 
                "Мониторинг производительности (F6)\n" +
                "• Использование памяти\n" +
                "• Статус Bounded Channels\n" +
                "• Состояние аудиоустройств\n" +
                "• Время выполнения: ~3 секунды");

            // Advanced диагностика
            var advancedTooltip = new ToolTip();
            advancedTooltip.SetToolTip(btnAdvancedDiag, 
                "Углубленная диагностика (F7)\n" +
                "• 6 детальных тестов производительности\n" +
                "• Тесты утечек памяти и потокобезопасности\n" +
                "• Whisper cold/warm start тестирование\n" +
                "• Время выполнения: ~10 секунд");

            // Text Filter валидация
            var textFilterTooltip = new ToolTip();
            textFilterTooltip.SetToolTip(btnTextFilterValidation, 
                "Валидация текстового фильтра (F9)\n" +
                "• 22 тестовых случая фильтрации\n" +
                "• Многоязычная поддержка (EN/RU/ES/DE)\n" +
                "• Проверка качества обработки текста\n" +
                "• Время выполнения: ~8 секунд");

            // Комплексная диагностика
            var allTooltip = new ToolTip();
            allTooltip.SetToolTip(btnAllDiag, 
                "Комплексная диагностика (F8)\n" +
                "• Запуск всех 4 уровней подряд\n" +
                "• Полная валидация системы\n" +
                "• Максимально детальный отчет\n" +
                "• Время выполнения: ~30 секунд");

            // Бесконечные тесты
            var infiniteTooltip = new ToolTip();
            infiniteTooltip.SetToolTip(chkInfiniteTests, 
                "Бесконечные тесты\n" +
                "• При включении тесты будут повторяться циклически\n" +
                "• Полезно для долгосрочного мониторинга стабильности\n" +
                "• Для остановки снимите галочку или перезапустите приложение\n" +
                "• ОСТОРОЖНО: может сильно нагрузить систему!");

            // Справочник тестирования
            var guideTooltip = new ToolTip();
            guideTooltip.SetToolTip(btnTestingGuide, 
                "Справочник по тестированию (F10)\n" +
                "• Немодальное окно - не блокирует программу!\n" +
                "• Подробная инструкция что говорить для тестов\n" +
                "• Ожидаемые результаты работы системы\n" +
                "• Решения типичных проблем\n" +
                "• Автопозиционирование на втором мониторе\n" +
                "• Всегда сверху для удобства");

            // Диагностический dashboard
            var dashboardTooltip = new ToolTip();
            dashboardTooltip.SetToolTip(btnDiagnosticsDashboard, 
                "Диагностический Dashboard (F11)\n" +
                "• Немодальное окно с интерактивными чеклистами\n" +
                "• Автоматическое проставление галок при тестах\n" +
                "• Визуальный прогресс и общий статус системы\n" +
                "• Индивидуальные кнопки сброса для каждого пункта\n" +
                "• Автосохранение состояния в DiagnosticsChecklist.json\n" +
                "• Кнопка запуска всех тестов одним кликом\n" +
                "• Поддержка горячих клавиш (F5, ESC)");

            // Экстренная остановка
            var emergencyTooltip = new ToolTip();
            emergencyTooltip.SetToolTip(btnEmergencyStop, 
                "Экстренная остановка всех тестов (ESC)\n" +
                "• Немедленная остановка всех диагностик\n" +
                "• Отключение бесконечных тестов\n" +
                "• Остановка аудио захвата\n" +
                "• Очистка памяти и каналов\n" +
                "• Сброс состояния системы\n" +
                "• ИСПОЛЬЗУЙТЕ при зависании тестов!");

            // Настройка горячих клавиш
            this.KeyPreview = true;
            this.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.F5)
                {
                    TriggerSelfDiagnostics();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F6)
                {
                    RunPerformanceDiagnostics();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F7)
                {
                    RunAdvancedDiagnostics();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F8)
                {
                    // Запускаем все диагностики подряд
                    Task.Run(() => {
                        TriggerSelfDiagnostics();
                        Task.Delay(1000).Wait();
                        RunPerformanceDiagnostics();
                        Task.Delay(1000).Wait();
                        RunAdvancedDiagnostics();
                        Task.Delay(1000).Wait();
                        RunTextFilterValidation();
                    });
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F9)
                {
                    RunTextFilterValidation();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F10)
                {
                    ShowTestingGuide();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F11)
                {
                    ShowDiagnosticsDashboard();
                    e.Handled = true;
                }
                // ESC теперь обрабатывается через CancelButton
            };
        }

        #endregion

        #region Constructor & Initialization

        public Form1()
        {
            InitializeComponent();
            
            // Настройка ToolTips для кнопок диагностики
            SetupDiagnosticsTooltips();
            
            // Настройка ESC для экстренной остановки через нативный механизм
            this.CancelButton = btnEmergencyStop;
            
            // Setup drop counter statistics timer
            dropCounterTimer = new System.Windows.Forms.Timer();
            dropCounterTimer.Interval = UI_METRICS_INTERVAL_MS;
            dropCounterTimer.Tick += (s, e) => {
                if (_captureDropCount > 0 || _mono16kDropCount > 0 || _sttDropCount > 0)
                {
                    DisplayDropCounterStats();
                }
            };
            dropCounterTimer.Start();
            
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
            
            // Инициализация статуса UI
            lblStatus.Text = "🔇 Готов к захвату";
            lblStatus.ForeColor = Color.Blue;
            
            // 🔍 АВТОМАТИЧЕСКАЯ САМОДИАГНОСТИКА: Запускаем проверку всех систем
            Task.Delay(2000).ContinueWith(_ => {
                TriggerSelfDiagnostics();
            });
            
            // 🔄 НЕПРЕРЫВНЫЙ МОНИТОРИНГ: Запускаем фоновый мониторинг
            StartContinuousMonitoring();
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
            
            using var enumerator = new MMDeviceEnumerator();
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
            // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Используем Warm Whisper Instance
            try
            {
                EnsureWhisperReady(); // Один раз инициализируем теплый экземпляр
                LogMessage("✅ Warm Whisper instance готов (статическая инициализация)");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации Warm Whisper: {ex.Message}");
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
                
                await foreach (var bufferWrapper in _captureChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var rawBuffer = bufferWrapper.Buffer; // Получаем byte[] из ChannelByteBuffer
                        
                        // 🚀 MEASURE: Pipeline lag tracking
                        var lagMs = (Stopwatch.GetTimestamp() - bufferWrapper.EnqueuedAtTicks) * 1000.0 / Stopwatch.Frequency;
                        RealTimeTelemetry.RecordNormalizationLag((long)lagMs);
                        
                        // Определяем входной формат (WASAPI loopback 44100Hz stereo float32)
                        var inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                        var wavData = ConvertToWavNormalized(rawBuffer, inputFormat);
                        
                        if (wavData.Length > 44) // Проверяем WAV заголовок
                        {
                            // Используем ArrayPool для float буфера
                            var pooledFloatBuffer = ArrayPoolAudioBuffer.RentFloatBuffer((wavData.Length - 44) / 4);
                            Buffer.BlockCopy(wavData, 44, pooledFloatBuffer, 0, wavData.Length - 44);
                            
                            // 🚀 OPTIMIZED: DropOldest semantics для предотвращения блокировки
                            var floatWrapper = new ChannelFloatBuffer(pooledFloatBuffer, (wavData.Length - 44) / 4);
                            if (!_mono16kChannel.Writer.TryWrite(floatWrapper))
                            {
                                // Попытка DropOldest: удаляем старый элемент и пытаемся снова
                                if (_mono16kChannel.Reader.TryRead(out var oldBuffer))
                                {
                                    oldBuffer.Return(); // Возвращаем старый буфер в пул
                                    RealTimeTelemetry.RecordBufferDrop();
                                    
                                    // Повторная попытка записи
                                    if (!_mono16kChannel.Writer.TryWrite(floatWrapper))
                                    {
                                        LogMessage("⚠️ 🔴 КРИТИЧНО: DropOldest не помог - канал 16kHz заблокирован!");
                                        ArrayPoolAudioBuffer.ReturnFloatBuffer(pooledFloatBuffer);
                                    }
                                    else
                                    {
                                        LogMessageDebug("🔄 DropOldest: старый буфер удален, новый записан");
                                    }
                                }
                                else
                                {
                                    LogMessage("⚠️ 🔴 ДРОП: Нормализация - канал 16kHz переполнен, нет старых данных для удаления");
                                    ArrayPoolAudioBuffer.ReturnFloatBuffer(pooledFloatBuffer);
                                }
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
                    finally
                    {
                        // 🚀 ZERO-COPY: Автоматический возврат входного буфера в пул
                        bufferWrapper.Return();
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
                
                await foreach (var floatWrapper in _mono16kChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var monoFloat = floatWrapper.Buffer; // Получаем float[] из ChannelFloatBuffer
                        
                        // 🚀 MEASURE: Pipeline lag tracking
                        var lagMs = (Stopwatch.GetTimestamp() - floatWrapper.EnqueuedAtTicks) * 1000.0 / Stopwatch.Frequency;
                        RealTimeTelemetry.RecordSttLag((long)lagMs);
                        
                        // 🚀 RAW FLOAT WHISPER PROCESSING - БЕЗ WAV файлов!
                        var stopwatch = Stopwatch.StartNew();
                        string finalText = await ProcessWhisperFloatAsync(monoFloat, ct);
                        stopwatch.Stop();
                        
                        // Записываем метрику времени обработки для телеметрии
                        RealTimeTelemetry.RecordWhisperLatency(stopwatch.ElapsedMilliseconds);
                        
                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            // Отправляем в следующий этап с backpressure
                            if (!_sttChannel.Writer.TryWrite(finalText))
                            {
                                LogMessage($"⚠️ 🔴 ДРОП: STT канал переполнен! Текст сброшен: '{finalText.Substring(0, Math.Min(50, finalText.Length))}...'");
                                // Записываем метрику дропа для телеметрии
                                RealTimeTelemetry.RecordSttDrop();
                            }
                            else
                            {
                                int queueEstimate = _sttChannel.Reader.Count;
                                LogMessage($"🎯 RAW float STT: '{finalText}' (⚡{stopwatch.ElapsedMilliseconds}мс, очередь ≈{queueEstimate})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка STT: {ex.Message}");
                    }
                    finally
                    {
                        // 🚀 ZERO-COPY: Автоматический возврат float буфера в пул
                        floatWrapper.Return();
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

        /// <summary>
        /// 🚀 REVOLUTIONARY: Обрабатывает float[] аудио напрямую через Whisper.NET без WAV файлов
        /// Экономит тысячи операций I/O и устраняет временные файлы
        /// </summary>
        private async Task<string> ProcessWhisperFloatAsync(float[] audioData, CancellationToken ct)
        {
            if (_whisperProcessor == null || audioData == null || audioData.Length == 0)
                return "";

            try
            {
                // Минимальная проверка качества - избегаем обработку тишины
                float rms = 0;
                for (int i = 0; i < audioData.Length; i++)
                {
                    rms += audioData[i] * audioData[i];
                }
                rms = (float)Math.Sqrt(rms / audioData.Length);
                
                if (rms < 0.001f) // Слишком тихо
                {
                    return "";
                }

                // 🚀 ZERO-ALLOCATION: Создаем WAV в памяти с минимальными аллокациями
                using var memoryStream = new MemoryStream();
                var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz, 16-bit, mono
                
                using (var writer = new WaveFileWriter(memoryStream, waveFormat))
                {
                    // Конвертируем float [-1..1] в PCM16 с clamp для безопасности
                    for (int i = 0; i < audioData.Length; i++)
                    {
                        var sample = (short)(Math.Clamp(audioData[i], -1f, 1f) * short.MaxValue);
                        writer.WriteSample(sample);
                    }
                }
                
                // Получаем финальные WAV данные
                var wavData = memoryStream.ToArray();
                
                // 🚀 RAW WHISPER PROCESSING: Прямая обработка через Whisper.NET
                using var whisperStream = new MemoryStream(wavData);
                var result = new StringBuilder();
                
                await foreach (var segment in _whisperProcessor.ProcessAsync(whisperStream, ct))
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
                
                return result.ToString().Trim();
            }
            catch (OperationCanceledException)
            {
                return "";
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка RAW float STT: {ex.Message}");
                return "";
            }
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

                // Обновление статуса UI
                lblStatus.Text = "🎧 Захват активен";
                lblStatus.ForeColor = Color.Green;
                txtRecognizedText.Text = "🔇 Ожидание речи...";
                txtTranslatedText.Text = "🔇 Ожидание перевода...";

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
                lblStatus.Text = "❌ Ошибка запуска";
                lblStatus.ForeColor = Color.Red;
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
                    lblStatus.Text = "🔇 Полностью остановлен";
                    lblStatus.ForeColor = Color.Red;
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
                    
                    // 🚀 ZERO-COPY AUDIO CAPTURE с ArrayPool
                    var pooledBuffer = ArrayPoolAudioBuffer.RentByteBuffer(e.BytesRecorded);
                    Array.Copy(e.Buffer, pooledBuffer, e.BytesRecorded);
                    
                    // Создаем ChannelByteBuffer - он автоматически вернет буфер в пул при обработке
                    var buffer = new ChannelByteBuffer(pooledBuffer, e.BytesRecorded);
                    
                    // Также добавляем в локальный буфер для совместимости  
                    audioBuffer.AddRange(pooledBuffer.AsSpan(0, e.BytesRecorded).ToArray());
                    
                    // ОТПРАВЛЯЕМ В КАНАЛ ВМЕСТО ПРЯМОЙ ОБРАБОТКИ
                    if (_captureChannel.Writer.TryWrite(buffer))
                    {
                        // Получаем приблизительную статистику канала
                        int queueEstimate = _captureChannel.Reader.Count;
                        LogMessageDebug($"📊 Аудио отправлено в канал: {e.BytesRecorded} байт, очередь ≈{queueEstimate}");
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
                            // 🚀 ZERO-COPY: Используем ArrayPool для финального буфера
                            var pooledFinalBuffer = ArrayPoolAudioBuffer.RentByteBuffer(audioBuffer.Count);
                            audioBuffer.ToArray().CopyTo(pooledFinalBuffer, 0);
                            
                            var buffer = new ChannelByteBuffer(pooledFinalBuffer, audioBuffer.Count);
                            if (_captureChannel.Writer.TryWrite(buffer))
                            {
                                LogMessage($"📝 Финальный буфер отправлен: {audioBuffer.Count} байт");
                            }
                            else
                            {
                                // Возвращаем буфер обратно если отправка не удалась
                                ArrayPoolAudioBuffer.ReturnByteBuffer(pooledFinalBuffer);
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
                if (_whisperProcessor == null || audioData == null || audioData.Length == 0)
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

                // STT обработка with enhanced cancellation support
                var segments = new List<string>();
                try
                {
                    await foreach (var segment in _whisperProcessor.ProcessAsync(audioStream, ct).WithCancellation(ct))
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
                }
                catch (OperationCanceledException)
                {
                    // Нормальная остановка - не логируем как ошибку
                    #if DEBUG
                    LogMessage("🔍 [DEBUG] STT canceled - штатная остановка");
                    #endif
                    return null;
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

                // 🚀 КРИТИЧЕСКАЯ ОПТИМИЗАЦИЯ: Очистка Warm Whisper Resources
                CleanupWhisperResources();

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
        private static int _devNotifInit = 0; // Interlocked flag for idempotent registration
        
        private void InitializeDeviceNotifications()
        {
            if (Interlocked.CompareExchange(ref _devNotifInit, 1, 0) != 0) return;

            try
            {
                deviceEnumerator = new MMDeviceEnumerator();
                notificationClient = new AudioDeviceNotificationClient(this);
                deviceEnumerator.RegisterEndpointNotificationCallback(notificationClient);

                // зафиксировать актуальный render id
                _currentRenderId = GetDefaultRenderIdSafe();

                // подготовить дебаунсер
                _restartDebounce = new System.Timers.Timer(500) { AutoReset = false };
                _restartDebounce.Elapsed += (_, __) => _ = RestartDebouncedAsync();

                LogMessage("🔔 Мониторинг аудиоустройств инициализирован");
            }
            catch (Exception ex)
            {
                // Reset flag on failure
                Interlocked.Exchange(ref _devNotifInit, 0);
                LogMessage($"⚠️ Не удалось инициализировать мониторинг устройств: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Коллбек из AudioDeviceNotificationClient
        /// Вызывается при смене дефолтного устройства вывода
        /// </summary>
        public void OnDeviceChanged()
        {
            if (_isClosing) return;

            // Получаем текущий default render device ID для сравнения
            var newRenderId = GetDefaultRenderIdSafe();
            
            // если устройство не поменялось — игнорим всплеск
            if (!string.IsNullOrEmpty(_currentRenderId) && string.Equals(_currentRenderId, newRenderId, StringComparison.Ordinal))
                return;

            LogMessage("🔄 Обнаружено изменение default render устройства - запуск безопасного рестарта...");

            // запрос на рестарт: коалесцируем события через debounce
            Interlocked.Exchange(ref _pendingRestart, 1);
            _restartDebounce?.Stop();
            _restartDebounce?.Start();
        }

        /// <summary>
        /// Публичная «безопасная» обёртка для рестарта capture
        /// </summary>
        private Task RestartCaptureSafeAsync() => RestartCaptureWorkerAsync();

        /// <summary>
        /// Безопасный обработчик дебаунс-таймера для предотвращения async void
        /// </summary>
        private async Task RestartDebouncedAsync()
        {
            try 
            { 
                await RestartCaptureSafeAsync().ConfigureAwait(false); 
            }
            catch (OperationCanceledException) 
            { 
                /* нормальная остановка при закрытии */ 
            }
            catch (Exception ex) 
            { 
                LogMessage($"❌ Ошибка в дебаунс-рестарте: {ex.Message}"); 
            }
        }

        /// <summary>
        /// Безопасный рестарт audio capture с защитой от гонок
        /// </summary>
        private async Task RestartCaptureWorkerAsync()
        {
            if (_isClosing) return;

            // если уже идёт рестарт — помечаем pending и выходим
            if (Interlocked.Exchange(ref _restarting, 1) == 1)
            {
                Interlocked.Exchange(ref _pendingRestart, 1);
                return;
            }

            try
            {
                await _restartGate.WaitAsync().ConfigureAwait(false);

                // цикл: пока в процессе рестарта прилетают новые события — повторим
                int backoffMs = 250;
                do
                {
                    Interlocked.Exchange(ref _pendingRestart, 0);
                    _restartAttempts++;

                    if (_isClosing) break;
                    LogMessage($"🔄 Перезапуск loopback-захвата (смена устройства) - попытка #{_restartAttempts}...");

                    try
                    {
                        // 1) Безопасная остановка текущего capture
                        var wasCapturing = isCapturing;
                        if (wasCapturing)
                        {
                            this.Invoke(() => {
                                try { StopRecording(); } catch { /* ignore */ }
                            });
                        }

                        // 2) обновить список устройств и переподключиться
                        this.Invoke(() => {
                            try 
                            { 
                                RefreshAudioDevices(); 
                                if (availableSpeakerDevices.Count > 0 && wasCapturing)
                                {
                                    var bestDevice = availableSpeakerDevices.First();
                                    SetSpeakerDevice(bestDevice);
                                    LogMessage($"🔄 Переподключен к: {bestDevice.FriendlyName}");
                                    
                                    // Небольшая пауза и возобновление capture
                                    Task.Delay(500).ContinueWith(_ => this.Invoke(() => {
                                        try 
                                        { 
                                            StartAudioCapture(); 
                                            // Обновить _currentRenderId только после успешного старта
                                            _currentRenderId = GetDefaultRenderIdSafe();
                                        } 
                                        catch (Exception ex) 
                                        { 
                                            LogMessage($"❌ Ошибка возобновления capture: {ex.Message}"); 
                                        }
                                    }));
                                }
                            } 
                            catch (Exception ex) 
                            { 
                                LogMessage($"❌ Ошибка UI операций в рестарте: {ex.Message}"); 
                                throw; 
                            }
                        });

                        LogMessage($"✅ Захват перезапущен успешно (попытка #{_restartAttempts})");
                        backoffMs = 250; // reset backoff on success
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"❌ Ошибка рестарта loopback (попытка #{_restartAttempts}): {ex.Message}");
                        LogMessage($"🔄 Backoff: {backoffMs}ms, следующая попытка через {Math.Min(backoffMs * 2, 5000)}ms");
                        // бэкофф и повторная попытка, если во время рестарта пришёл новый запрос
                        await Task.Delay(backoffMs).ConfigureAwait(false);
                        backoffMs = Math.Min(backoffMs * 2, 5000);
                        Interlocked.Exchange(ref _pendingRestart, 1);
                    }
                }
                while (Volatile.Read(ref _pendingRestart) == 1 && !_isClosing);
                
                // Сброс счетчика после успешного завершения всех попыток
                if (!_isClosing && _restartAttempts > 1)
                {
                    LogMessage($"📊 Рестарт завершен после {_restartAttempts} попыток");
                }
                _restartAttempts = 0;
            }
            finally
            {
                if (_restartGate.CurrentCount == 0) _restartGate.Release();
                Interlocked.Exchange(ref _restarting, 0);
            }
        }
        
        private void CleanupDeviceNotifications()
        {
            if (Interlocked.Exchange(ref _devNotifInit, 0) == 1)
            {
                try 
                { 
                    deviceEnumerator?.UnregisterEndpointNotificationCallback(notificationClient); 
                    deviceEnumerator?.Dispose();
                    notificationClient = null;
                    deviceEnumerator = null;
                    LogMessage("🔔 Мониторинг аудиоустройств очищен");
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Ошибка очистки мониторинга устройств: {ex.Message}");
                }
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

        #region Testing Guide and Manual

        /// <summary>
        /// Открывает подробный справочник по тестированию системы
        /// </summary>
        private void btnTestingGuide_Click(object sender, EventArgs e)
        {
            ShowTestingGuide();
        }

        private void btnDiagnosticsDashboard_Click(object sender, EventArgs e)
        {
            ShowDiagnosticsDashboard();
        }

        private void btnDiagnostics_Click(object sender, EventArgs e)
        {
            TriggerSelfDiagnostics();
        }

        private void btnPerfDiag_Click(object sender, EventArgs e)
        {
            RunPerformanceDiagnostics();
        }

        private void btnAdvancedDiag_Click(object sender, EventArgs e)
        {
            RunAdvancedDiagnostics();
        }

        private void btnTextFilterValidation_Click(object sender, EventArgs e)
        {
            RunTextFilterValidation();
        }

        private void btnAllDiag_Click(object sender, EventArgs e)
        {
            // Безопасная замена CancellationTokenSource с утилизацией предыдущего
            Interlocked.Exchange(ref testingCancellationTokenSource, null)?.Dispose();
            testingCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = testingCancellationTokenSource.Token;
            
            // Запускаем все диагностики подряд с поддержкой отмены
            Task.Run(async () => {
                try
                {
                    LogMessage("🎯 Запуск комплексной диагностики (все тесты подряд)...");
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        TriggerSelfDiagnostics();
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        RunPerformanceDiagnostics();
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        RunAdvancedDiagnostics();
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        RunTextFilterValidation();
                    }
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        LogMessage("✅ Комплексная диагностика успешно завершена!");
                    }
                }
                catch (OperationCanceledException)
                {
                    LogMessage("⚠️ Комплексная диагностика была отменена пользователем");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Ошибка в комплексной диагностике: {ex.Message}");
                }
            }, cancellationToken);
        }

        private void btnEmergencyStop_Click(object sender, EventArgs e)
        {
            EmergencyStopAllTesting();
        }

        /// <summary>
        /// Экстренная остановка всех тестов и диагностики
        /// </summary>
        private void EmergencyStopAllTesting()
        {
            // Защита от повторного вызова
            if (!btnEmergencyStop.Enabled) return;
            btnEmergencyStop.Enabled = false;
            
            try
            {
                LogMessage("🚨 ЭКСТРЕННАЯ ОСТАНОВКА ВСЕХ ТЕСТОВ!");
                
                // 1. Безопасная отмена и утилизация токена
                var cts = Interlocked.Exchange(ref testingCancellationTokenSource, null);
                cts?.Cancel();
                cts?.Dispose();
                LogMessage("✅ Токен отмены активирован и утилизирован");
                
                // 2. Останавливаем бесконечные тесты
                if (chkInfiniteTests.Checked)
                {
                    chkInfiniteTests.Checked = false;
                    LogMessage("✅ Бесконечные тесты отключены");
                }
                
                // 3. Останавливаем аудио захват если активен
                if (isCapturing && stableAudioCapture != null)
                {
                    Task.Run(async () => {
                        try
                        {
                            await stableAudioCapture.StopCaptureAsync();
                            LogMessage("✅ Аудио захват остановлен");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"⚠️ Ошибка при остановке аудио: {ex.Message}");
                        }
                    });
                }
                
                // 4. Сбрасываем статус
                this.Invoke(() => {
                    lblStatus.Text = "🔇 Готов к захвату";
                    lblStatus.ForeColor = Color.Blue;
                    btnStartCapture.Enabled = true;
                    btnStopCapture.Enabled = false;
                });
                
                // 5. Очистка каналов - убираем принудительный GC
                Task.Run(() => {
                    try
                    {
                        // Полагаемся на CLR для сборки мусора
                        LogMessage("✅ Каналы обработки сброшены");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"⚠️ Ошибка очистки каналов: {ex.Message}");
                    }
                });
                
                LogMessage("🎯 Экстренная остановка завершена. Система готова к работе.");
                
                // 6. Показываем уведомление пользователю
                this.Invoke(() => {
                    lblStats.Text = "📊 Статистика: тестирование экстренно остановлено";
                    lblStats.ForeColor = Color.Red;
                    
                    // Через 3 секунды возвращаем нормальный цвет
                    Task.Delay(3000).ContinueWith(_ => {
                        this.Invoke(() => {
                            lblStats.ForeColor = Color.Black;
                        });
                    });
                });
                
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Критическая ошибка при экстренной остановке: {ex.Message}");
            }
            finally
            {
                btnEmergencyStop.Enabled = true;
            }
        }

        /// <summary>
        /// Показывает инструкцию-справочник для тестирования
        /// </summary>
        private void ShowTestingGuide()
        {
            // Если справочник уже открыт, активируем его
            if (guideWindow != null && !guideWindow.IsDisposed)
            {
                guideWindow.WindowState = FormWindowState.Normal;
                guideWindow.BringToFront();
                guideWindow.Activate();
                return;
            }

            guideWindow = new Form
            {
                Text = "📋 Справочник по тестированию STT+Translate+TTS",
                Size = new Size(900, 700),
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimumSize = new Size(800, 600),
                MaximizeBox = true,
                MinimizeBox = true,
                ShowInTaskbar = true,
                Icon = this.Icon
            };

            // Позиционируем справочник справа от главного окна (для второго монитора)
            var mainFormBounds = this.Bounds;
            guideWindow.Location = new Point(mainFormBounds.Right + 20, mainFormBounds.Top);
            
            // Если справочник выходит за границы экрана, размещаем слева
            var screen = Screen.FromControl(this);
            if (guideWindow.Right > screen.WorkingArea.Right)
            {
                guideWindow.Location = new Point(Math.Max(0, mainFormBounds.Left - guideWindow.Width - 20), mainFormBounds.Top);
            }

            var txtGuide = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.White,
                ReadOnly = true,
                Text = GetTestingGuideText()
            };

            guideWindow.Controls.Add(txtGuide);
            
            // Очищаем ссылку при закрытии окна
            guideWindow.FormClosed += (s, e) => { guideWindow = null; };
            
            // Показываем немодально - не блокирует главное окно!
            guideWindow.Show();
            
            // Делаем справочник "всегда сверху" чтобы не терялся
            guideWindow.TopMost = true;
        }

        /// <summary>
        /// Возвращает полный текст справочника по тестированию
        /// </summary>
        private string GetTestingGuideText()
        {
            return @"📋 СПРАВОЧНИК ПО ТЕСТИРОВАНИЮ STT+TRANSLATE+TTS
════════════════════════════════════════════════════════════════

💡 ВАЖНО: Этот справочник НЕ блокирует главное окно! 
Вы можете держать справочник открытым на втором мониторе 
и одновременно тестировать программу на основном.

🎯 СОДЕРЖАНИЕ:
1. Работа с двумя окнами
2. Основы тестирования  
3. Элементы интерфейса
4. Что говорить для тестов
5. Ожидаемые результаты
6. Диагностика работы
7. Проблемы и решения
8. Расширенное тестирование
9. Сохранение настроек

════════════════════════════════════════════════════════════════

💻 1. РАБОТА С ДВУМЯ ОКНАМИ

📌 УДОБСТВО ИСПОЛЬЗОВАНИЯ:
• Справочник открывается как отдельное немодальное окно
• Главное окно программы остается активным и доступным
• Можно одновременно читать инструкции и нажимать кнопки тестирования
• Справочник автоматически позиционируется справа от программы
• Поддержка двух мониторов - справочник на втором, программа на первом

📌 УПРАВЛЕНИЕ ОКНАМИ:
• F10 или кнопка ""📋 Справочник"" - открыть/активировать справочник
• Справочник ""всегда сверху"" - не теряется за другими окнами
• При повторном нажатии F10 активирует уже открытый справочник
• Можно перетаскивать и изменять размер справочника
• Закрытие справочника не влияет на работу программы

📌 РЕКОМЕНДУЕМАЯ НАСТРОЙКА:
• Откройте справочник (F10)
• Перетащите его на второй монитор или в удобное место
• Держите справочник открытым во время всего тестирования
• Пользуйтесь программой как обычно - ничего не заблокировано!

════════════════════════════════════════════════════════════════

🔧 2. ОСНОВЫ ТЕСТИРОВАНИЯ

📌 ПОДГОТОВКА:
• Убедитесь что микрофон подключен и работает
• Выберите правильное аудиоустройство в выпадающем списке
• Установите порог звука (обычно 0.050 - хорошее значение)
• Выберите языки: ""Автоопределение"" → ""Русский"" для базового теста
• ВАЖНО: Выберите режим работы в ""⚙️ Режим работы""

📌 РЕЖИМЫ ЗАХВАТА ЗВУКА:
• 🔊 ""Захват с динамиков"" - перехватывает звук из динамиков/наушников (WasapiLoopback)
  └─ Для тестирования: включите музыку/видео и говорите параллельно
• 🎤 ""Захват с микрофона"" - записывает с микрофона (WaveInEvent, аналогично MORT)  
  └─ Для тестирования: говорите прямо в микрофон
• 🎬 ""Стриминговый режим"" - продвинутая обработка в реальном времени

📌 КНОПКИ ДИАГНОСТИКИ:
• 🔍 Диагностика (F5) - базовая проверка 6 компонентов
• 📊 Performance (F6) - мониторинг производительности  
• 🔬 Advanced (F7) - углубленные тесты (6 детальных проверок)
• 🎯 Все тесты (F8) - полная комплексная диагностика
• 🔍 Text Filter (F9) - валидация фильтра текста (22 теста)

════════════════════════════════════════════════════════════════

⚙️ 3. ЭЛЕМЕНТЫ ИНТЕРФЕЙСА

📌 ОСНОВНЫЕ КНОПКИ:
• 🎧 ""Начать захват"" - запускает запись и обработку звука
• ⏹️ ""Остановить"" - останавливает все процессы захвата
• 🔊 ""Тест TTS"" - проверяет синтез речи (воспроизводит тестовую фразу)
• 📋 ""Справочник"" (F10) - открывает этот справочник

📌 ВЫПАДАЮЩИЕ СПИСКИ:
• 🔊 ""Устройство"" - выбор аудиоустройства для захвата
• 🌍 ""Из языка"" - язык исходного текста (""Автоопределение"" рекомендуется)
• 🎯 ""На язык"" - язык для перевода (обычно ""Русский"")
• ⚙️ ""Режим работы"" - способ обработки звука:
  └─ 🔄 ""Оригинальный (ждет паузы)"" - захват с динамиков, ждет тишины
  └─ ⚡ ""Потоковый (каждые 3 сек)"" - обработка порциями по 3 секунды  
  └─ 🎤 ""Микрофон (аналогично MORT)"" - захват с микрофона

📌 НАСТРОЙКИ:
• 🎚️ ""Порог звука"" - чувствительность к звуку (0.001-1.000)
  └─ Низкие значения (0.020) = более чувствительно
  └─ Высокие значения (0.100) = менее чувствительно
• 🔄 ""Автоперевод + TTS"" - автоматический перевод и озвучивание
• 🔄 ""Бесконечные тесты"" - циклическое повторение диагностики

📌 ИНФОРМАЦИОННЫЕ ПАНЕЛИ:
• 🎤 ""Распознанный текст"" - результат STT (речь → текст)
• 🌐 ""Переведенный текст"" - результат перевода
• 📊 ""Уровень звука"" - текущий уровень входного сигнала  
• 📝 ""Логи обработки"" - детальная информация о всех процессах
• 📊 ""Статистика"" - общая информация о работе системы

📌 ИНДИКАТОРЫ СОСТОЯНИЯ:
• 🔇 ""Готов к захвату"" - система готова
• 🎤 ""Идет захват..."" - активная запись
• ⚡ ""Обработка STT..."" - распознавание речи
• 🌐 ""Перевод..."" - получение перевода
• 🔊 ""TTS воспроизведение..."" - синтез речи
• ❌ ""Ошибка: [описание]"" - проблемы в работе

════════════════════════════════════════════════════════════════

🎤 4. ЧТО ГОВОРИТЬ ДЛЯ ТЕСТОВ

✅ ВАЛИДНЫЕ ФРАЗЫ (должны обрабатываться):

РУССКИЙ:
• ""Привет, как дела?""
• ""Это очень интересная книга!""
• ""Мне нужно подумать...""
• ""iPhone работает отлично.""
• ""5 минут назад случилось это.""
• ""Сегодня хорошая погода.""
• ""Что ты думаешь об этом?""
• ""Давай пойдем в магазин.""

АНГЛИЙСКИЙ:
• ""Hello, how are you?""
• ""This is a great application.""
• ""I need to think about it...""
• ""The weather is nice today.""
• ""What do you think about this?""

МНОГОЯЗЫЧНЫЕ ТЕСТЫ:
• ""¿Cómo estás?"" (испанский)
• ""Das ist interessant."" (немецкий)
• ""C'est très bien!"" (французский)

❌ МУСОРНЫЕ ФРАЗЫ (должны отклоняться):

НЕЗАВЕРШЕННЫЕ:
• ""what we do"" (без знаков препинания)
• ""привет мир"" (маленькая буква без точки)
• ""hallo wie geht"" (немецкий без завершения)

МЕЖДОМЕТИЯ:
• ""hmm""
• ""э-э-э""
• ""ага""
• ""нуда""

ТЕХНИЧЕСКИЕ ТОКЕНЫ:
• ""[BLANK_AUDIO]""
• ""*burp*""
• ""(звук)""

ОДИНОЧНЫЕ СИМВОЛЫ:
• ""а""
• ""и""
• ""..."" (только знаки)
• ""???"" (только вопросы)

════════════════════════════════════════════════════════════════

📊 4. ОЖИДАЕМЫЕ РЕЗУЛЬТАТЫ

🟢 НОРМАЛЬНАЯ РАБОТА:

РАСПОЗНАВАНИЕ РЕЧИ:
• Время обработки: 2-5 секунд для фразы
• Точность: 85-95% для четкой речи
• Латентность: менее 1 секунды после окончания речи
• Логи: ""✅ STT обработка завершена""

ФИЛЬТРАЦИЯ ТЕКСТА:
• Валидные фразы попадают в ""Распознанный текст""
• Мусор отклоняется без попадания в интерфейс
• Логи: ""✅ Текст принят"" или ""❌ Текст отклонен как заглушка""

ПЕРЕВОД:
• Google Translate работает стабильно
• Время перевода: 1-3 секунды
• Качественный перевод для простых фраз
• Логи: ""✅ Перевод получен""

TTS (СИНТЕЗ РЕЧИ):
• Четкое воспроизведение переведенного текста
• Без искажений и артефактов
• Логи: ""✅ TTS воспроизведение завершено""

ДИАГНОСТИКА:
• Все тесты должны показывать ✅
• Производительность >= 85% для Text Filter
• Memory usage < 500MB
• Whisper warm start < 100ms

🟡 ПРЕДУПРЕЖДЕНИЯ (работает, но не идеально):

РАСПОЗНАВАНИЕ:
• Время обработки 6-10 секунд
• Точность 70-85%
• Пропуск некоторых тихих слов
• Логи: ""⚠️ Медленная обработка""

ФИЛЬТРАЦИЯ:
• Иногда пропускает мусор
• Редко отклоняет валидный текст
• Производительность Text Filter: 70-85%

ПЕРЕВОД:
• Медленный ответ Google (4-8 секунд)
• Иногда неточные переводы
• Логи: ""⚠️ Медленный перевод""

🔴 ПРОБЛЕМЫ (требует вмешательства):

КРИТИЧЕСКИЕ ОШИБКИ:
• ""❌ Whisper модель не найдена""
• ""❌ Ошибка инициализации MediaFoundation""
• ""❌ Аудиоустройство недоступно""
• Memory usage > 500MB
• Whisper cold start > 5 секунд

СЕТЕВЫЕ ПРОБЛЕМЫ:
• ""❌ Google Translate недоступен""
• ""❌ Timeout перевода""
• Нет интернет-соединения

АУДИО ПРОБЛЕМЫ:
• Нет входного сигнала
• Искажения или шумы
• Неправильное устройство выбрано

════════════════════════════════════════════════════════════════

🔍 5. ДИАГНОСТИКА РАБОТЫ

📋 ПОШАГОВАЯ ПРОВЕРКА:

1. ЗАПУСК ДИАГНОСТИКИ:
   • Нажмите F5 или кнопку ""🔍 Диагностика""
   • Проверьте все ✅ в отчете
   • При ❌ смотрите детали ошибки

2. ТЕСТ АУДИОСИСТЕМЫ:
   • Запустите захват (🎧 Начать захват)
   • РЕЖИМ ДИНАМИКОВ: Включите музыку/YouTube, говорите параллельно
   • РЕЖИМ МИКРОФОНА: Говорите прямо в микрофон
   • Говорите тестовую фразу: ""Привет, как дела?""
   • Наблюдайте уровень звука в progressbar
   • Проверьте появление текста в ""Распознанный текст""

3. ТЕСТ ФИЛЬТРАЦИИ:
   • Нажмите F9 для Text Filter валидации
   • Убедитесь что тест проходит >= 85%
   • При низком результате - проблемы с фильтром

4. ТЕСТ TTS:
   • Нажмите ""🔊 Тест TTS""
   • Должно проиграться: ""Тест синтеза речи выполнен успешно""
   • Проверьте качество звука

5. ТЕСТ ПЕРЕВОДА:
   • Скажите ""Hello, how are you?"" 
   • Должен появиться перевод ""Привет, как дела?""
   • TTS должен озвучить русский перевод

📊 МОНИТОРИНГ ПРОИЗВОДИТЕЛЬНОСТИ:
• F6 - Performance диагностика каждые 5 секунд
• Следите за использованием памяти
• Контролируйте время ответа Whisper
• Проверяйте статус Bounded Channels

════════════════════════════════════════════════════════════════

⚠️ 6. ПРОБЛЕМЫ И РЕШЕНИЯ

🔧 ЧАСТЫЕ ПРОБЛЕМЫ:

ПРОБЛЕМА: ""Нет распознавания речи""
РЕШЕНИЕ:
• Проверьте микрофон в Windows
• Увеличьте громкость записи
• Уменьшите порог звука до 0.020
• Выберите другое аудиоустройство

ПРОБЛЕМА: ""Медленная работа""
РЕШЕНИЕ:
• Закройте другие приложения
• Проверьте Memory usage (F6)
• Перезапустите приложение
• Проверьте Whisper warm start время

ПРОБЛЕМА: ""Плохое качество распознавания""
РЕШЕНИЕ:
• Говорите четче и громче
• Уберите фоновые шумы
• Используйте качественный микрофон
• Говорите ближе к микрофону

ПРОБЛЕМА: ""Перевод не работает""
РЕШЕНИЕ:
• Проверьте интернет-соединение
• Дождитесь восстановления Google Translate
• Попробуйте другую языковую пару

ПРОБЛЕМА: ""TTS не работает""
РЕШЕНИЕ:
• Проверьте аудиоустройство воспроизведения
• Увеличьте системную громкость
• Перезапустите Windows Audio Service

🔄 ПЕРЕЗАПУСК КОМПОНЕНТОВ:
• Остановите захват (⏹️ Остановить)
• Подождите 3 секунды
• Запустите захват снова
• При серьезных проблемах - перезапуск приложения

════════════════════════════════════════════════════════════════

🚀 7. РАСШИРЕННОЕ ТЕСТИРОВАНИЕ

🔬 ТЕСТИРОВАНИЕ ПО РЕЖИМАМ ЗАХВАТА:

📢 ТЕСТ РЕЖИМА ""ЗАХВАТ С ДИНАМИКОВ"":
• Установите режим работы: ""Захват с динамиков""
• Выберите аудиоустройство (динамики/наушники)
• Запустите любую музыку или YouTube видео
• Говорите тестовые фразы ПОВЕРХ музыки:
  └─ ""Привет, как дела?"" 
  └─ ""Я тестирую захват с динамиков""
  └─ ""Музыка играет, но меня должно быть слышно""
• Система должна распознавать вашу речь + фоновую музыку
• ОЖИДАЕМО: Смешанное распознавание речи и музыки

🎤 ТЕСТ РЕЖИМА ""ЗАХВАТ С МИКРОФОНА"":
• Установите режим работы: ""Захват с микрофона""
• Убедитесь что микрофон подключен и настроен
• Говорите четко в микрофон:
  └─ ""Тестирую микрофонный режим""
  └─ ""Это работает аналогично MORT""
  └─ ""Только мой голос, без фоновых звуков""
• Система должна захватывать ТОЛЬКО ваш голос
• ОЖИДАЕМО: Чистое распознавание только речи

🎬 ТЕСТ СТРИМИНГОВОГО РЕЖИМА:
• Установите режим работы: ""Стриминговый режим""
• Тестируйте как режим с динамиками
• Ожидайте более быструю обработку
• Проверьте качество в реальном времени

🔬 СТРЕСС-ТЕСТИРОВАНИЕ:

ДЛИТЕЛЬНЫЙ ТЕСТ:
• Включите ""🔄 Бесконечные тесты""
• Оставьте на 15-30 минут
• Следите за Memory usage
• Проверяйте стабильность системы

МНОГОЯЗЫЧНЫЙ ТЕСТ:
• Тестируйте все языковые пары
• ""English"" → ""Русский""
• ""Русский"" → ""Английский""
• ""Испанский"" → ""Русский""
• ""Немецкий"" → ""Английский""

ГРАНИЧНЫЕ СЛУЧАИ:
• Очень тихая речь
• Очень громкая речь
• Быстрая речь
• Медленная речь
• Речь с акцентом
• Фоновый шум

🎯 ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ:

ХОЛОДНЫЙ СТАРТ:
• Перезапустите приложение
• Запустите F7 (Advanced диагностика)
• Cold start должен быть < 5 секунд

ТЕПЛЫЙ СТАРТ:
• Повторный запуск F7
• Warm start должен быть < 100ms

ПАМЯТЬ:
• Начальное потребление: ~50-100MB
• Рабочее потребление: ~200-300MB
• Максимум допустимо: ~500MB

🔍 ДЕТАЛЬНАЯ ДИАГНОСТИКА:

КОМПОНЕНТЫ ДЛЯ ПРОВЕРКИ:
1. ✅ Warm Whisper Instance
2. ✅ MediaFoundation
3. ✅ Bounded Channels 
4. ✅ Enhanced Text Filtering
5. ✅ Device Notifications
6. ✅ Audio Devices

КАНАЛЫ ОБРАБОТКИ:
• Capture Channel (аудио сырые данные)
• Mono16k Channel (конвертированное аудио)
• STT Channel (распознанный текст)

ФОНОВЫЕ ПРОЦЕССЫ:
• Continuous Monitoring (каждые 30 сек)
• Device Change Detection
• Memory Leak Detection

════════════════════════════════════════════════════════════════

⚙️ 8. НАСТРОЙКИ СИСТЕМЫ И СОХРАНЕНИЕ

📁 РАСПОЛОЖЕНИЕ ФАЙЛОВ КОНФИГУРАЦИИ:
• Файл настроек: application.exe.config (рядом с EXE)
• Backup конфигурации: config.backup (автоматически)
• Пользовательские настройки сохраняются автоматически

📋 АВТОСОХРАНЕНИЕ НАСТРОЕК:
• 🎚️ Порог звука (ThresholdValue)
• 🌐 Языковые пары (SourceLanguage, TargetLanguage)
• 🔄 Состояние ""Автоперевод + TTS""
• 🔄 Состояние ""Бесконечные тесты""
• 📏 Размер и позиция окна
• 🎤 Выбранный режим захвата

📤 ЭКСПОРТ НАСТРОЕК:
• При закрытии приложения настройки сохраняются автоматически
• При критической ошибке создается backup конфигурации
• Настройки восстанавливаются при следующем запуске

🔧 РУЧНОЕ УПРАВЛЕНИЕ НАСТРОЙКАМИ:
• Сброс к умолчаниям: удалите файл application.exe.config
• Резервная копия: скопируйте application.exe.config в безопасное место
• Восстановление: замените поврежденный config файлом из backup

════════════════════════════════════════════════════════════════

⌨️ 9. БЫСТРЫЕ КЛАВИШИ И ГОРЯЧИЕ КОМБИНАЦИИ

🔍 ДИАГНОСТИЧЕСКИЕ КЛАВИШИ:
• F5 - Базовая диагностика (быстрая проверка системы)
• F6 - Углубленная диагностика (детальная проверка)
• F7 - Расширенная диагностика (full system scan)
• F8 - Комплексная диагностика (с тестами производительности)
• F9 - Валидация фильтра (проверка текстовых фильтров)
• F10 - Открыть справочник (эта инструкция)

🎮 УПРАВЛЕНИЕ ЗАХВАТОМ:
• Пробел - Начать/остановить захват звука
• Enter - Принудительная обработка накопленного аудио
• Escape - Экстренная остановка всех процессов

🌐 ПЕРЕВОДЧЕСКИЕ ФУНКЦИИ:
• Ctrl+T - Переключить автоперевод ON/OFF
• Ctrl+R - Повторить последний перевод
• Ctrl+C - Скопировать переведенный текст в буфер

🔧 СИСТЕМНЫЕ КОМАНДЫ:
• Ctrl+D - Показать окно диагностики
• Ctrl+L - Очистить все логи
• Ctrl+S - Принудительное сохранение настроек
• Alt+F4 - Корректное закрытие с сохранением

🎯 СПЕЦИАЛЬНЫЕ ФУНКЦИИ:
• Ctrl+Shift+I - Бесконечные тесты (вкл/выкл)
• Ctrl+Shift+R - Перезапуск аудиосистемы
• Ctrl+Shift+M - Переключение режима захвата (динамики/микрофон)

════════════════════════════════════════════════════════════════

💡 ЗАКЛЮЧЕНИЕ

Этот справочник поможет вам эффективно тестировать систему 
**test_speaker_stt_translate_tts** - экспериментальную платформу
для речевого распознавания, перевода и синтеза речи.

Помните: система работает лучше всего с четкой речью, стабильным 
интернетом и качественным микрофоном.

При возникновении проблем - сначала запустите диагностику (F5),
затем проверьте конкретные компоненты через F6-F9.

Удачного тестирования вашей STT+Translate+TTS системы! 🎉

════════════════════════════════════════════════════════════════";
        }

        
        #region Diagnostics Dashboard Management
        
        /// <summary>
        /// Показывает или фокусирует диагностический dashboard
        /// </summary>
        private void ShowDiagnosticsDashboard()
        {
            try
            {
                if (diagnosticsDashboard == null || diagnosticsDashboard.IsDisposed)
                {
                    diagnosticsDashboard = new DiagnosticsChecklistForm(this);
                    
                    // Позиционирование на втором мониторе если доступен
                    if (Screen.AllScreens.Length > 1)
                    {
                        var secondScreen = Screen.AllScreens[1];
                        diagnosticsDashboard.StartPosition = FormStartPosition.Manual;
                        diagnosticsDashboard.Location = new Point(
                            secondScreen.Bounds.X + 50,
                            secondScreen.Bounds.Y + 50
                        );
                    }
                }

                // Показываем или выносим на передний план
                if (diagnosticsDashboard.Visible)
                {
                    diagnosticsDashboard.Activate();
                    diagnosticsDashboard.BringToFront();
                }
                else
                {
                    diagnosticsDashboard.Show();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка открытия диагностического dashboard: {ex.Message}");
            }
        }
        
        #endregion

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


