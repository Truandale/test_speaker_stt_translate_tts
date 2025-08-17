using System.Speech.Synthesis;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Улучшенный TTS класс с логикой из MORT и интеграцией с SmartAudioManager
    /// </summary>
    public class EnhancedTTSEngine : IDisposable
    {
        private SpeechSynthesizer? speechSynthesizer;
        private bool isTTSActive = false;
        private SmartAudioManager? audioManager;
        
        // События
        public event Action? TTSStarted;
        public event Action? TTSCompleted;
        public event Action<string>? TTSError;
        
        public bool IsTTSActive => isTTSActive;

        public EnhancedTTSEngine(SmartAudioManager? smartAudioManager = null)
        {
            audioManager = smartAudioManager;
            InitializeTTS();
        }

        private void InitializeTTS()
        {
            try
            {
                speechSynthesizer = new SpeechSynthesizer();
                speechSynthesizer.SetOutputToDefaultAudioDevice();
                
                // События для отслеживания состояния
                speechSynthesizer.SpeakStarted += (s, e) => 
                {
                    isTTSActive = true;
                    audioManager?.NotifyTTSStarted(); // Уведомляем менеджер
                    TTSStarted?.Invoke();
                    AudioAnalysisUtils.SafeDebugLog("🔊 TTS начат");
                };
                
                speechSynthesizer.SpeakCompleted += (s, e) => 
                {
                    isTTSActive = false;
                    audioManager?.NotifyTTSCompleted(); // Уведомляем менеджер
                    TTSCompleted?.Invoke();
                    AudioAnalysisUtils.SafeDebugLog("✅ TTS завершен");
                };
                
                // Настройки по умолчанию
                speechSynthesizer.Rate = 0; // Нормальная скорость
                speechSynthesizer.Volume = 80; // 80% громкости
                
                AudioAnalysisUtils.SafeDebugLog("✅ TTS инициализирован");
                LogAvailableVoices();
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка инициализации TTS: {ex.Message}");
                TTSError?.Invoke($"Ошибка инициализации TTS: {ex.Message}");
            }
        }

        private void LogAvailableVoices()
        {
            try
            {
                if (speechSynthesizer == null) return;
                
                var voices = speechSynthesizer.GetInstalledVoices();
                AudioAnalysisUtils.SafeDebugLog($"🎤 Найдено {voices.Count} голосов TTS:");
                
                foreach (var voice in voices)
                {
                    var info = voice.VoiceInfo;
                    AudioAnalysisUtils.SafeDebugLog($"  - {info.Name} ({info.Culture.Name}) - {info.Gender}");
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка получения списка голосов: {ex.Message}");
            }
        }

        /// <summary>
        /// Основная функция TTS с логикой из MORT
        /// </summary>
        public async Task<bool> SpeakTextAsync(string text, string? targetLanguage = null)
        {
            try
            {
                if (speechSynthesizer == null)
                {
                    TTSError?.Invoke("TTS не инициализирован");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    AudioAnalysisUtils.SafeDebugLog("⚠️ Пустой текст для TTS");
                    return false;
                }

                // Фильтруем заглушки
                if (AudioAnalysisUtils.IsAudioPlaceholder(text))
                {
                    AudioAnalysisUtils.SafeDebugLog($"🚫 Отфильтровано TTS заглушка: {text}");
                    return false;
                }

                // Ограничиваем длину текста для предотвращения переполнения
                if (text.Length > 300)
                {
                    text = text.Substring(0, 300) + "...";
                    AudioAnalysisUtils.SafeDebugLog($"⚠️ Текст обрезан до 300 символов для безопасности");
                }

                // Определяем язык текста
                bool isEnglish = AudioAnalysisUtils.IsEnglishText(text);
                if (!string.IsNullOrEmpty(targetLanguage))
                {
                    isEnglish = targetLanguage.ToLower().Contains("en");
                }

                AudioAnalysisUtils.SafeDebugLog($"🔊 TTS для текста ({(isEnglish ? "EN" : "RU")}): '{text}'");

                // Выбираем подходящий голос
                SelectBestVoice(isEnglish);

                // Останавливаем предыдущий TTS если активен
                if (isTTSActive)
                {
                    speechSynthesizer.SpeakAsyncCancelAll();
                    await Task.Delay(100); // Небольшая задержка для остановки
                }

                // Запускаем TTS асинхронно
                await Task.Run(() =>
                {
                    try
                    {
                        speechSynthesizer.Speak(text);
                    }
                    catch (Exception ex)
                    {
                        AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка во время TTS: {ex.Message}");
                        throw;
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка TTS: {ex.Message}");
                TTSError?.Invoke($"Ошибка TTS: {ex.Message}");
                isTTSActive = false;
                return false;
            }
        }

        /// <summary>
        /// Выбор лучшего голоса для языка (адаптировано из MORT)
        /// </summary>
        private void SelectBestVoice(bool isEnglish)
        {
            try
            {
                if (speechSynthesizer == null) return;

                var voices = speechSynthesizer.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => v.VoiceInfo)
                    .ToList();

                VoiceInfo? selectedVoice = null;

                if (isEnglish)
                {
                    // Ищем английские голоса
                    selectedVoice = voices.FirstOrDefault(v => 
                        v.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ??
                        voices.FirstOrDefault(v => 
                            v.Name.Contains("David", StringComparison.OrdinalIgnoreCase) ||
                            v.Name.Contains("Zira", StringComparison.OrdinalIgnoreCase) ||
                            v.Name.Contains("Mark", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Ищем русские голоса
                    selectedVoice = voices.FirstOrDefault(v => 
                        v.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) ??
                        voices.FirstOrDefault(v => 
                            v.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                            v.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase));
                }

                if (selectedVoice != null)
                {
                    speechSynthesizer.SelectVoice(selectedVoice.Name);
                    AudioAnalysisUtils.SafeDebugLog($"✅ Выбран голос: {selectedVoice.Name} ({selectedVoice.Culture.Name})");
                }
                else
                {
                    AudioAnalysisUtils.SafeDebugLog($"⚠️ Подходящий голос для языка {(isEnglish ? "EN" : "RU")} не найден, используем голос по умолчанию");
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка выбора голоса: {ex.Message}");
            }
        }

        /// <summary>
        /// Остановка текущего TTS
        /// </summary>
        public void StopSpeaking()
        {
            try
            {
                if (speechSynthesizer != null && isTTSActive)
                {
                    speechSynthesizer.SpeakAsyncCancelAll();
                    isTTSActive = false;
                    AudioAnalysisUtils.SafeDebugLog("🛑 TTS остановлен");
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка остановки TTS: {ex.Message}");
            }
        }

        /// <summary>
        /// Настройка параметров TTS
        /// </summary>
        public void SetTTSParameters(int rate = 0, int volume = 80)
        {
            try
            {
                if (speechSynthesizer == null) return;

                speechSynthesizer.Rate = Math.Max(-10, Math.Min(10, rate));
                speechSynthesizer.Volume = Math.Max(0, Math.Min(100, volume));
                
                AudioAnalysisUtils.SafeDebugLog($"🔧 TTS параметры: скорость={speechSynthesizer.Rate}, громкость={speechSynthesizer.Volume}");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка настройки TTS: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                StopSpeaking();
                speechSynthesizer?.Dispose();
                speechSynthesizer = null;
                AudioAnalysisUtils.SafeDebugLog("🗑️ EnhancedTTSEngine утилизирован");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка утилизации TTS: {ex.Message}");
            }
        }
    }
}