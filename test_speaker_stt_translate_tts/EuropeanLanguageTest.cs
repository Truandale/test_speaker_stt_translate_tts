using System;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Тестирование фильтрации для европейских языков
    /// Демонстрирует поддержку различных языковых особенностей
    /// </summary>
    public static class EuropeanLanguageTest
    {
        /// <summary>
        /// Запускает полное тестирование европейских языков
        /// </summary>
        public static void RunAllTests()
        {
            Console.WriteLine("🇪🇺 === ТЕСТИРОВАНИЕ ЕВРОПЕЙСКИХ ЯЗЫКОВ ===");
            Console.WriteLine();

            TestRussianSentences();
            TestEnglishSentences();
            TestGermanSentences();
            TestFrenchSentences();
            TestSpanishSentences();
            TestItalianSentences();
            TestGreekSentences();
            TestIncompleteExamples();

            Console.WriteLine("✅ Все тесты европейских языков завершены!");
            Console.WriteLine($"📊 {EuropeanLanguageFilter.GetSupportedLanguages()}");
        }

        /// <summary>
        /// Тестирование русских предложений
        /// </summary>
        private static void TestRussianSentences()
        {
            Console.WriteLine("🇷🇺 === РУССКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Привет, как дела?", true, "стандартное предложение"),
                ("Это очень интересная книга!", true, "восклицательное предложение"),
                ("Мне нужно подумать...", true, "предложение с многоточием"),
                ("«Что ты сказал?» — спросил он.", true, "прямая речь с кавычками"),
                ("iPhone работает отлично.", true, "бренд с маленькой буквы"),
                ("5 минут назад я видел его.", true, "начало с цифры"),
                ("$100 — это много денег.", true, "начало с символа валюты"),

                // Некорректные (незавершенные)
                ("привет как дела", false, "нет заглавной буквы"),
                ("Это незавершенная фраза", false, "нет знака завершения"),
                ("что мы делаем", false, "нет заглавной и знака завершения"),
                ("и это не человеческая речь", false, "незавершенная фраза из логов"),
                ("Я забыли ж", false, "обрезанная фраза"),
                ("...choppers from a church", false, "фрагмент с многоточием в начале"),
                ("what we do", false, "английская незавершенная фраза")
            };

            RunLanguageTest("Русский", testCases);
        }

        /// <summary>
        /// Тестирование английских предложений
        /// </summary>
        private static void TestEnglishSentences()
        {
            Console.WriteLine("🇬🇧 === АНГЛИЙСКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Hello, how are you?", true, "стандартный вопрос"),
                ("This is a great book!", true, "восклицательное предложение"),
                ("I need to think about it...", true, "предложение с многоточием"),
                ("eBay is a popular marketplace.", true, "бренд с маленькой буквы"),
                ("10 years ago, everything was different.", true, "начало с цифры"),

                // Некорректные
                ("hello how are you", false, "нет заглавной буквы"),
                ("This is incomplete", false, "нет знака завершения"),
                ("what we do", false, "незавершенная фраза"),
                ("and this is not human speech", false, "незавершенная фраза")
            };

            RunLanguageTest("Английский", testCases);
        }

        /// <summary>
        /// Тестирование немецких предложений
        /// </summary>
        private static void TestGermanSentences()
        {
            Console.WriteLine("🇩🇪 === НЕМЕЦКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Hallo, wie geht es dir?", true, "стандартный вопрос"),
                ("Das ist ein sehr interessantes Buch!", true, "с заглавными существительными"),
                ("Ich möchte darüber nachdenken...", true, "с умлаутами"),
                ("Der Hund ist sehr groß.", true, "стандартное предложение"),

                // Некорректные
                ("hallo wie geht es", false, "нет заглавной буквы"),
                ("Das ist unvollständig", false, "нет знака завершения")
            };

            RunLanguageTest("Немецкий", testCases);
        }

        /// <summary>
        /// Тестирование французских предложений
        /// </summary>
        private static void TestFrenchSentences()
        {
            Console.WriteLine("🇫🇷 === ФРАНЦУЗСКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Bonjour, comment allez-vous ?", true, "с пробелом перед ?"),
                ("C'est un livre très intéressant !", true, "с пробелом перед !"),
                ("Je dois réfléchir à cela...", true, "с многоточием"),
                ("« Qu'est-ce que tu dis ? » demanda-t-il.", true, "французские кавычки"),

                // Некорректные
                ("bonjour comment allez vous", false, "нет заглавной буквы"),
                ("C'est incomplet", false, "нет знака завершения")
            };

            RunLanguageTest("Французский", testCases);
        }

        /// <summary>
        /// Тестирование испанских предложений
        /// </summary>
        private static void TestSpanishSentences()
        {
            Console.WriteLine("🇪🇸 === ИСПАНСКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("¿Cómo estás?", true, "парные знаки вопроса"),
                ("¡Qué libro tan interesante!", true, "парные знаки восклицания"),
                ("Hola, ¿cómo te llamas?", true, "смешанные знаки"),
                ("Necesito pensarlo...", true, "обычное предложение"),

                // Некорректные
                ("¿cómo estás", false, "нет закрывающего знака"),
                ("¡qué interesante", false, "нет закрывающего знака"),
                ("hola cómo estás", false, "нет заглавной буквы")
            };

            RunLanguageTest("Испанский", testCases);
        }

        /// <summary>
        /// Тестирование итальянских предложений
        /// </summary>
        private static void TestItalianSentences()
        {
            Console.WriteLine("🇮🇹 === ИТАЛЬЯНСКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Ciao, come stai?", true, "стандартный вопрос"),
                ("Questo è un libro molto interessante!", true, "восклицательное предложение"),
                ("Devo pensarci...", true, "с многоточием"),

                // Некорректные
                ("ciao come stai", false, "нет заглавной буквы"),
                ("Questo è incompleto", false, "нет знака завершения")
            };

            RunLanguageTest("Итальянский", testCases);
        }

        /// <summary>
        /// Тестирование греческих предложений
        /// </summary>
        private static void TestGreekSentences()
        {
            Console.WriteLine("🇬🇷 === ГРЕЧЕСКИЙ ЯЗЫК ===");

            var testCases = new (string text, bool shouldAccept, string reason)[]
            {
                // Корректные предложения
                ("Γεια σας, πώς είστε;", true, "с греческим знаком вопроса ;"),
                ("Αυτό είναι ένα πολύ ενδιαφέρον βιβλίο!", true, "восклицательное предложение"),
                ("Πρέπει να το σκεφτώ...", true, "с многоточием"),

                // Некорректные
                ("γεια σας πώς είστε", false, "нет заглавной буквы"),
                ("Αυτό είναι ατελές", false, "нет знака завершения")
            };

            RunLanguageTest("Греческий", testCases);
        }

        /// <summary>
        /// Тестирование незавершенных примеров из реальных логов
        /// </summary>
        private static void TestIncompleteExamples()
        {
            Console.WriteLine("🚫 === НЕЗАВЕРШЕННЫЕ ФРАЗЫ (должны фильтроваться) ===");

            var incompleteCases = new (string text, string reason)[]
            {
                ("what we do", "английская незавершенная фраза"),
                ("и это не человеческая речь", "русская незавершенная фраза"),
                ("Я забыли ж", "обрезанная русская фраза"),
                ("...choppers from a church", "фрагмент с многоточием в начале"),
                ("que hacemos", "испанская незавершенная фраза"),
                ("was machen wir", "немецкая незавершенная фраза"),
                ("qu'est-ce que nous faisons", "французская незавершенная фраза"),
                ("τι κάνουμε", "греческая незавершенная фраза"),
                ("cosa facciamo", "итальянская незавершенная фраза")
            };

            foreach (var (text, reason) in incompleteCases)
            {
                bool result = EuropeanLanguageFilter.IsValidEuropeanSpeech(text);
                string status = result ? "❌ НЕ ОТФИЛЬТРОВАНО" : "✅ ОТФИЛЬТРОВАНО";
                Console.WriteLine($"  {status}: '{text}' ({reason})");
            }
        }

        /// <summary>
        /// Запускает тест для конкретного языка
        /// </summary>
        private static void RunLanguageTest(string language, (string text, bool shouldAccept, string reason)[] testCases)
        {
            int passed = 0;
            int total = testCases.Length;

            foreach (var (text, shouldAccept, reason) in testCases)
            {
                bool result = EuropeanLanguageFilter.IsValidEuropeanSpeech(text);
                bool testPassed = result == shouldAccept;
                
                if (testPassed) passed++;

                string status = testPassed ? "✅" : "❌";
                string expectedStr = shouldAccept ? "ПРИНЯТЬ" : "ОТКЛОНИТЬ";
                string actualStr = result ? "ПРИНЯТО" : "ОТКЛОНЕНО";
                
                Console.WriteLine($"  {status} '{text}' -> {actualStr} (ожидалось: {expectedStr}) - {reason}");
                
                if (!testPassed)
                {
                    Debug.WriteLine($"[EUROPEAN_TEST_FAIL] {language}: '{text}' -> ожидалось {expectedStr}, получено {actualStr}");
                }
            }

            Console.WriteLine($"📊 {language}: {passed}/{total} тестов пройдено");
            Console.WriteLine();
        }

        /// <summary>
        /// Демонстрация различий между стандартным и европейским фильтром
        /// </summary>
        public static void CompareFilters()
        {
            Console.WriteLine("🔄 === СРАВНЕНИЕ ФИЛЬТРОВ ===");

            var testPhrases = new string[]
            {
                "¿Cómo estás?",           // Испанский вопрос
                "Γεια σας;",              // Греческий вопрос
                "Bonjour, comment ça va ?", // Французский с пробелом
                "что мы делаем",          // Русская незавершенная фраза
                "iPhone работает отлично.", // Бренд с маленькой буквы
                "...fragment of text",    // Фрагмент с многоточием
                "5 минут назад."          // Начало с цифры
            };

            foreach (var phrase in testPhrases)
            {
                bool standardResult = AdvancedSpeechFilter.IsValidSpeechQuick(phrase);
                bool europeanResult = EuropeanLanguageFilter.IsValidEuropeanSpeech(phrase);

                string comparison;
                if (standardResult == europeanResult)
                {
                    comparison = standardResult ? "✅ Оба ПРИНИМАЮТ" : "❌ Оба ОТКЛОНЯЮТ";
                }
                else
                {
                    comparison = $"🔄 Стандартный: {(standardResult ? "ПРИНИМАЕТ" : "ОТКЛОНЯЕТ")}, Европейский: {(europeanResult ? "ПРИНИМАЕТ" : "ОТКЛОНЯЕТ")}";
                }

                Console.WriteLine($"  '{phrase}' -> {comparison}");
            }
        }
    }
}