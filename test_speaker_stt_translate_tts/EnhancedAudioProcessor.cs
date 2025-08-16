using NAudio.Wave;
using System.Diagnostics;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Продвинутая логика обработки аудио для STT, адаптированная из MORT
    /// </summary>
    public class EnhancedAudioProcessor : IDisposable
    {
        private readonly List<byte> audioBuffer = new();
        private bool isCollectingAudio = false;
        private DateTime lastVoiceActivity = DateTime.Now;
        private DateTime audioCollectionStartTime = DateTime.Now;
        private bool isTTSPlaying = false; // Флаг блокировки STT во время TTS
        private int debugCounter = 0;
        
        // Настройки из MORT
        private float voiceThreshold = 0.05f;
        private int silenceDurationMs = 1000; // 1 секунда тишины
        private int maxRecordingMs = 8000; // Максимум 8 секунд записи
        private int maxBufferSize = 500000; // Максимум 500KB
        
        // События
        public event Func<byte[], Task<string>>? AudioReadyForSTT; // Аудио готово для распознавания
        public event Action<string>? StatusUpdated; // Обновление статуса
        
        public float VoiceThreshold 
        { 
            get => voiceThreshold; 
            set => voiceThreshold = Math.Max(0.001f, Math.Min(1.0f, value)); 
        }
        
        public int SilenceDurationMs 
        { 
            get => silenceDurationMs; 
            set => silenceDurationMs = Math.Max(100, Math.Min(5000, value)); 
        }
        
        public bool IsTTSPlaying 
        { 
            get => isTTSPlaying; 
            set => isTTSPlaying = value; 
        }
        
        public bool IsCollectingAudio => isCollectingAudio;

        /// <summary>
        /// Основная функция обработки аудио (адаптированная из MORT ProcessAudioForSTT)
        /// </summary>
        public async Task ProcessAudioForSTT(byte[] buffer, int bytesRecorded, float audioLevel)
        {
            try
            {
                // 🔇 Блокировка STT во время воспроизведения TTS (предотвращение зацикливания)
                if (isTTSPlaying)
                {
                    // Проверяем, не заблокирован ли флаг TTS более 30 секунд
                    if (DateTime.Now - lastVoiceActivity > TimeSpan.FromSeconds(30))
                    {
                        AudioAnalysisUtils.SafeDebugLog("⚠️ TTS флаг заблокирован слишком долго, сбрасываем");
                        isTTSPlaying = false;
                    }
                    else
                    {
                        return; // Игнорируем аудио во время TTS
                    }
                }

                // Отладочная информация каждые 50 вызовов
                debugCounter++;
                if (debugCounter % 20 == 0)
                {
                    AudioAnalysisUtils.SafeDebugLog($"🔊 ProcessAudioForSTT: уровень={audioLevel:F4}, порог={voiceThreshold:F4}, собираем={isCollectingAudio}");

                    // Показываем состояние ожидания когда нет активности
                    if (!isCollectingAudio)
                    {
                        StatusUpdated?.Invoke($"🔇 Ожидание речи... (уровень: {audioLevel:F3}, порог: {voiceThreshold:F3})");
                    }
                }

                // Проверяем, есть ли голосовая активность с улучшенным анализом
                bool isVoiceDetected = audioLevel > voiceThreshold;
                
                // Дополнительная проверка для человеческой речи (частотный анализ)
                float speechLikelihood = 1.0f;
                if (isVoiceDetected && bytesRecorded >= 8)
                {
                    speechLikelihood = AudioAnalysisUtils.AnalyzeSpeechCharacteristics(buffer, bytesRecorded);
                    if (speechLikelihood < 0.3f)
                    {
                        isVoiceDetected = false; // Отфильтровываем как не-речь
                        AudioAnalysisUtils.SafeDebugLog($"🚫 Фильтруем как не-речь: уровень={audioLevel:F4}, вероятность={speechLikelihood:F3}");
                    }
                }

                if (isVoiceDetected)
                {
                    // Начинаем сбор аудио данных
                    if (!isCollectingAudio)
                    {
                        isCollectingAudio = true;
                        audioBuffer.Clear();
                        audioCollectionStartTime = DateTime.Now;
                        AudioAnalysisUtils.SafeDebugLog($"🎤 Начато распознавание речи... Уровень: {audioLevel:F4}");
                        StatusUpdated?.Invoke($"🎤 Слушаю... (уровень: {audioLevel:F3})");
                    }
                    else
                    {
                        // Обновляем статус во время записи
                        if (debugCounter % 10 == 0)
                        {
                            StatusUpdated?.Invoke($"🎤 Записываю... (уровень: {audioLevel:F3}, буфер: {audioBuffer.Count} байт)");
                        }
                    }

                    // Добавляем аудио данные в буфер
                    byte[] audioData = new byte[bytesRecorded];
                    Array.Copy(buffer, audioData, bytesRecorded);
                    audioBuffer.AddRange(audioData);

                    lastVoiceActivity = DateTime.Now;
                }
                else if (isCollectingAudio)
                {
                    // Проверяем, достаточно ли времени прошло без голоса ИЛИ буфер стал очень большим
                    var silenceDuration = DateTime.Now - lastVoiceActivity;
                    var totalCollectionTime = DateTime.Now - audioCollectionStartTime;
                    
                    bool shouldProcess = silenceDuration.TotalMilliseconds > silenceDurationMs || 
                                        totalCollectionTime.TotalMilliseconds > maxRecordingMs || 
                                        audioBuffer.Count > maxBufferSize;
                    
                    if (shouldProcess)
                    {
                        AudioAnalysisUtils.SafeDebugLog($"⏹️ Конец речи: тишина={silenceDuration.TotalMilliseconds}мс, время={totalCollectionTime.TotalMilliseconds}мс, буфер={audioBuffer.Count}");

                        // Обрабатываем собранные аудио данные асинхронно
                        _ = Task.Run(async () => await ProcessCollectedAudioAsync());
                        isCollectingAudio = false;
                    }
                }

                // Очищаем буфер если он становится слишком большим (защита от переполнения)
                if (audioBuffer.Count > 50_000_000) // 50MB лимит
                {
                    AudioAnalysisUtils.SafeDebugLog("⚠️ Буфер переполнен, очищаем");
                    audioBuffer.Clear();
                    isCollectingAudio = false;
                    StatusUpdated?.Invoke("⚠️ Буфер переполнен, сброшен");
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка в ProcessAudioForSTT: {ex.Message}");
            }
        }

        /// <summary>
        /// Обрабатывает собранные аудио данные (из MORT)
        /// </summary>
        private async Task ProcessCollectedAudioAsync()
        {
            try
            {
                AudioAnalysisUtils.SafeDebugLog($"🔄 ProcessCollectedAudioAsync ВЫЗВАН! Буфер: {audioBuffer.Count} байт");
                
                if (audioBuffer.Count == 0) 
                {
                    AudioAnalysisUtils.SafeDebugLog($"⚠️ Пустой аудио буфер, прерываем обработку");
                    return;
                }

                StatusUpdated?.Invoke($"🔄 Обрабатываю... (записано {audioBuffer.Count} байт)");

                // Вызываем событие обработки STT если есть подписчики
                if (AudioReadyForSTT != null)
                {
                    // Копируем буфер для безопасности
                    byte[] audioCopy = audioBuffer.ToArray();
                    
                    try
                    {
                        string result = await AudioReadyForSTT(audioCopy);
                        
                        // Фильтруем заглушки
                        if (!AudioAnalysisUtils.IsAudioPlaceholder(result))
                        {
                            AudioAnalysisUtils.SafeDebugLog($"✅ STT результат: {result}");
                        }
                        else
                        {
                            AudioAnalysisUtils.SafeDebugLog($"🚫 Отфильтровано как заглушка: {result}");
                            StatusUpdated?.Invoke("🚫 Обнаружена аудио заглушка, игнорируем");
                        }
                    }
                    catch (Exception ex)
                    {
                        AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка STT обработки: {ex.Message}");
                        StatusUpdated?.Invoke($"❌ Ошибка распознавания: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка в ProcessCollectedAudioAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Сброс состояния обработки
        /// </summary>
        public void Reset()
        {
            try
            {
                isCollectingAudio = false;
                audioBuffer.Clear();
                isTTSPlaying = false;
                AudioAnalysisUtils.SafeDebugLog("🔄 Состояние аудио процессора сброшено");
                StatusUpdated?.Invoke("🔄 Готов к записи");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка сброса: {ex.Message}");
            }
        }

        /// <summary>
        /// Принудительная обработка текущего буфера
        /// </summary>
        public async Task ForceProcessBuffer()
        {
            if (isCollectingAudio && audioBuffer.Count > 0)
            {
                AudioAnalysisUtils.SafeDebugLog("🔄 Принудительная обработка буфера");
                isCollectingAudio = false;
                await ProcessCollectedAudioAsync();
            }
        }

        public void Dispose()
        {
            try
            {
                Reset();
                AudioAnalysisUtils.SafeDebugLog("🗑️ EnhancedAudioProcessor утилизирован");
            }
            catch (Exception ex)
            {
                AudioAnalysisUtils.SafeDebugLog($"❌ Ошибка утилизации: {ex.Message}");
            }
        }
    }
}