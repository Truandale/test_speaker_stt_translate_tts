using System.Diagnostics;
using System.Text;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Улучшенные функции для обработки аудио и STT, взятые из MORT
    /// </summary>
    public static class AudioAnalysisUtils
    {
        /// <summary>
        /// Анализ характеристик речи для фильтрации аудио (из MORT)
        /// </summary>
        public static float AnalyzeSpeechCharacteristics(byte[] buffer, int bytesRecorded)
        {
            try
            {
                if (bytesRecorded < 8) return 0.0f;
                
                // Анализируем амплитуду и частотные характеристики
                float avgAmplitude = 0;
                float maxAmplitude = 0;
                int changeCount = 0;
                short lastSample = 0;
                
                for (int i = 0; i < bytesRecorded - 1; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    float amplitude = Math.Abs(sample) / 32768.0f;
                    
                    avgAmplitude += amplitude;
                    if (amplitude > maxAmplitude) maxAmplitude = amplitude;
                    
                    // Подсчитываем изменения сигнала (для определения речевых характеристик)
                    if (Math.Abs(sample - lastSample) > 500)
                        changeCount++;
                    
                    lastSample = sample;
                }
                
                int sampleCount = bytesRecorded / 2;
                avgAmplitude /= sampleCount;
                
                // Вычисляем показатели речи
                float dynamicRange = maxAmplitude / Math.Max(avgAmplitude, 0.001f); // Динамический диапазон
                float changeRate = (float)changeCount / sampleCount; // Частота изменений
                
                // Человеческая речь имеет определенные характеристики:
                // - Динамический диапазон: 2-10
                // - Частота изменений: 0.1-0.8
                float speechScore = 0.0f;
                
                if (dynamicRange >= 1.5f && dynamicRange <= 15.0f)
                    speechScore += 0.4f;
                
                if (changeRate >= 0.05f && changeRate <= 0.9f)
                    speechScore += 0.4f;
                
                if (avgAmplitude >= 0.01f && avgAmplitude <= 0.8f)
                    speechScore += 0.2f;
                
                return speechScore;
            }
            catch
            {
                return 0.0f;
            }
        }

        /// <summary>
        /// Определение языка текста (упрощенная версия из MORT)
        /// </summary>
        public static bool IsEnglishText(string text)
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
        /// Фильтрация аудио заглушек и мусора (из MORT)
        /// </summary>
        public static bool IsAudioPlaceholder(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            
            // Список известных заглушек Whisper
            var placeholders = new[]
            {
                "пшеница", "подписаться", "спасибо за просмотр",
                "thank you for watching", "subscribe", "like and subscribe",
                "музыка", "аплодисменты", "смех", "music", "applause", "laughter"
            };
            
            string lowerText = text.ToLower().Trim();
            
            // Проверяем точные совпадения
            if (placeholders.Contains(lowerText)) return true;
            
            // Проверяем очень короткий текст
            if (lowerText.Length <= 2) return true;
            
            // Проверяем повторяющиеся символы
            if (lowerText.All(c => c == lowerText[0])) return true;
            
            return false;
        }

        /// <summary>
        /// Продвинутая логика для определения голосовой активности (из MORT)
        /// </summary>
        public static bool IsVoiceActivity(float audioLevel, float threshold, float speechLikelihood = 1.0f)
        {
            // Базовая проверка уровня
            if (audioLevel <= threshold) return false;
            
            // Дополнительная проверка на речевые характеристики
            if (speechLikelihood < 0.3f) return false;
            
            return true;
        }

        /// <summary>
        /// Очистка результата перевода от служебных символов (из MORT)
        /// </summary>
        public static string CleanTranslationResult(string result)
        {
            if (string.IsNullOrEmpty(result)) return result;
            
            // Убираем служебные маркеры
            string cleaned = result.Replace("【===_TRANS_===】", "").Trim();
            
            // Убираем лишние пробелы
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            
            return cleaned;
        }

        /// <summary>
        /// Разбивка длинного текста на части для перевода (из MORT)
        /// </summary>
        public static List<string> SplitTextForTranslation(string longText, int maxLength = 200)
        {
            var parts = new List<string>();
            
            if (string.IsNullOrEmpty(longText)) return parts;
            
            if (longText.Length <= maxLength)
            {
                parts.Add(longText);
                return parts;
            }
            
            // Разбиваем по предложениям
            var sentences = longText.Split(new char[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            var currentPart = new StringBuilder();
            
            foreach (string sentence in sentences)
            {
                string trimmedSentence = sentence.Trim();
                if (string.IsNullOrEmpty(trimmedSentence)) continue;
                
                // Добавляем знак препинания обратно
                string sentenceWithPunc = trimmedSentence;
                if (!sentenceWithPunc.EndsWith(".") && !sentenceWithPunc.EndsWith("!") && !sentenceWithPunc.EndsWith("?"))
                {
                    sentenceWithPunc += ".";
                }
                
                if (currentPart.Length + sentenceWithPunc.Length > maxLength)
                {
                    if (currentPart.Length > 0)
                    {
                        parts.Add(currentPart.ToString().Trim());
                        currentPart.Clear();
                    }
                }
                
                currentPart.Append(sentenceWithPunc).Append(" ");
            }
            
            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString().Trim());
            }
            
            return parts;
        }

        /// <summary>
        /// Безопасное логирование для отладки (из MORT)
        /// </summary>
        public static void SafeDebugLog(string message)
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch
            {
                // Игнорируем ошибки логирования
            }
        }
    }
}