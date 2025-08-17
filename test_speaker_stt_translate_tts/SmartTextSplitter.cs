using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
        /// Умное разбиение с учетом контекста
        /// </summary>
        private static List<string> SplitWithContextAwareness(string text)
        {
            var sentences = new List<string>();
            var currentSentence = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                currentSentence.Append(c);

                // Проверяем на конец предложения
                if (SENTENCE_ENDINGS.Contains(c))
                {
                    if (IsActualSentenceEnd(text, i, currentSentence.ToString()))
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

            // Добавляем последнее предложение
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
        /// Проверяет, является ли точка реальным концом предложения
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
    }
}