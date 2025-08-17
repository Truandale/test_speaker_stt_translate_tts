using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Умное разбиение текста на предложения с учетом контекста и смысла
    /// Адаптировано из логики MAS (MORT Audio Settings)
    /// </summary>
    public static class SmartTextSplitter
    {
        /// <summary>
        /// Максимальная длина одного предложения для перевода
        /// </summary>
        private const int MAX_SENTENCE_LENGTH = 200;

        /// <summary>
        /// Минимальная длина предложения для обработки
        /// </summary>
        private const int MIN_SENTENCE_LENGTH = 5;

        /// <summary>
        /// Максимальная длина текста для обработки целиком
        /// </summary>
        private const int MAX_TEXT_LENGTH = 300;

        /// <summary>
        /// Знаки окончания предложений
        /// </summary>
        private static readonly char[] SENTENCE_ENDINGS = { '.', '!', '?' };

        /// <summary>
        /// Знаки паузы внутри предложений
        /// </summary>
        private static readonly char[] PAUSE_MARKS = { ',', ';', ':', '-', '–', '—' };

        /// <summary>
        /// Сокращения, которые не должны разбивать предложения
        /// </summary>
        private static readonly HashSet<string> ABBREVIATIONS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "Ltd", "Inc", "Corp", "Co",
            "vs", "etc", "i.e", "e.g", "a.m", "p.m", "U.S", "U.K", "EU", "USA", "UK",
            "г-н", "г-жа", "др", "проф", "см", "стр", "гл", "т.д", "т.п", "т.к", "т.е"
        };

        /// <summary>
        /// Основной метод для умного разбиения текста на предложения
        /// </summary>
        public static List<string> SplitIntoSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            text = text.Trim();

            // Если текст короткий, возвращаем как есть
            if (text.Length <= MAX_TEXT_LENGTH)
            {
                return new List<string> { text };
            }

            AudioAnalysisUtils.SafeDebugLog($"🔄 Разбиваем длинный текст на предложения: {text.Length} символов");

            var sentences = SplitWithContextAwareness(text);

            // Если умное разбиение не сработало, используем простое
            if (sentences.Count <= 1)
            {
                sentences = SplitSimple(text);
            }

            // Объединяем слишком короткие предложения
            sentences = MergeShortSentences(sentences);

            // Разбиваем слишком длинные предложения
            sentences = SplitLongSentences(sentences);

            AudioAnalysisUtils.SafeDebugLog($"📝 Получилось {sentences.Count} предложений для обработки");

            return sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        /// <summary>
        /// Умное разбиение с учетом контекста и особенностей Whisper.NET
        /// </summary>
        private static List<string> SplitWithContextAwareness(string text)
        {
            var sentences = new List<string>();
            var currentSentence = new StringBuilder();

            // Whisper.NET расставляет знаки препинания в конце предложений
            // Мы должны доверять этим знакам и не разрывать предложения
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                currentSentence.Append(c);

                // Проверяем на конец предложения (знаки от Whisper надежны)
                if (SENTENCE_ENDINGS.Contains(c))
                {
                    // Whisper.NET знает где заканчиваются предложения - доверяем ему
                    if (IsValidWhisperSentenceEnd(text, i, currentSentence.ToString()))
                    {
                        string sentence = currentSentence.ToString().Trim();
                        if (sentence.Length >= MIN_SENTENCE_LENGTH)
                        {
                            sentences.Add(sentence);
                            currentSentence.Clear();
                        }
                    }
                }
            }

            // Добавляем последнее предложение, если есть
            if (currentSentence.Length > 0)
            {
                string lastSentence = currentSentence.ToString().Trim();
                if (lastSentence.Length >= MIN_SENTENCE_LENGTH)
                {
                    sentences.Add(lastSentence);
                }
            }

            return sentences;
        }

        /// <summary>
        /// Проверяет, является ли знак препинания действительным концом предложения от Whisper
        /// </summary>
        private static bool IsValidWhisperSentenceEnd(string text, int position, string currentSentence)
        {
            char currentChar = text[position];
            
            // Whisper ставит точку, вопросительный и восклицательный знаки в конце предложений
            // Мы доверяем этому, но проверяем очевидные исключения
            
            // Проверяем на очевидные сокращения (более строго)
            if (currentChar == '.' && IsCommonAbbreviation(currentSentence))
                return false;

            // Проверяем следующий символ после знака препинания
            if (position + 1 < text.Length)
            {
                char nextChar = text[position + 1];
                
                // После конца предложения обычно идет пробел и заглавная буква
                // или конец текста
                if (char.IsWhiteSpace(nextChar))
                {
                    // Ищем следующий не-пробельный символ
                    for (int j = position + 1; j < text.Length; j++)
                    {
                        if (!char.IsWhiteSpace(text[j]))
                        {
                            // Если следующее слово начинается с заглавной буквы - это новое предложение
                            return char.IsUpper(text[j]) || char.IsDigit(text[j]);
                        }
                    }
                    // Если после пробелов ничего нет - это конец текста
                    return true;
                }
                
                // Если сразу после знака идет заглавная буква
                return char.IsUpper(nextChar);
            }
            
            // Если это конец текста - определенно конец предложения
            return true;
        }

        /// <summary>
        /// Проверяет на распространенные сокращения
        /// </summary>
        private static bool IsCommonAbbreviation(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            // Ищем последнее слово перед точкой
            var words = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            
            string lastWord = words[words.Length - 1].TrimEnd('.');
            
            // Список распространенных сокращений
            var commonAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mr", "mrs", "ms", "dr", "prof", "inc", "ltd", "corp", "co",
                "etc", "vs", "ie", "eg", "cf", "et", "al", "ca", "approx",
                "г", "гр", "тов", "им", "ул", "д", "кв", "стр", "корп"
            };
            
            return commonAbbreviations.Contains(lastWord);
        }

        /// <summary>
        /// Проверяет, является ли точка реальным концом предложения (старый метод для совместимости)
        /// </summary>
        private static bool IsActualSentenceEnd(string text, int position, string currentSentence)
        {
            // Проверяем на сокращения
            if (IsAbbreviation(currentSentence))
                return false;

            // Проверяем следующий символ
            if (position + 1 < text.Length)
            {
                char nextChar = text[position + 1];

                // Пропускаем пробелы
                int nextNonSpacePos = position + 1;
                while (nextNonSpacePos < text.Length && char.IsWhiteSpace(text[nextNonSpacePos]))
                {
                    nextNonSpacePos++;
                }

                if (nextNonSpacePos < text.Length)
                {
                    char nextNonSpaceChar = text[nextNonSpacePos];

                    // Если следующий символ заглавная буква или начало нового предложения
                    return char.IsUpper(nextNonSpaceChar) || char.IsDigit(nextNonSpaceChar);
                }
            }

            return true; // Конец текста
        }

        /// <summary>
        /// Проверяет, заканчивается ли предложение сокращением
        /// </summary>
        private static bool IsAbbreviation(string sentence)
        {
            if (string.IsNullOrEmpty(sentence) || sentence.Length < 2)
                return false;

            // Получаем последнее слово перед точкой
            var words = sentence.TrimEnd('.', '!', '?').Split(new char[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return false;

            string lastWord = words[words.Length - 1];

            return ABBREVIATIONS.Contains(lastWord);
        }

        /// <summary>
        /// Простое разбиение по знакам препинания
        /// </summary>
        private static List<string> SplitSimple(string text)
        {
            var sentences = new List<string>();
            var parts = text.Split(SENTENCE_ENDINGS, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length >= MIN_SENTENCE_LENGTH)
                {
                    sentences.Add(trimmed);
                }
            }

            return sentences;
        }

        /// <summary>
        /// Объединяет слишком короткие предложения с соседними
        /// </summary>
        private static List<string> MergeShortSentences(List<string> sentences)
        {
            var merged = new List<string>();

            for (int i = 0; i < sentences.Count; i++)
            {
                string current = sentences[i];

                // Если предложение слишком короткое, пытаемся объединить
                if (current.Length < MIN_SENTENCE_LENGTH * 2 && i + 1 < sentences.Count)
                {
                    string next = sentences[i + 1];
                    if (current.Length + next.Length < MAX_SENTENCE_LENGTH)
                    {
                        // Объединяем с следующим
                        merged.Add($"{current} {next}");
                        i++; // Пропускаем следующее предложение
                        continue;
                    }
                }

                merged.Add(current);
            }

            return merged;
        }

        /// <summary>
        /// Разбивает слишком длинные предложения на более короткие
        /// </summary>
        private static List<string> SplitLongSentences(List<string> sentences)
        {
            var result = new List<string>();

            foreach (string sentence in sentences)
            {
                if (sentence.Length <= MAX_SENTENCE_LENGTH)
                {
                    result.Add(sentence);
                    continue;
                }

                // Пытаемся разбить по знакам паузы
                var parts = SplitByPauseMarks(sentence);
                if (parts.Count > 1)
                {
                    result.AddRange(parts);
                }
                else
                {
                    // Если не получилось, разбиваем по словам
                    result.AddRange(SplitByWords(sentence));
                }
            }

            return result;
        }

        /// <summary>
        /// Разбивает предложение по знакам паузы (запятые, точки с запятой и т.д.)
        /// </summary>
        private static List<string> SplitByPauseMarks(string sentence)
        {
            var parts = new List<string>();
            var currentPart = new StringBuilder();

            foreach (char c in sentence)
            {
                currentPart.Append(c);

                if (PAUSE_MARKS.Contains(c) && currentPart.Length >= MIN_SENTENCE_LENGTH * 2)
                {
                    parts.Add(currentPart.ToString().Trim());
                    currentPart.Clear();
                }
            }

            // Добавляем остаток
            if (currentPart.Length > 0)
            {
                string remaining = currentPart.ToString().Trim();
                if (remaining.Length >= MIN_SENTENCE_LENGTH)
                {
                    parts.Add(remaining);
                }
                else if (parts.Count > 0)
                {
                    // Объединяем с последней частью если остаток слишком короткий
                    parts[parts.Count - 1] += " " + remaining;
                }
            }

            return parts;
        }

        /// <summary>
        /// Разбивает предложение по словам при достижении максимальной длины
        /// </summary>
        private static List<string> SplitByWords(string sentence)
        {
            var parts = new List<string>();
            var words = sentence.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var currentPart = new StringBuilder();

            foreach (string word in words)
            {
                if (currentPart.Length + word.Length + 1 > MAX_SENTENCE_LENGTH && currentPart.Length > 0)
                {
                    parts.Add(currentPart.ToString().Trim());
                    currentPart.Clear();
                }

                if (currentPart.Length > 0)
                    currentPart.Append(" ");
                currentPart.Append(word);
            }

            // Добавляем последнюю часть
            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString().Trim());
            }

            return parts;
        }

        /// <summary>
        /// Проверяет, нужно ли разбивать текст
        /// </summary>
        public static bool ShouldSplit(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && text.Length > MAX_TEXT_LENGTH;
        }

        /// <summary>
        /// Получает оптимальный размер части для обработки
        /// </summary>
        public static int GetOptimalChunkSize(string text, int maxChunks = 5)
        {
            if (string.IsNullOrWhiteSpace(text))
                return MAX_SENTENCE_LENGTH;

            int optimalSize = Math.Max(MIN_SENTENCE_LENGTH * 4, text.Length / maxChunks);
            return Math.Min(optimalSize, MAX_SENTENCE_LENGTH);
        }

        /// <summary>
        /// Статистика разбиения текста
        /// </summary>
        public static class SplitStats
        {
            public static void LogSplitResults(string originalText, List<string> sentences)
            {
                if (sentences.Count <= 1) return;

                AudioAnalysisUtils.SafeDebugLog($"📊 Статистика разбиения:");
                AudioAnalysisUtils.SafeDebugLog($"   Исходный текст: {originalText.Length} символов");
                AudioAnalysisUtils.SafeDebugLog($"   Предложений: {sentences.Count}");
                AudioAnalysisUtils.SafeDebugLog($"   Средняя длина: {sentences.Average(s => s.Length):F1} символов");
                AudioAnalysisUtils.SafeDebugLog($"   Мин/Макс: {sentences.Min(s => s.Length)}/{sentences.Max(s => s.Length)} символов");
            }
        }

        /// <summary>
        /// Переводит длинный текст по частям для предотвращения JSON ошибок
        /// Адаптировано из MORT MegaAudioSettings.TranslateLongTextInParts
        /// </summary>
        public static async Task<string> TranslateLongTextInParts(string longText, 
            Func<string, string, string, Task<string>> translateFunction,
            string sourceLanguage, string targetLanguage)
        {
            try
            {
                AudioAnalysisUtils.SafeDebugLog($"🔄 [SmartSplitter] Разбиваем длинный текст на предложения: {longText.Length} символов");

                // Используем существующую умную разбивку
                var sentences = SplitIntoSentences(longText);

                AudioAnalysisUtils.SafeDebugLog($"📝 [SmartSplitter] Получилось {sentences.Count} предложений для перевода");

                // 🎯 УЛУЧШЕНИЕ: если предложений мало (2-3), переводим целиком для сохранения контекста
                if (sentences.Count <= 3 && longText.Length < 800)
                {
                    AudioAnalysisUtils.SafeDebugLog($"📝 [SmartSplitter] Мало предложений ({sentences.Count}), переводим целиком для максимального сохранения контекста");
                    return await translateFunction(longText, sourceLanguage, targetLanguage);
                }

                if (sentences.Count <= 1)
                {
                    // Если разбивка не дала результата, используем обычный перевод
                    AudioAnalysisUtils.SafeDebugLog($"📝 [SmartSplitter] Одно предложение или неудачная разбивка, обычный перевод: {longText.Length} символов");
                    return await translateFunction(longText, sourceLanguage, targetLanguage);
                }

                // 🔗 УЛУЧШЕНИЕ: группируем полные предложения для сохранения контекста
                var contextualGroups = GroupSentencesForContext(sentences);
                AudioAnalysisUtils.SafeDebugLog($"📝 [SmartSplitter] Сгруппировано в {contextualGroups.Count} групп полных предложений");

                var translatedParts = new List<string>();

                // Переводим каждую контекстную группу отдельно
                for (int i = 0; i < contextualGroups.Count; i++)
                {
                    string group = contextualGroups[i];
                    string preview = group.Length > 80 ? group.Substring(0, 77) + "..." : group;

                    AudioAnalysisUtils.SafeDebugLog($"🔄 [SmartSplitter] Переводим группу {i + 1}/{contextualGroups.Count}: '{preview}'");

                    try
                    {
                        string partResult = await translateFunction(group, sourceLanguage, targetLanguage);
                        
                        if (!string.IsNullOrEmpty(partResult) && !partResult.Contains("[Ошибка]"))
                        {
                            translatedParts.Add(partResult.Trim());
                            string resultPreview = partResult.Length > 80 ? partResult.Substring(0, 77) + "..." : partResult;
                            AudioAnalysisUtils.SafeDebugLog($"✅ [SmartSplitter] Группа {i + 1} переведена: '{resultPreview}'");
                        }
                        else
                        {
                            AudioAnalysisUtils.SafeDebugLog($"❌ [SmartSplitter] Группа {i + 1} не переведена, используем оригинал");
                            translatedParts.Add(group); // Добавляем оригинал если перевод не удался
                        }

                        // Небольшая задержка между запросами к API для предотвращения rate limiting
                        await Task.Delay(150);
                    }
                    catch (Exception partEx)
                    {
                        AudioAnalysisUtils.SafeDebugLog($"❌ [SmartSplitter] Ошибка перевода группы {i + 1}: {partEx.Message}");
                        translatedParts.Add(group); // Добавляем оригинал при ошибке
                    }
                }

                // Объединяем переведенные части с правильными разделителями
                string finalResult = string.Join(" ", translatedParts);

                string finalPreview = finalResult.Length > 100 ? finalResult.Substring(0, 97) + "..." : finalResult;
                AudioAnalysisUtils.SafeDebugLog($"✅ [SmartSplitter] Длинный текст переведен по частям: '{finalPreview}' ({finalResult.Length} символов)");
                
                return finalResult;
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ [SmartSplitter] Критическая ошибка перевода длинного текста: {ex.Message}");
                return $"[Ошибка перевода длинного текста] {longText}";
            }
        }

        /// <summary>
        /// Группирует предложения для сохранения контекста при переводе
        /// </summary>
        private static List<string> GroupSentencesForContext(List<string> sentences)
        {
            var groups = new List<string>();
            var currentGroup = new List<string>();
            int currentLength = 0;
            
            foreach (var sentence in sentences)
            {
                var trimmedSentence = sentence.Trim();
                if (string.IsNullOrEmpty(trimmedSentence)) continue;
                
                // Если добавление этого предложения превысит лимит в 800 символов
                // или текущая группа уже содержит 3 предложения
                if ((currentLength + trimmedSentence.Length > 800 && currentGroup.Count > 0) 
                    || currentGroup.Count >= 3)
                {
                    // Завершаем текущую группу
                    if (currentGroup.Count > 0)
                    {
                        groups.Add(string.Join(" ", currentGroup));
                        currentGroup.Clear();
                        currentLength = 0;
                    }
                }
                
                // Добавляем предложение в текущую группу
                currentGroup.Add(trimmedSentence);
                currentLength += trimmedSentence.Length + 1; // +1 для пробела
            }
            
            // Добавляем последнюю группу, если она не пуста
            if (currentGroup.Count > 0)
            {
                groups.Add(string.Join(" ", currentGroup));
            }
            
            AudioAnalysisUtils.SafeDebugLog($"📝 [SmartSplitter] Группировка: {sentences.Count} предложений → {groups.Count} групп предложений");
            
            // Подробная информация о группах для отладки
            for (int i = 0; i < groups.Count; i++)
            {
                string groupPreview = groups[i].Length > 60 ? groups[i].Substring(0, 57) + "..." : groups[i];
                AudioAnalysisUtils.SafeDebugLog($"  📋 Группа {i + 1}: {groups[i].Length} символов - '{groupPreview}'");
            }
            
            return groups;
        }
    }
}