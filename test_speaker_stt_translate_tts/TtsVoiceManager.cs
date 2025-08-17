using System;
using System.Speech.Synthesis;
using System.Collections.Generic;
using System.Linq;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Управляет автоматическим переключением TTS голосов на основе языка текста
    /// Реализация основана на MORT PerformSAPITTS с автоматическим выбором голоса
    /// </summary>
    public class TtsVoiceManager
    {
        private SpeechSynthesizer synthesizer;
        private List<VoiceInfo> englishVoices;
        private List<VoiceInfo> russianVoices;
        private VoiceInfo? currentEnglishVoice;
        private VoiceInfo? currentRussianVoice;

        public TtsVoiceManager(SpeechSynthesizer? existingSynthesizer = null)
        {
            synthesizer = existingSynthesizer ?? new SpeechSynthesizer();
            englishVoices = new List<VoiceInfo>();
            russianVoices = new List<VoiceInfo>();
            
            LoadVoices();
            SelectDefaultVoices();
        }

        /// <summary>
        /// Загружает доступные голоса и разделяет их по языкам
        /// Адаптировано из MORT LoadSAPIVoices()
        /// </summary>
        private void LoadVoices()
        {
            try
            {
                englishVoices.Clear();
                russianVoices.Clear();

                foreach (var voice in synthesizer.GetInstalledVoices())
                {
                    var voiceInfo = voice.VoiceInfo;
                    
                    if (voiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                    {
                        russianVoices.Add(voiceInfo);
                    }
                    else if (voiceInfo.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    {
                        englishVoices.Add(voiceInfo);
                    }
                }

                Console.WriteLine($"[TTS] Загружено голосов: RU={russianVoices.Count}, EN={englishVoices.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Ошибка загрузки голосов: {ex.Message}");
            }
        }

        /// <summary>
        /// Выбирает голоса по умолчанию на основе паттернов из MORT
        /// </summary>
        private void SelectDefaultVoices()
        {
            // Выбор английского голоса с приоритетом по именам из MORT
            currentEnglishVoice = SelectBestVoice(englishVoices, new[] { "david", "zira", "mark", "english" });
            
            // Выбор русского голоса с приоритетом по именам из MORT
            currentRussianVoice = SelectBestVoice(russianVoices, new[] { "irina", "pavel", "russian", "русский" });

            Console.WriteLine($"[TTS] Выбраны голоса по умолчанию:");
            Console.WriteLine($"  EN: {currentEnglishVoice?.Name ?? "не найден"}");
            Console.WriteLine($"  RU: {currentRussianVoice?.Name ?? "не найден"}");
        }

        /// <summary>
        /// Выбирает лучший голос из списка на основе приоритетных имен
        /// Адаптировано из MORT логики выбора голосов
        /// </summary>
        private VoiceInfo? SelectBestVoice(List<VoiceInfo> voices, string[] priorityNames)
        {
            if (!voices.Any()) return null;

            // Поиск по приоритетным именам
            foreach (var priorityName in priorityNames)
            {
                var found = voices.FirstOrDefault(v => 
                    v.Name.ToLower().Contains(priorityName.ToLower()));
                if (found != null) return found;
            }

            // Если не найден приоритетный, возвращаем первый доступный
            return voices.First();
        }

        /// <summary>
        /// Определяет, является ли текст английским
        /// Точная копия из MORT IsEnglishText()
        /// </summary>
        private bool IsEnglishText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Простая эвристика: если большинство символов латинские - считаем английским
            int latinCount = 0;
            int cyrillicCount = 0;

            foreach (char c in text)
            {
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')
                    latinCount++;
                else if (c >= 'А' && c <= 'Я' || c >= 'а' && c <= 'я')
                    cyrillicCount++;
            }

            return latinCount > cyrillicCount;
        }

        /// <summary>
        /// Автоматически выбирает и устанавливает голос на основе языка текста
        /// Реализация основана на MORT PerformSAPITTS логике
        /// </summary>
        public void SelectVoiceForText(string text)
        {
            try
            {
                bool isEnglish = IsEnglishText(text);
                VoiceInfo targetVoice = isEnglish ? currentEnglishVoice : currentRussianVoice;

                if (targetVoice != null)
                {
                    // Устанавливаем голос только если он отличается от текущего
                    if (synthesizer.Voice?.Name != targetVoice.Name)
                    {
                        synthesizer.SelectVoice(targetVoice.Name);
                        Console.WriteLine($"[TTS] Переключен голос: {targetVoice.Name} ({(isEnglish ? "EN" : "RU")})");
                    }
                }
                else
                {
                    Console.WriteLine($"[TTS] Голос для языка {(isEnglish ? "EN" : "RU")} не найден");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Ошибка выбора голоса: {ex.Message}");
            }
        }

        /// <summary>
        /// Воспроизводит текст с автоматическим выбором голоса
        /// </summary>
        public void SpeakWithAutoVoice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                SelectVoiceForText(text);
                synthesizer.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Ошибка воспроизведения: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить информацию о текущих голосах
        /// </summary>
        public string GetVoiceInfo()
        {
            return $"EN: {currentEnglishVoice?.Name ?? "нет"} | RU: {currentRussianVoice?.Name ?? "нет"}";
        }

        /// <summary>
        /// Получить список доступных голосов
        /// </summary>
        public (List<VoiceInfo> English, List<VoiceInfo> Russian) GetAvailableVoices()
        {
            return (englishVoices, russianVoices);
        }

        /// <summary>
        /// Установить конкретный голос для языка
        /// </summary>
        public void SetVoice(VoiceInfo voice, bool isEnglish)
        {
            if (isEnglish)
                currentEnglishVoice = voice;
            else
                currentRussianVoice = voice;

            Console.WriteLine($"[TTS] Установлен голос {voice.Name} для {(isEnglish ? "EN" : "RU")}");
        }

        public void Dispose()
        {
            synthesizer?.Dispose();
        }
    }
}