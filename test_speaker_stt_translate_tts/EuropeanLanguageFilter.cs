using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Расширенный фильтр для европейских языков с учетом специфических особенностей
    /// Поддерживает: русский, английский, немецкий, французский, испанский, итальянский, греческий и другие
    /// </summary>
    public static class EuropeanLanguageFilter
    {
        /// <summary>
        /// Debug логирование для европейского фильтра
        /// </summary>
        private static void DebugLogEuropean(string message)
        {
            Debug.WriteLine($"[EUROPEAN_FILTER_DEBUG] {message}");
        }

        #region Европейские знаки препинания

        /// <summary>
        /// Стандартные знаки конца предложения для большинства европейских языков
        /// </summary>
        private static readonly char[] STANDARD_SENTENCE_ENDINGS = { '.', '?', '!' };

        /// <summary>
        /// Многоточие в различных вариантах
        /// </summary>
        private static readonly string[] ELLIPSIS_VARIANTS = { "...", "…", "…." };

        /// <summary>
        /// Испанские парные знаки препинания
        /// </summary>
        private static readonly char[] SPANISH_OPENING_MARKS = { '¿', '¡' };
        private static readonly char[] SPANISH_CLOSING_MARKS = { '?', '!' };

        /// <summary>
        /// Греческий знак вопроса (выглядит как точка с запятой)
        /// </summary>
        private const char GREEK_QUESTION_MARK = ';';

        /// <summary>
        /// Французские кавычки "ёлочки"
        /// </summary>
        private static readonly char[] FRENCH_QUOTES = { '«', '»' };

        /// <summary>
        /// Немецкие кавычки
        /// </summary>
        private static readonly string[] GERMAN_QUOTES = { "\u201E", "\u201C", "\u201A", "\u2018" };

        /// <summary>
        /// Комбинированные знаки препинания
        /// </summary>
        private static readonly string[] COMBINED_PUNCTUATION = 
        {
            "?!", "!?", "?..", "!..", "?!..", "!?..", 
            "?…", "!…", "..", "...", "…"
        };

        #endregion

        #region Определение языка

        /// <summary>
        /// Простое определение возможного языка по символам
        /// </summary>
        private static class LanguageDetector
        {
            public static bool IsLikelyCyrillic(string text) => 
                Regex.IsMatch(text, @"[а-яё]", RegexOptions.IgnoreCase);

            public static bool IsLikelyLatin(string text) => 
                Regex.IsMatch(text, @"[a-z]", RegexOptions.IgnoreCase);

            public static bool IsLikelySpanish(string text) => 
                text.Contains('¿') || text.Contains('¡') || 
                Regex.IsMatch(text, @"[ñáéíóúü]", RegexOptions.IgnoreCase);

            public static bool IsLikelyFrench(string text) => 
                text.Contains('«') || text.Contains('»') || 
                Regex.IsMatch(text, @"[àâäéèêëïîôùûüÿç]", RegexOptions.IgnoreCase);

            public static bool IsLikelyGerman(string text) => 
                Regex.IsMatch(text, @"[äöüß]", RegexOptions.IgnoreCase) ||
                text.Contains("\u201E") || text.Contains("\u201C");

            public static bool IsLikelyGreek(string text) => 
                Regex.IsMatch(text, @"[α-ωΑ-Ω]", RegexOptions.IgnoreCase);
        }

        #endregion

        #region Основной фильтр

        /// <summary>
        /// Проверяет, является ли фраза завершенной с учетом особенностей европейских языков
        /// </summary>
        public static bool IsCompleteSentenceEuropean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var trimmedText = text.Trim();
            if (trimmedText.Length <= 2) return false;

            DebugLogEuropean($"🔍 Анализ европейского предложения: '{text}'");

            // 1. Проверка начала предложения
            if (!StartsCorrectlyEuropean(trimmedText))
            {
                DebugLogEuropean($"🚫 Неправильное начало предложения");
                return false;
            }

            // 2. Проверка конца предложения с учетом языковых особенностей
            if (!EndsCorrectlyEuropean(trimmedText))
            {
                DebugLogEuropean($"🚫 Неправильное окончание предложения");
                return false;
            }

            // 3. Специфические проверки для отдельных языков
            if (!PassesLanguageSpecificChecks(trimmedText))
            {
                DebugLogEuropean($"🚫 Не прошло языко-специфические проверки");
                return false;
            }

            DebugLogEuropean($"✅ Принято как завершенное европейское предложение");
            return true;
        }

        /// <summary>
        /// Проверяет правильность начала предложения для европейских языков
        /// </summary>
        private static bool StartsCorrectlyEuropean(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Пропускаем начальные символы: кавычки, скобки, тире
            int startIndex = SkipPrefixSymbols(text);
            if (startIndex >= text.Length) return false;

            char firstChar = text[startIndex];

            // Испанский: разрешаем начало с ¿ и ¡
            if (SPANISH_OPENING_MARKS.Contains(firstChar))
            {
                DebugLogEuropean($"🔍 Испанский знак в начале: '{firstChar}'");
                return true;
            }

            // Цифры и специальные символы разрешены
            if (char.IsDigit(firstChar) || 
                firstChar == '$' || firstChar == '€' || firstChar == '£' ||
                firstChar == '@' || firstChar == '#')
            {
                DebugLogEuropean($"🔍 Цифра/символ в начале: '{firstChar}'");
                return true;
            }

            // Основная проверка заглавной буквы
            bool isCapital = char.IsUpper(firstChar);
            
            // Исключения для брендов
            if (!isCapital && IsAllowedLowercaseBrand(text, startIndex))
            {
                return true;
            }

            return isCapital;
        }

        /// <summary>
        /// Проверяет правильность окончания предложения для европейских языков
        /// </summary>
        private static bool EndsCorrectlyEuropean(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Убираем завершающие кавычки и скобки
            string cleanEnd = RemoveSuffixSymbols(text);
            if (string.IsNullOrEmpty(cleanEnd)) return false;

            // Греческий: разрешаем ';' как знак вопроса
            if (LanguageDetector.IsLikelyGreek(text))
            {
                if (cleanEnd.EndsWith(GREEK_QUESTION_MARK.ToString()))
                {
                    DebugLogEuropean($"🔍 Греческий знак вопроса: ';'");
                    return true;
                }
            }

            // Испанский: проверяем парные знаки
            if (LanguageDetector.IsLikelySpanish(text))
            {
                if (HasMatchingSpanishPunctuation(text))
                {
                    DebugLogEuropean($"🔍 Корректные испанские парные знаки");
                    return true;
                }
            }

            // Стандартные знаки конца предложения
            char lastChar = cleanEnd[cleanEnd.Length - 1];
            if (STANDARD_SENTENCE_ENDINGS.Contains(lastChar))
            {
                return true;
            }

            // Многоточие
            foreach (var ellipsis in ELLIPSIS_VARIANTS)
            {
                if (cleanEnd.EndsWith(ellipsis))
                {
                    return true;
                }
            }

            // Комбинированные знаки
            foreach (var combo in COMBINED_PUNCTUATION)
            {
                if (cleanEnd.EndsWith(combo))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Языко-специфические проверки
        /// </summary>
        private static bool PassesLanguageSpecificChecks(string text)
        {
            // Французский: проверяем правильность пробелов перед знаками препинания
            if (LanguageDetector.IsLikelyFrench(text))
            {
                return ValidateFrenchPunctuation(text);
            }

            // Немецкий: проверяем заглавные существительные (базово)
            if (LanguageDetector.IsLikelyGerman(text))
            {
                return ValidateGermanCapitalization(text);
            }

            // Испанский: дополнительная проверка парных знаков
            if (LanguageDetector.IsLikelySpanish(text))
            {
                return ValidateSpanishPunctuation(text);
            }

            return true; // Для остальных языков пока без специфических проверок
        }

        #endregion

        #region Вспомогательные методы

        /// <summary>
        /// Пропускает префиксные символы (кавычки, скобки, тире)
        /// </summary>
        private static int SkipPrefixSymbols(string text)
        {
            int index = 0;
            while (index < text.Length)
            {
                char ch = text[index];
                
                if (ch == '«' || ch == '»' || ch == '"' || ch == '\'' || 
                    ch == '(' || ch == ')' || ch == '[' || ch == ']' ||
                    ch == '{' || ch == '}' || ch == '—' || ch == '–' || ch == '-' ||
                    ch == ' ' || ch == '\t' || ch == '\u201E' || ch == '\u201C' || ch == '\u201A' || ch == '\u2018')
                {
                    index++;
                    continue;
                }

                // Многоточие в начале
                if ((ch == '.' && index + 2 < text.Length && 
                     text[index + 1] == '.' && text[index + 2] == '.') ||
                    ch == '…')
                {
                    index += (ch == '…') ? 1 : 3;
                    continue;
                }

                break;
            }
            return index;
        }

        /// <summary>
        /// Убирает суффиксные символы (кавычки, скобки)
        /// </summary>
        private static string RemoveSuffixSymbols(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var result = text.TrimEnd();
            
            // Убираем завершающие кавычки и скобки
            while (result.Length > 0)
            {
                char lastChar = result[result.Length - 1];
                if (lastChar == '»' || lastChar == '"' || lastChar == '\'' || 
                    lastChar == ')' || lastChar == ']' || lastChar == '}' ||
                    lastChar == '\u201C' || lastChar == '\u2018' || lastChar == ' ')
                {
                    result = result.Substring(0, result.Length - 1);
                    continue;
                }
                break;
            }

            return result;
        }

        /// <summary>
        /// Проверяет разрешенные бренды с маленькой буквы
        /// </summary>
        private static bool IsAllowedLowercaseBrand(string text, int startIndex)
        {
            string restOfText = text.Substring(startIndex).ToLower();
            string[] allowedBrands = 
            { 
                "iphone", "ipad", "ios", "macbook", "imac",
                "ebay", "etsy", "android", "gmail", "youtube",
                "facebook", "twitter", "instagram", "linkedin"
            };

            foreach (var brand in allowedBrands)
            {
                if (restOfText.StartsWith(brand))
                {
                    DebugLogEuropean($"🔍 Разрешенный бренд: '{brand}'");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Проверяет парные испанские знаки препинания
        /// </summary>
        private static bool HasMatchingSpanishPunctuation(string text)
        {
            // Ищем ¿...? и ¡...!
            bool hasQuestionPair = text.Contains('¿') && text.Contains('?');
            bool hasExclamationPair = text.Contains('¡') && text.Contains('!');
            
            // Если есть испанские открывающие знаки, должны быть и закрывающие
            if (text.Contains('¿') && !text.Contains('?')) return false;
            if (text.Contains('¡') && !text.Contains('!')) return false;

            return hasQuestionPair || hasExclamationPair || 
                   (!text.Contains('¿') && !text.Contains('¡')); // Или их вообще нет
        }

        /// <summary>
        /// Валидация французской пунктуации (пробелы перед знаками)
        /// </summary>
        private static bool ValidateFrenchPunctuation(string text)
        {
            // Во французском перед : ; ! ? ставится узкий неразрывный пробел
            // Мы проверяем наличие пробела (любого) для упрощения
            
            // Если есть эти знаки, проверяем пробелы перед ними (но не строго)
            char[] frenchMarks = { ':', ';', '!', '?' };
            
            foreach (char mark in frenchMarks)
            {
                int index = text.IndexOf(mark);
                if (index > 0)
                {
                    char prevChar = text[index - 1];
                    // Во французском должен быть пробел, но мы не будем строго требовать
                    // Просто логируем для информации
                    if (prevChar != ' ')
                    {
                        DebugLogEuropean($"🔍 Французский знак '{mark}' без пробела");
                    }
                }
            }

            return true; // Не фильтруем, просто анализируем
        }

        /// <summary>
        /// Валидация немецкой капитализации (упрощенная)
        /// </summary>
        private static bool ValidateGermanCapitalization(string text)
        {
            // В немецком все существительные пишутся с заглавной буквы
            // Мы делаем только базовую проверку - не фильтруем строго
            
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int capitalizedWords = words.Count(w => w.Length > 0 && char.IsUpper(w[0]));
            
            DebugLogEuropean($"🔍 Немецкий: {capitalizedWords}/{words.Length} слов с заглавной");
            
            return true; // Не фильтруем, так как это сложно определить точно
        }

        /// <summary>
        /// Валидация испанской пунктуации
        /// </summary>
        private static bool ValidateSpanishPunctuation(string text)
        {
            return HasMatchingSpanishPunctuation(text);
        }

        #endregion

        #region Интеграция с основным фильтром

        /// <summary>
        /// Расширенная проверка незавершенности фраз с учетом европейских языков
        /// </summary>
        public static bool IsIncompletePhrase(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            // Сначала стандартная проверка
            bool standardIncomplete = AdvancedSpeechFilter.IsIncompletePhrase(text);
            
            // Если стандартная проверка прошла, проверяем европейские особенности
            if (!standardIncomplete)
            {
                return !IsCompleteSentenceEuropean(text);
            }

            return standardIncomplete;
        }

        /// <summary>
        /// Улучшенная версия основного фильтра с европейской поддержкой
        /// </summary>
        public static bool IsValidEuropeanSpeech(string text, float[]? audioSamples = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Основная фильтрация через AdvancedSpeechFilter
            bool basicValid = AdvancedSpeechFilter.IsValidSpeechQuick(text);
            
            if (!basicValid) return false;

            // Дополнительная европейская проверка завершенности
            if (IsIncompletePhrase(text))
            {
                DebugLogEuropean($"🚫 Европейская проверка: незавершенная фраза '{text}'");
                return false;
            }

            DebugLogEuropean($"✅ Принято европейским фильтром: '{text}'");
            return true;
        }

        /// <summary>
        /// Статистика поддерживаемых языков
        /// </summary>
        public static string GetSupportedLanguages()
        {
            return "Поддерживаемые языки: " +
                   "🇷🇺 Русский (кириллица), " +
                   "🇬🇧 Английский, " +
                   "🇩🇪 Немецкий (заглавные существительные), " +
                   "🇫🇷 Французский (пробелы перед :;!?), " +
                   "🇪🇸 Испанский (¿¡ парные знаки), " +
                   "🇮🇹 Итальянский, " +
                   "🇬🇷 Греческий (; как ?), " +
                   "🇪🇺 + другие европейские";
        }

        #endregion
    }
}