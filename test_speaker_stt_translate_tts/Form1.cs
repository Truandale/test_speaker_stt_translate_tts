using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;
using System.Speech.Synthesis;
using RestSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Linq;

namespace test_speaker_stt_translate_tts
{
    public partial class Form1 : Form
    {
        #region Private Fields
        
        private WasapiLoopbackCapture? wasapiCapture;
        private WaveInEvent? waveInCapture;
        private List<byte> audioBuffer = new();
        private bool isCapturing = false;
        private bool isCollectingAudio = false;
        private int audioLogCount = 0; // Для отладки перезапуска
        private volatile bool isTTSActive = false; // Для отслеживания активных TTS операций
        private DateTime lastVoiceActivity = DateTime.Now;
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
        
        // STT & Translation
        private static string WhisperModelPath => Path.Combine(Application.StartupPath, "models", "whisper", "ggml-small.bin");
        private SpeechSynthesizer? speechSynthesizer;
        private TtsVoiceManager? ttsVoiceManager;
        private RestClient? googleTranslateClient;
        
        // Статистика
        private int totalProcessedFrames = 0;
        private DateTime sessionStartTime = DateTime.Now;
        
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
            LogMessage("🚀 Инициализация приложения...");
            
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
            InitializeTTS();
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
            
            LogMessage("✅ Приложение готово к работе");
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
                
                // Конвертируем и обрабатываем аудио
                await ProcessAudioDataInternal(segment.AudioData);
                
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
                
                LogMessage("✅ TTS инициализирован с автоматическим выбором голосов");
                LogMessage($"📢 Доступные голоса: {ttsVoiceManager.GetVoiceInfo()}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка инициализации TTS: {ex.Message}");
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
            audioLevelTimer.Interval = 100; // Update every 100ms
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
            StartAudioCapture();
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
                        await StopAudioCapture();
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

        private async void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            // Проверяем, что форма не была освобождена
            if (IsDisposed || !IsHandleCreated || isDisposed)
            {
                return; // Форма закрыта, прекращаем обработку аудио
            }
            
            if (!isCapturing) 
            {
                LogMessage("⚠️ OnAudioDataAvailable: isCapturing=false, игнорируем данные");
                return;
            }

            // ПРАВИЛЬНАЯ ЛОГИКА: НЕ ИГНОРИРУЕМ РЕЧЬ СОБЕСЕДНИКА ВО ВРЕМЯ TTS
            // Во время TTS накапливаем аудио для последующей обработки
            if (isTTSActive || (speechSynthesizer?.State == System.Speech.Synthesis.SynthesizerState.Speaking))
            {
                if (smartAudioManager != null)
                {
                    // Копируем текущие аудиоданные для накопления
                    byte[] currentAudio = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, currentAudio, e.BytesRecorded);
                    
                    // Добавляем в очередь для обработки после TTS
                    smartAudioManager.QueueAudioSegment(currentAudio, DateTime.Now, "tts_period");
                    
                    // Также сохраняем текущий буфер если идет запись
                    if (isCollectingAudio && audioBuffer.Count > 0)
                    {
                        byte[] bufferedAudio = audioBuffer.ToArray();
                        smartAudioManager.QueueAudioSegment(bufferedAudio, DateTime.Now, "tts_buffered");
                        audioBuffer.Clear();
                        isCollectingAudio = false;
                    }
                }
                return; // Аудио сохранено в очереди для последующей обработки
            }

            try
            {
                // Calculate audio level (32-bit float for speakers)
                float level = CalculateAudioLevel(e.Buffer, e.BytesRecorded);
                currentAudioLevel = level;

                // Логирование первых 5 уровней звука для отладки
                if (audioLogCount < 5)
                {
                    LogMessage($"🔊 Аудиоуровень #{audioLogCount + 1}: {level:F3} (порог: {voiceThreshold:F3})");
                    audioLogCount++;
                }

                // ПРОСТАЯ ЛОГИКА VAD КАК ДЛЯ МИКРОФОНА
                bool isVoiceDetected = level > voiceThreshold;

                if (isVoiceDetected)
                {
                    if (!isCollectingAudio)
                    {
                        isCollectingAudio = true;
                        audioBuffer.Clear();
                        recordingStartTime = DateTime.Now;
                        LogMessage($"🔊 Начат захват речи с динамиков (уровень: {level:F3})");
                        
                        Invoke(() => {
                            txtRecognizedText.Text = "🔊 Записываю речь с динамиков...";
                            progressBar.Visible = true;
                        });
                    }
                    
                    lastVoiceActivity = DateTime.Now;
                }

                if (isCollectingAudio)
                {
                    // Добавляем данные как есть (32-bit float для динамиков)
                    audioBuffer.AddRange(e.Buffer.Take(e.BytesRecorded));
                    
                    // Check for max recording time
                    var recordingDuration = DateTime.Now - recordingStartTime;
                    if (recordingDuration.TotalMilliseconds > maxRecordingMs)
                    {
                        isCollectingAudio = false;
                        LogMessage($"⏰ Принудительная остановка записи динамиков (максимум {maxRecordingMs}мс достигнут)");
                        
                        if (audioBuffer.Count > 16000)
                        {
                            LogMessage($"⏹️ Принудительная обработка динамиков (данных: {audioBuffer.Count} байт)");
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "🔄 Обрабатываю аудио с динамиков...";
                            });
                            
                            await ProcessAudioDataInternal(audioBuffer.ToArray());
                        }
                        else
                        {
                            LogMessage("⚠️ Недостаточно данных для обработки с динамиков");
                            audioBuffer.Clear();
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "⚠️ Недостаточно аудиоданных с динамиков";
                                progressBar.Visible = false;
                            });
                        }
                        return;
                    }

                    // Check for silence duration
                    var silenceDuration = DateTime.Now - lastVoiceActivity;
                    if (silenceDuration.TotalMilliseconds > silenceDurationMs)
                    {
                        isCollectingAudio = false;
                        LogMessage($"🔇 Обнаружена пауза в речи с динамиков ({silenceDuration.TotalMilliseconds:F0}мс)");
                        
                        if (audioBuffer.Count > 8000) // Минимальный размер для обработки
                        {
                            LogMessage($"✅ Обрабатываем аудио с динамиков (данных: {audioBuffer.Count} байт)");
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "🔄 Обрабатываю аудио с динамиков...";
                            });
                            
                            await ProcessAudioDataInternal(audioBuffer.ToArray());
                        }
                        else
                        {
                            LogMessage("⚠️ Слишком мало данных для обработки с динамиков");
                            audioBuffer.Clear();
                            
                            Invoke(() => {
                                txtRecognizedText.Text = "⚠️ Слишком короткий фрагмент с динамиков";
                                progressBar.Visible = false;
                            });
                        }
                    }
                }

                // Если включен стриминговый режим, также обрабатываем его
                if (currentProcessingMode == 1 && streamingProcessor != null && isVoiceDetected)
                {
                    ProcessStreamingAudio(e.Buffer, e.BytesRecorded, level);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки аудио: {ex.Message}");
            }
        }
        
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
                        _ = Task.Run(() => ProcessAudioDataInternal(recordedAudio));
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
                        _ = Task.Run(() => ProcessAudioDataInternal(timeoutAudio));
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
                        Task.Run(() => ProcessAudioDataInternal(audioDataCopy));
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
                            _ = Task.Run(() => ProcessAudioDataInternal(timeoutAudio));
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
                            _ = Task.Run(() => ProcessAudioDataInternal(silenceAudio));
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

            // Assuming 32-bit float samples
            for (int i = 0; i < bytesRecorded - 3; i += 4)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                sum += Math.Abs(sample);
                sampleCount++;
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
                
                progressAudioLevel.Value = percentage;
                lblAudioLevel.Text = $"📊 Уровень: {percentage}%";
                lblAudioLevel.ForeColor = percentage > (voiceThreshold * 100) ? Color.Green : Color.Gray;
            }
        }

        #endregion

        #region STT Processing

        private async Task ProcessAudioDataInternal(byte[] audioData)
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
                
                LogMessage($"🎯 Начало STT обработки ({audioData.Length} байт)");
                
                // Convert to WAV format for Whisper
                var wavData = ConvertToWav(audioData);
                LogMessage($"🔄 Конвертация в WAV: {wavData.Length} байт");

                // Perform STT with Whisper.NET
                string recognizedText = await PerformWhisperSTT(wavData);
                
                if (!string.IsNullOrEmpty(recognizedText) && IsValidSpeech(recognizedText))
                {
                    LogMessage($"✅ Распознан текст: '{recognizedText}'");
                    
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

        private async Task<string> PerformWhisperSTT(byte[] wavData)
        {
            try
            {
                LogMessage("🤖 Инициализация Whisper.NET...");
                
                // Create temporary WAV file
                string tempFile = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tempFile, wavData);
                
                try
                {
                    using var whisperFactory = WhisperFactory.FromPath(WhisperModelPath);
                    using var processor = whisperFactory.CreateBuilder()
                        .WithLanguage("auto") // Автоматическое определение языка
                        .WithPrompt("This is human speech") // Фокус на человеческой речи
                        .WithProbabilities() // Включаем вероятности для фильтрации
                        .WithTemperature(0.0f) // Минимальная температура для стабильности
                        .Build();

                    LogMessage("🔄 Обработка аудио через Whisper...");
                    
                    using var fileStream = File.OpenRead(tempFile);
                    var result = new StringBuilder();
                    
                    await foreach (var segment in processor.ProcessAsync(fileStream))
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
                    
                    return result.ToString().Trim();
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
            // 🚀 Используем продвинутый европейский фильтр с debug логированием
            DebugLogSpeechValidation($"🔍 Проверка на заглушку: '{text}'");
            
            bool isValid = EuropeanLanguageFilter.IsValidEuropeanSpeech(text);
            bool isPlaceholder = !isValid;
            
            DebugLogSpeechValidation($"📊 Заглушка: IsValid={isValid}, IsPlaceholder={isPlaceholder}");
            
            return isPlaceholder;
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
                
                // Конвертируем float32 в int16 с ресамплингом
                var samples = new List<short>();
                
                // Простой downsampling: берем каждый (44100/16000) ≈ 2.75-й семпл
                float ratio = (float)sourceSampleRate / targetSampleRate;
                
                for (int i = 0; i < audioData.Length - 3; i += 4)
                {
                    float floatSample = BitConverter.ToSingle(audioData, i);
                    
                    // Ограничиваем диапазон и конвертируем в 16-bit
                    floatSample = Math.Max(-1.0f, Math.Min(1.0f, floatSample));
                    short intSample = (short)(floatSample * 32767f);
                    
                    // Применяем простой downsampling
                    if (samples.Count < (i / 4) / ratio)
                    {
                        samples.Add(intSample);
                    }
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
                
                return wav.ToArray();
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка конвертации WAV: {ex.Message}");
                return audioData; // Fallback: возвращаем исходные данные
            }
        }

        #endregion

        #region Translation & TTS

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

        private async Task SpeakText(string text)
        {
            try
            {
                if (speechSynthesizer == null || ttsVoiceManager == null) return;
                
                // Проверяем, не выполняется ли уже TTS операция
                if (isTTSActive || speechSynthesizer.State == System.Speech.Synthesis.SynthesizerState.Speaking)
                {
                    LogMessage("⚠️ TTS уже выполняется, отменяем предыдущую операцию...");
                    speechSynthesizer.SpeakAsyncCancelAll();
                    await Task.Delay(300); // Увеличенное время ожидания для стабильности
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
                
                LogMessage("✅ Озвучивание завершено");
            }
            catch (OperationCanceledException)
            {
                // Специальная обработка отмены TTS
                isTTSActive = false; // Гарантированно сбрасываем флаг
                smartAudioManager?.NotifyTTSCompleted();
                LogMessage("🛑 TTS отменен пользователем");
            }
            catch (Exception ex)
            {
                // В случае других ошибок также уведомляем о завершении TTS
                isTTSActive = false; // Гарантированно сбрасываем флаг
                smartAudioManager?.NotifyTTSCompleted();
                LogMessage($"❌ Ошибка озвучивания: {ex.Message}");
            }
        }

        private string GetLanguageCode(string languageName)
        {
            // Для автоопределения возвращаем "auto"
            if (languageName == "Автоопределение")
                return "auto";
                
            return languageCodes.TryGetValue(languageName, out string? code) ? code : "en";
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
                
                LogMessage("✅ Приложение закрыто");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при закрытии: {ex.Message}");
            }
        }

        #endregion

        #region Logging

        private void LogMessage(string message)
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

        #region Helper Classes

        #region Form Cleanup

        private void Form1_OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                Debug.WriteLine("🔄 Начало очистки ресурсов при закрытии формы...");
                
                // Останавливаем захват аудио
                isCapturing = false;
                
                // Останавливаем и освобождаем WASAPI захват
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
                
                // Останавливаем и освобождаем WaveIn захват
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
                
                // Останавливаем таймер
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
                
                // Устанавливаем флаг освобождения
                isDisposed = true;
                
                Debug.WriteLine("✅ Очистка ресурсов завершена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Критическая ошибка при очистке ресурсов: {ex.Message}");
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

        #endregion
    }
}
