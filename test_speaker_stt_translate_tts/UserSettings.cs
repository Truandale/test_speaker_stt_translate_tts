using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Класс для сохранения и загрузки пользовательских настроек приложения
    /// </summary>
    public class UserSettings
    {
        [JsonPropertyName("selectedAudioDevice")]
        public string SelectedAudioDevice { get; set; } = "";

        [JsonPropertyName("sourceLanguage")]
        public string SourceLanguage { get; set; } = "Автоопределение";

        [JsonPropertyName("targetLanguage")]
        public string TargetLanguage { get; set; } = "Русский";

        [JsonPropertyName("voiceThreshold")]
        public float VoiceThreshold { get; set; } = 0.05f;

        [JsonPropertyName("processingMode")]
        public int ProcessingMode { get; set; } = 0; // 0 = Поток, 1 = Паузы, 2 = Микрофон

        [JsonPropertyName("autoTranslateAndTTS")]
        public bool AutoTranslateAndTTS { get; set; } = true;

        [JsonPropertyName("silenceDurationMs")]
        public int SilenceDurationMs { get; set; } = 1000;

        [JsonPropertyName("maxRecordingMs")]
        public int MaxRecordingMs { get; set; } = 5000;

        [JsonPropertyName("windowWidth")]
        public int WindowWidth { get; set; } = 800;

        [JsonPropertyName("windowHeight")]
        public int WindowHeight { get; set; } = 600;

        [JsonPropertyName("windowLocationX")]
        public int WindowLocationX { get; set; } = -1;

        [JsonPropertyName("windowLocationY")]
        public int WindowLocationY { get; set; } = -1;

        /// <summary>
        /// Путь к файлу настроек
        /// </summary>
        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpeakerSTTTranslateTTS",
            "settings.json"
        );

        /// <summary>
        /// Сохранение настроек в файл
        /// </summary>
        public static bool SaveSettings(UserSettings settings)
        {
            try
            {
                string settingsDir = Path.GetDirectoryName(SettingsFilePath)!;
                if (!Directory.Exists(settingsDir))
                {
                    Directory.CreateDirectory(settingsDir);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string jsonString = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(SettingsFilePath, jsonString, System.Text.Encoding.UTF8);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка сохранения настроек: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Загрузка настроек из файла
        /// </summary>
        public static UserSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    // Возвращаем настройки по умолчанию
                    var defaultSettings = new UserSettings();
                    SaveSettings(defaultSettings); // Создаем файл с настройками по умолчанию
                    return defaultSettings;
                }

                string jsonString = File.ReadAllText(SettingsFilePath, System.Text.Encoding.UTF8);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var settings = JsonSerializer.Deserialize<UserSettings>(jsonString, options);
                return settings ?? new UserSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка загрузки настроек: {ex.Message}");
                // При ошибке возвращаем настройки по умолчанию
                return new UserSettings();
            }
        }

        /// <summary>
        /// Автоматическое сохранение настроек с задержкой
        /// </summary>
        public static void AutoSave(UserSettings settings, int delayMs = 1000)
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = delayMs;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                SaveSettings(settings);
            };
            timer.Start();
        }

        /// <summary>
        /// Проверка валидности настроек
        /// </summary>
        public bool IsValid()
        {
            return VoiceThreshold >= 0.0f && VoiceThreshold <= 1.0f &&
                   SilenceDurationMs > 0 && SilenceDurationMs <= 10000 &&
                   MaxRecordingMs > 0 && MaxRecordingMs <= 60000 &&
                   ProcessingMode >= 0 && ProcessingMode <= 2;
        }

        /// <summary>
        /// Применение безопасных значений для некорректных настроек
        /// </summary>
        public void ApplySafeDefaults()
        {
            if (VoiceThreshold < 0.0f || VoiceThreshold > 1.0f)
                VoiceThreshold = 0.05f;

            if (SilenceDurationMs <= 0 || SilenceDurationMs > 10000)
                SilenceDurationMs = 1000;

            if (MaxRecordingMs <= 0 || MaxRecordingMs > 60000)
                MaxRecordingMs = 5000;

            if (ProcessingMode < 0 || ProcessingMode > 2)
                ProcessingMode = 0;

            if (WindowWidth < 400)
                WindowWidth = 800;

            if (WindowHeight < 300)
                WindowHeight = 600;
        }

        /// <summary>
        /// Создание копии настроек
        /// </summary>
        public UserSettings Clone()
        {
            return new UserSettings
            {
                SelectedAudioDevice = SelectedAudioDevice,
                SourceLanguage = SourceLanguage,
                TargetLanguage = TargetLanguage,
                VoiceThreshold = VoiceThreshold,
                ProcessingMode = ProcessingMode,
                AutoTranslateAndTTS = AutoTranslateAndTTS,
                SilenceDurationMs = SilenceDurationMs,
                MaxRecordingMs = MaxRecordingMs,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowLocationX = WindowLocationX,
                WindowLocationY = WindowLocationY
            };
        }

        /// <summary>
        /// Получение пути к директории настроек
        /// </summary>
        public static string GetSettingsDirectory()
        {
            return Path.GetDirectoryName(SettingsFilePath)!;
        }

        /// <summary>
        /// Проверка существования файла настроек
        /// </summary>
        public static bool SettingsFileExists()
        {
            return File.Exists(SettingsFilePath);
        }

        /// <summary>
        /// Удаление файла настроек (сброс к настройкам по умолчанию)
        /// </summary>
        public static bool ResetSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    File.Delete(SettingsFilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка сброса настроек: {ex.Message}");
                return false;
            }
        }
    }
}