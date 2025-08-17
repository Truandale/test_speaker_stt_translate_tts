using System.Collections.Concurrent;
using System.Text;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Менеджер потоковых результатов STT с параллельным переводом и TTS
    /// </summary>
    public class StreamingResultManager : IDisposable
    {
        private readonly ConcurrentDictionary<int, StreamingResult> results = new();
        private readonly SemaphoreSlim translationSemaphore = new(2); // Максимум 2 параллельных перевода
        private readonly StringBuilder currentText = new();
        private EnhancedTTSEngine? ttsEngine;
        private int lastDisplayedChunk = 0;
        private int maxResultsToKeep = 50; // Сколько результатов хранить в памяти
        
        // События
        public event Func<string, string, string, Task<string>>? TranslationRequested; // text, fromLang, toLang
        public event Action<string>? CurrentTextUpdated; // Обновление накопленного текста
        public event Action<string, int>? ChunkTranslated; // Переведенный чанк
        public event Action<string>? StatusUpdated;
        
        private struct StreamingResult
        {
            public int ChunkNumber;
            public string OriginalText;
            public string TranslatedText;
            public DateTime Timestamp;
            public bool IsTranslated;
            public bool IsSpoken;
        }

        public StreamingResultManager(EnhancedTTSEngine? ttsEngine = null)
        {
            this.ttsEngine = ttsEngine;
        }

        /// <summary>
        /// Добавляет новый результат STT для обработки
        /// </summary>
        public async Task AddStreamingResult(string text, int chunkNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                
                // Создаем результат
                var result = new StreamingResult
                {
                    ChunkNumber = chunkNumber,
                    OriginalText = text.Trim(),
                    TranslatedText = "",
                    Timestamp = DateTime.Now,
                    IsTranslated = false,
                    IsSpoken = false
                };
                
                // Добавляем в коллекцию
                results.TryAdd(chunkNumber, result);
                
                AudioAnalysisUtils.SafeDebugLog($"📝 Добавлен результат чанка #{chunkNumber}: '{text}'");
                
                // Обновляем накопленный текст
                UpdateCurrentText();
                
                // Запускаем параллельный перевод
                _ = Task.Run(() => ProcessResultAsync(chunkNumber));
                
                // Очищаем старые результаты
                CleanupOldResults();
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка добавления результата: {ex.Message}");
            }
        }

        /// <summary>
        /// Асинхронная обработка результата (перевод + TTS)
        /// </summary>
        private async Task ProcessResultAsync(int chunkNumber)
        {
            await translationSemaphore.WaitAsync();
            
            try
            {
                if (!results.TryGetValue(chunkNumber, out var result))
                    return;
                
                AudioAnalysisUtils.SafeDebugLog($"🔄 Обработка результата чанка #{chunkNumber}");
                StatusUpdated?.Invoke($"🔄 Перевожу чанк #{chunkNumber}...");
                
                // Определяем язык
                bool isEnglish = AudioAnalysisUtils.IsEnglishText(result.OriginalText);
                string fromLang = isEnglish ? "en" : "ru";
                string toLang = isEnglish ? "ru" : "en";
                
                // Выполняем перевод
                string translatedText = result.OriginalText;
                if (TranslationRequested != null)
                {
                    translatedText = await TranslationRequested(result.OriginalText, fromLang, toLang);
                }
                
                // Обновляем результат
                result.TranslatedText = translatedText;
                result.IsTranslated = true;
                results.TryUpdate(chunkNumber, result, results[chunkNumber]);
                
                AudioAnalysisUtils.SafeDebugLog($"✅ Чанк #{chunkNumber} переведен: '{translatedText}'");
                ChunkTranslated?.Invoke(translatedText, chunkNumber);
                
                // Озвучиваем результат
                await SpeakResultAsync(chunkNumber);
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка обработки чанка #{chunkNumber}: {ex.Message}");
            }
            finally
            {
                translationSemaphore.Release();
            }
        }

        /// <summary>
        /// Озвучивание результата
        /// </summary>
        private async Task SpeakResultAsync(int chunkNumber)
        {
            try
            {
                if (ttsEngine == null || !results.TryGetValue(chunkNumber, out var result))
                    return;
                
                if (!result.IsTranslated || result.IsSpoken)
                    return;
                
                // Фильтруем заглушки
                if (AudioAnalysisUtils.IsAudioPlaceholder(result.TranslatedText))
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS_DEBUG] 🚫 TTS отфильтрован для чанка #{chunkNumber}: заглушка");
                    return;
                }
                
                // Проверяем, что это не слишком короткий текст
                if (result.TranslatedText.Length < 3)
                {
                    AudioAnalysisUtils.SafeDebugLog($"🚫 TTS отфильтрован для чанка #{chunkNumber}: слишком короткий");
                    return;
                }
                
                AudioAnalysisUtils.SafeDebugLog($"🔊 TTS для чанка #{chunkNumber}: '{result.TranslatedText}'");
                
                // Уведомляем процессор об активном TTS
                if (ttsEngine != null)
                {
                    bool success = await ttsEngine.SpeakTextAsync(result.TranslatedText);
                    
                    if (success)
                    {
                        // Обновляем статус
                        result.IsSpoken = true;
                        results.TryUpdate(chunkNumber, result, results[chunkNumber]);
                        AudioAnalysisUtils.SafeDebugLog($"✅ TTS завершен для чанка #{chunkNumber}");
                    }
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка TTS для чанка #{chunkNumber}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет накопленный текст из всех результатов
        /// </summary>
        private void UpdateCurrentText()
        {
            try
            {
                currentText.Clear();
                
                // Сортируем результаты по номеру чанка
                var sortedResults = results.Values
                    .OrderBy(r => r.ChunkNumber)
                    .ToList();
                
                foreach (var result in sortedResults)
                {
                    if (!string.IsNullOrWhiteSpace(result.OriginalText))
                    {
                        currentText.Append(result.OriginalText).Append(" ");
                    }
                }
                
                string text = currentText.ToString().Trim();
                CurrentTextUpdated?.Invoke(text);
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка обновления текста: {ex.Message}");
            }
        }

        /// <summary>
        /// Очистка старых результатов для экономии памяти
        /// </summary>
        private void CleanupOldResults()
        {
            try
            {
                if (results.Count <= maxResultsToKeep) return;
                
                // Удаляем самые старые результаты
                var oldResults = results.Values
                    .OrderBy(r => r.Timestamp)
                    .Take(results.Count - maxResultsToKeep)
                    .ToList();
                
                foreach (var oldResult in oldResults)
                {
                    results.TryRemove(oldResult.ChunkNumber, out _);
                }
                
                AudioAnalysisUtils.SafeDebugLog($"🧹 Очищено {oldResults.Count} старых результатов");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка очистки результатов: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение накопленного текста
        /// </summary>
        public string GetCurrentText()
        {
            return currentText.ToString().Trim();
        }

        /// <summary>
        /// Получение статистики
        /// </summary>
        public string GetStats()
        {
            int total = results.Count;
            int translated = results.Values.Count(r => r.IsTranslated);
            int spoken = results.Values.Count(r => r.IsSpoken);
            
            return $"Всего: {total}, Переведено: {translated}, Озвучено: {spoken}";
        }

        /// <summary>
        /// Принудительная обработка всех необработанных результатов
        /// </summary>
        public async Task FlushPendingResults()
        {
            var pendingChunks = results.Values
                .Where(r => !r.IsTranslated)
                .Select(r => r.ChunkNumber)
                .ToList();
            
            foreach (int chunkNumber in pendingChunks)
            {
                _ = Task.Run(() => ProcessResultAsync(chunkNumber));
            }
            
            AudioAnalysisUtils.SafeDebugLog($"🔄 Запущена обработка {pendingChunks.Count} ожидающих результатов");
        }

        /// <summary>
        /// Сброс всех результатов
        /// </summary>
        public void Reset()
        {
            try
            {
                results.Clear();
                currentText.Clear();
                lastDisplayedChunk = 0;
                CurrentTextUpdated?.Invoke("");
                AudioAnalysisUtils.SafeDebugLog("🔄 StreamingResultManager сброшен");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка сброса результатов: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                Reset();
                translationSemaphore?.Dispose();
                AudioAnalysisUtils.SafeDebugLog("🗑️ StreamingResultManager утилизирован");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка утилизации: {ex.Message}");
            }
        }
    }
}