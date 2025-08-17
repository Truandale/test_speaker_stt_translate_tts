using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Продвинутый фильтр на основе лучших практик MORT
    /// Заменяет простую фильтрацию на многоуровневую систему анализа
    /// </summary>
    public static class AdvancedSpeechFilter
    {
        // Конфигурация порогов
        private const float MIN_SPEECH_LIKELIHOOD = 0.3f;
        private const int MIN_TEXT_LENGTH = 2;
        private const float MIN_DYNAMIC_RANGE = 1.5f;
        private const float MAX_DYNAMIC_RANGE = 15.0f;
        private const float MIN_CHANGE_RATE = 0.05f;
        private const float MAX_CHANGE_RATE = 0.9f;

        /// <summary>
        /// Строгие технические токены Whisper - всегда фильтруются
        /// </summary>
        private static readonly string[] STRICT_TECHNICAL_TOKENS = {
            "[music]", "[applause]", "[noise]", "[silence]", "[beep]", "[sound]", "[audio]",
            "this is human speech", "this is human", "human speech",
            "[background music]", "[laughter]", "[inaudible]"
        };

        /// <summary>
        /// Эмоциональные маркеры в звездочках - фильтруются
        /// </summary>
        private static readonly string[] EMOTIONAL_MARKERS = {
            "*sigh*", "*laugh*", "*cough*", "*sneeze*", "*yawn*", "*whisper*", "*shout*",
            "*crying*", "*sobbing*", "*giggle*", "*gasp*", "*breath*", "*breathing*"
        };

        /// <summary>
        /// Служебные сообщения системы - не переводятся
        /// </summary>
        private static readonly string[] SYSTEM_MESSAGES = {
            "[Текст не распознан]", "[Ошибка]", "[Fallback]", "[Test]", "[ТЕСТ]",
            "[System]", "Error -", "INVALID_REQUEST", "BadRequest",
            "🔇 Ожидание речи", "🎤 Слушаю", "🔄 Обрабатываю",
            "VOSK recognition", "Windows Speech", "(plug)", "(заглушка)"
        };

        /// <summary>
        /// Основная функция фильтрации - многоуровневый анализ
        /// </summary>
        public static bool IsValidHumanSpeech(string text, float[]? audioSamples = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var cleanText = text.Trim();
            var lowerText = cleanText.ToLower();

            // Уровень 1: Фильтрация строгих технических токенов
            if (IsStrictTechnicalToken(lowerText))
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 [L1] Строгий технический токен: '{text}'");
                return false;
            }

            // Уровень 2: Фильтрация служебных сообщений
            if (IsSystemMessage(lowerText))
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 [L2] Служебное сообщение: '{text}'");
                return false;
            }

            // Уровень 3: Базовые проверки текста
            if (!PassesBasicValidation(cleanText))
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 [L3] Базовая валидация: '{text}'");
                return false;
            }

            // Уровень 4: Анализ аудио характеристик (если доступны)
            if (audioSamples != null)
            {
                float speechLikelihood = AnalyzeSpeechCharacteristics(audioSamples);
                if (speechLikelihood < MIN_SPEECH_LIKELIHOOD)
                {
                    AudioAnalysisUtils.SafeDebugLog($"🚫 [L4] Аудио анализ: speechLikelihood={speechLikelihood:F3} < {MIN_SPEECH_LIKELIHOOD}");
                    return false;
                }
            }

            // Уровень 5: Проверка на реальные слова
            if (!HasRealWords(lowerText))
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 [L5] Нет реальных слов: '{text}'");
                return false;
            }

            // Уровень 6: Проверка на чисто эмоциональные маркеры
            if (IsOnlyEmotionalMarkers(cleanText))
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 [L6] Только эмоциональные маркеры: '{text}'");
                return false;
            }

            AudioAnalysisUtils.SafeDebugLog($"✅ Принят как человеческая речь: '{text}'");
            return true;
        }

        /// <summary>
        /// Уровень 1: Строгие технические токены
        /// </summary>
        private static bool IsStrictTechnicalToken(string lowerText)
        {
            // Точные совпадения с техническими токенами
            if (STRICT_TECHNICAL_TOKENS.Contains(lowerText))
                return true;

            // Полные маркеры в квадратных скобках
            if (lowerText.StartsWith("[") && lowerText.EndsWith("]"))
                return true;

            // Эмоциональные маркеры в звездочках
            if (EMOTIONAL_MARKERS.Contains(lowerText))
                return true;

            // Любой текст в звездочках *текст*
            if (lowerText.StartsWith("*") && lowerText.EndsWith("*") && lowerText.Length > 2)
                return true;

            return false;
        }

        /// <summary>
        /// Уровень 2: Служебные сообщения системы
        /// </summary>
        private static bool IsSystemMessage(string lowerText)
        {
            foreach (var systemMsg in SYSTEM_MESSAGES)
            {
                if (lowerText.Contains(systemMsg.ToLower()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Уровень 3: Базовые проверки текста
        /// </summary>
        private static bool PassesBasicValidation(string text)
        {
            // Минимальная длина (с исключением для цифр)
            if (text.Length < MIN_TEXT_LENGTH && !text.Any(char.IsDigit))
                return false;

            // Фильтруем фразы состоящие только из знаков препинания
            if (text.All(c => !char.IsLetterOrDigit(c)))
                return false;

            // Фильтруем экстремально повторяющиеся символы
            if (text.Length > 1 && text.All(c => c == text[0]))
                return false;

            return true;
        }

        /// <summary>
        /// Уровень 4: Анализ аудио характеристик (адаптировано из MORT)
        /// </summary>
        private static float AnalyzeSpeechCharacteristics(float[] samples)
        {
            try
            {
                if (samples == null || samples.Length < 8) return 0.5f;

                float avgAmplitude = 0;
                float maxAmplitude = 0;
                int changeCount = 0;
                float lastSample = 0;

                foreach (var sample in samples)
                {
                    float amplitude = Math.Abs(sample);
                    avgAmplitude += amplitude;

                    if (amplitude > maxAmplitude)
                        maxAmplitude = amplitude;

                    // Подсчитываем изменения сигнала
                    if (Math.Abs(sample - lastSample) > 0.01f)
                        changeCount++;

                    lastSample = sample;
                }

                avgAmplitude /= samples.Length;

                // Вычисляем показатели речи
                float dynamicRange = maxAmplitude / Math.Max(avgAmplitude, 0.001f);
                float changeRate = (float)changeCount / samples.Length;

                // Человеческая речь имеет определенные характеристики
                float speechScore = 0.0f;

                if (dynamicRange >= MIN_DYNAMIC_RANGE && dynamicRange <= MAX_DYNAMIC_RANGE)
                    speechScore += 0.4f;

                if (changeRate >= MIN_CHANGE_RATE && changeRate <= MAX_CHANGE_RATE)
                    speechScore += 0.4f;

                if (avgAmplitude >= 0.01f && avgAmplitude <= 0.8f)
                    speechScore += 0.2f;

                return speechScore;
            }
            catch
            {
                return 0.5f; // Нейтральное значение при ошибке
            }
        }

        /// <summary>
        /// Уровень 5: Проверка наличия реальных слов
        /// </summary>
        private static bool HasRealWords(string lowerText)
        {
            // Проверяем наличие реальных слов (хотя бы 2 буквы подряд)
            bool hasWords = Regex.IsMatch(lowerText, @"[a-zа-я]{2,}");
            
            // ИЛИ разрешаем числа (любой длины)
            bool hasNumbers = lowerText.Any(char.IsDigit);

            return hasWords || hasNumbers;
        }

        /// <summary>
        /// Упрощенная версия для быстрой проверки (без аудио анализа)
        /// </summary>
        public static bool IsValidSpeechQuick(string text)
        {
            return IsValidHumanSpeech(text, null);
        }

        /// <summary>
        /// Проверка на экстремальные повторяющиеся паттерны
        /// </summary>
        public static bool HasExtremeDuplication(string text, int minWords = 15, int minRepeats = 5)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var words = text.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= minWords) return false;

            var wordGroups = new Dictionary<string, int>();
            
            // Анализируем триграммы (группы из 3 слов)
            for (int i = 0; i < words.Length - 2; i++)
            {
                string trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                if (wordGroups.ContainsKey(trigram))
                    wordGroups[trigram]++;
                else
                    wordGroups[trigram] = 1;
            }

            var mostRepeated = wordGroups.Where(kv => kv.Value >= minRepeats).FirstOrDefault();
            if (mostRepeated.Value >= minRepeats)
            {
                AudioAnalysisUtils.SafeDebugLog($"🚫 Экстремальный повтор: '{mostRepeated.Key}' x{mostRepeated.Value}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Статистика фильтрации для отладки
        /// </summary>
        public static string GetFilterStatistics()
        {
            return $"Конфигурация фильтра: " +
                   $"SpeechLikelihood≥{MIN_SPEECH_LIKELIHOOD}, " +
                   $"MinLength≥{MIN_TEXT_LENGTH}, " +
                   $"DynamicRange=[{MIN_DYNAMIC_RANGE}-{MAX_DYNAMIC_RANGE}], " +
                   $"ChangeRate=[{MIN_CHANGE_RATE}-{MAX_CHANGE_RATE}]";
        }

        /// <summary>
        /// Очищает текст от эмоциональных маркеров в звездочках
        /// </summary>
        public static string CleanEmotionalMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Удаляем все что в звездочках: *sigh*, *laugh*, *любой текст*
            var cleanedText = System.Text.RegularExpressions.Regex.Replace(text, @"\*[^*]*\*", "");
            
            // Удаляем лишние пробелы
            cleanedText = System.Text.RegularExpressions.Regex.Replace(cleanedText, @"\s+", " ").Trim();

            if (cleanedText != text)
            {
                AudioAnalysisUtils.SafeDebugLog($"🧹 Очищен от маркеров: '{text}' → '{cleanedText}'");
            }

            return cleanedText;
        }

        /// <summary>
        /// Проверяет, содержит ли текст только эмоциональные маркеры
        /// </summary>
        public static bool IsOnlyEmotionalMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var cleanedText = CleanEmotionalMarkers(text);
            return string.IsNullOrWhiteSpace(cleanedText);
        }
    }
}