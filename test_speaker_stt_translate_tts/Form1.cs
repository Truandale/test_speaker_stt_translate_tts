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
        private DateTime lastVoiceActivity = DateTime.Now;
        private DateTime recordingStartTime = DateTime.Now;
        private float voiceThreshold = 0.05f; // Повысим порог активации
        private int silenceDurationMs = 1000; // Сократим до 1 сек
        private int maxRecordingMs = 5000; // Максимум 5 секунд записи (сократили с 10 сек)
        private System.Windows.Forms.Timer? audioLevelTimer;
        private float currentAudioLevel = 0f;
        
        // Processing mode
        private bool isStreamingMode = false;
        
        // Smart Audio Management
        private SmartAudioManager? smartAudioManager;
        
        // User Settings
        private UserSettings userSettings = new UserSettings();
        
        // STT & Translation
        private static string WhisperModelPath => Path.Combine(Application.StartupPath, "models", "whisper", "ggml-small.bin");
        private SpeechSynthesizer? speechSynthesizer;
        private RestClient? googleTranslateClient;
        
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
            InitializeApplication();
        }

        private void InitializeApplication()
        {
            LogMessage("🚀 Инициализация приложения...");
            
            // Загружаем пользовательские настройки
            LoadUserSettings();
            
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
                LogMessage($"🔄 Обработка аудио сегмента из очереди: {segment.AudioData.Length} байт");
                
                // Конвертируем и обрабатываем аудио
                await ProcessAudioDataInternal(segment.AudioData);
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
                LogMessage("✅ TTS инициализирован");
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
            
            cbProcessingMode.SelectedIndexChanged += ProcessingMode_Changed;
            
            LogMessage("✅ Режимы обработки настроены");
        }

        private void ProcessingMode_Changed(object sender, EventArgs e)
        {
            isStreamingMode = cbProcessingMode.SelectedIndex == 1;
            var selectedMode = cbProcessingMode.SelectedIndex switch
            {
                1 => "Потоковый",
                2 => "Микрофон (MORT)",
                _ => "Оригинальный"
            };
            LogMessage($"🔧 Режим обработки изменен на: {selectedMode}");
            
            if (cbProcessingMode.SelectedIndex == 1)
            {
                LogMessage("⚡ Включен потоковый режим - обработка каждые 3 секунды без ожидания пауз");
            }
            else if (cbProcessingMode.SelectedIndex == 2)
            {
                LogMessage("🎤 Включен режим микрофона - как в MORT с WaveInEvent");
            }
            else
            {
                LogMessage("🔄 Включен оригинальный режим - ожидание пауз в речи");
            }
            
            // Сохраняем настройку
            userSettings.ProcessingMode = cbProcessingMode.SelectedIndex;
            UserSettings.AutoSave(userSettings);
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
                    cbProcessingMode.SelectedIndex = userSettings.ProcessingMode;

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
                userSettings.ProcessingMode = cbProcessingMode.SelectedIndex;
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
            StopAudioCapture();
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
                
                // Проверяем режим обработки
                int processingMode = cbProcessingMode.SelectedIndex;
                
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
                
                isCapturing = true;
                audioBuffer.Clear();
                
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

        private void StopAudioCapture()
        {
            try
            {
                LogMessage("⏹️ Остановка захвата аудио...");
                
                isCapturing = false;
                audioLevelTimer?.Stop();
                
                // Очищаем очередь в SmartAudioManager
                if (smartAudioManager != null)
                {
                    smartAudioManager.ClearQueue();
                    LogMessage("🗑️ Очередь обработки очищена");
                }
                
                // Остановка TTS если активен
                if (speechSynthesizer != null)
                {
                    speechSynthesizer.SpeakAsyncCancelAll();
                    LogMessage("🛑 TTS остановлен");
                }
                
                // Остановка WASAPI
                wasapiCapture?.StopRecording();
                wasapiCapture?.Dispose();
                wasapiCapture = null;
                
                // Остановка микрофона
                waveInCapture?.StopRecording();
                waveInCapture?.Dispose();
                waveInCapture = null;
                
                // Update UI
                btnStartCapture.Enabled = true;
                btnStopCapture.Enabled = false;
                lblStatus.Text = "🔇 Готов к захвату";
                lblStatus.ForeColor = Color.Blue;
                progressAudioLevel.Value = 0;
                lblAudioLevel.Text = "📊 Уровень: 0%";
                
                LogMessage("✅ Захват остановлен");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка остановки захвата: {ex.Message}");
            }
        }

        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!isCapturing) return;

            // Проверяем, можно ли обрабатывать аудио (не активен TTS)
            if (smartAudioManager != null && !smartAudioManager.CanProcessAudio())
            {
                // Во время TTS добавляем аудио в очередь для последующей обработки
                if (isCollectingAudio && audioBuffer.Count > 0)
                {
                    byte[] queuedAudio = audioBuffer.ToArray();
                    smartAudioManager.QueueAudioSegment(queuedAudio, DateTime.Now, "tts_pause");
                    audioBuffer.Clear();
                    isCollectingAudio = false;
                    
                    Invoke(() => {
                        txtRecognizedText.Text = "⏸️ TTS активен - аудио в очереди";
                        progressBar.Visible = false;
                    });
                }
                return;
            }

            try
            {
                // Calculate audio level
                float level = CalculateAudioLevel(e.Buffer, e.BytesRecorded);
                currentAudioLevel = level;

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
                    byte[] audioData = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, audioData, e.BytesRecorded);
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
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка обработки аудио: {ex.Message}");
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
                    LogMessage("⚠️ Текст не распознан или отфильтрован как заглушка");
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
                                LogMessage($"🚫 Пропущен сегмент-заглушка: '{cleanText}'");
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
            if (string.IsNullOrWhiteSpace(text)) return false;
            
            string cleanText = text.Trim().ToLower();
            
            // Filter out Whisper placeholders and tokens
            string[] invalidTokens = {
                "[", "]", "(", ")",
                "wheat", "subscribe", "music", "applause", "nice move", "stack", "tablet", "drums",
                "пшеница", "подписаться", "музыка", "аплодисменты",
                "thank you", "спасибо", "thanks", "bye", "пока",
                "this is human speech", "this is human", "human speech" // Добавлены Whisper заглушки
            };
            
            // Check for exact placeholder matches first
            foreach (string token in invalidTokens)
            {
                if (cleanText.Contains(token))
                {
                    LogMessage($"🚫 Отфильтровано как заглушка: содержит '{token}'");
                    return false;
                }
            }
            
            // Check for repetitive patterns (same phrase repeated multiple times)
            string[] words = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 9) // More than 3 words repeated 3 times
            {
                var wordGroups = new Dictionary<string, int>();
                for (int i = 0; i < words.Length - 2; i++)
                {
                    string trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                    if (wordGroups.ContainsKey(trigram))
                        wordGroups[trigram]++;
                    else
                        wordGroups[trigram] = 1;
                }
                
                var mostRepeated = wordGroups.Where(kv => kv.Value >= 3).FirstOrDefault();
                if (mostRepeated.Value >= 3)
                {
                    LogMessage($"🚫 Отфильтрован как повторяющаяся заглушка: '{mostRepeated.Key}' повторяется {mostRepeated.Value} раз");
                    return false;
                }
            }
            
            foreach (string token in invalidTokens)
            {
                if (cleanText.Contains(token.ToLower()))
                {
                    LogMessage($"🚫 Отфильтрован как заглушка: '{text}' (содержит '{token}')");
                    return false;
                }
            }
            
            // Must be at least 3 characters and contain letters
            if (cleanText.Length < 3 || !cleanText.Any(char.IsLetter))
            {
                LogMessage($"🚫 Слишком короткий или не содержит букв: '{text}'");
                return false;
            }
            
            return true;
        }

        private bool IsPlaceholderToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            
            string cleanText = text.Trim().ToLower();
            
            // Быстрая проверка отдельных токенов
            string[] singleTokens = {
                "[music]", "[drums]", "[distorted breathing]", "[drum roll]", "[dark music]", "[distorted sound]",
                "[applause]", "[laughter]", "[beep]", "[click]", "[noise]", "[silence]",
                "music", "drums", "applause", "laughter", "beep", "click", "noise", "silence"
            };
            
            foreach (string token in singleTokens)
            {
                if (cleanText.Equals(token))
                {
                    return true;
                }
            }
            
            // Проверка на содержание скобок
            if (cleanText.Contains("[") || cleanText.Contains("("))
            {
                return true;
            }
            
            return false;
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
                        LogMessage($"✅ Переведено: '{translatedText}'");
                        
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
                    
                    // Parse Google Translate response - более безопасный парсинг
                    try
                    {
                        // Google возвращает массив массивов, где [0][0][0] это переведенный текст
                        var jsonArray = JsonConvert.DeserializeObject<dynamic>(response.Content);
                        
                        if (jsonArray is Newtonsoft.Json.Linq.JArray outerArray && outerArray.Count > 0)
                        {
                            var firstGroup = outerArray[0];
                            if (firstGroup is Newtonsoft.Json.Linq.JArray firstArray && firstArray.Count > 0)
                            {
                                var firstTranslation = firstArray[0];
                                if (firstTranslation is Newtonsoft.Json.Linq.JArray translationArray && translationArray.Count > 0)
                                {
                                    string translatedText = translationArray[0]?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(translatedText))
                                    {
                                        LogMessage($"✅ Успешный парсинг перевода: '{translatedText}'");
                                        return translatedText;
                                    }
                                }
                            }
                        }
                        
                        LogMessage("❌ Не удалось извлечь перевод из JSON ответа");
                        return string.Empty;
                    }
                    catch (JsonException jsonEx)
                    {
                        LogMessage($"❌ Ошибка парсинга JSON: {jsonEx.Message}");
                        
                        // Fallback: попытка простого regex парсинга
                        var match = System.Text.RegularExpressions.Regex.Match(response.Content, @"\[\[\[""([^""]+)""");
                        if (match.Success && match.Groups.Count > 1)
                        {
                            string simpleResult = match.Groups[1].Value;
                            LogMessage($"✅ Fallback парсинг успешен: '{simpleResult}'");
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
        }

        private async Task SpeakText(string text)
        {
            try
            {
                if (speechSynthesizer == null) return;
                
                LogMessage($"🔊 Озвучивание: '{text}'");
                
                // Уведомляем SmartAudioManager о начале TTS
                smartAudioManager?.NotifyTTSStarted();
                
                await Task.Run(() => {
                    speechSynthesizer.Speak(text); // Используем синхронный Speak для корректной работы событий
                });
                
                // Уведомляем SmartAudioManager о завершении TTS
                smartAudioManager?.NotifyTTSCompleted();
                
                LogMessage("✅ Озвучивание завершено");
            }
            catch (Exception ex)
            {
                // В случае ошибки также уведомляем о завершении TTS
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
            string testText = "";
            
            // Безопасное получение значения из UI потока
            Invoke(() => {
                testText = cbTargetLang.SelectedItem?.ToString() == "Русский" 
                    ? "Тест системы озвучивания текста" 
                    : "Text to speech system test";
            });
                
            await SpeakText(testText);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                LogMessage("🔄 Закрытие приложения...");
                
                // Сохраняем настройки
                SaveCurrentSettings();
                
                // Остановка захвата
                StopAudioCapture();
                
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
            
            if (InvokeRequired)
            {
                Invoke(() => AddLogEntry(logEntry));
            }
            else
            {
                AddLogEntry(logEntry);
            }
        }

        private void AddLogEntry(string logEntry)
        {
            txtLogs.AppendText(logEntry + Environment.NewLine);
            txtLogs.SelectionStart = txtLogs.Text.Length;
            txtLogs.ScrollToCaret();
        }

        #endregion

        #region Helper Classes

        private class AudioDevice
        {
            public string Name { get; set; } = string.Empty;
            public MMDevice Device { get; set; } = null!;
            
            public override string ToString() => Name;
        }

        #endregion
    }
}
