using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.MediaFoundation;
using System.Buffers;
using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Стабильная система захвата аудио с горячим переподключением и минимальными GC-паузами
    /// </summary>
    public class StableAudioCapture : IDisposable
    {
        #region Константы диагностики
        
        // 🔧 Настройки диагностики - можно менять для тестирования
        private const bool ENABLE_DIAGNOSTIC_STUBS = false;     // Включить тестовые заглушки
        private const bool ENABLE_STATS_LOGGING = true;         // Показывать статистику обработки
        private const double STATS_INTERVAL_SECONDS = 5.0;      // Интервал вывода статистики
        
        #endregion
        
        #region MMCSS и Power Management для приоритета потоков
        
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out int taskIndex);
        
        [DllImport("avrt.dll")]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
        
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_AWAYMODE_REQUIRED = 0x00000040;
        
        #endregion

        #region Private Fields
        
        private WasapiLoopbackCapture? _capture;
        private MMDeviceEnumerator? _deviceEnumerator;
        private DeviceNotificationClient? _deviceNotificationClient;
        private bool _mediaFoundationInitialized = false;
        
        // Event-driven каналы с bounded capacity для backpressure
        private readonly Channel<byte[]> _rawAudioChannel;
        private readonly Channel<float[]> _normalizedAudioChannel;
        private readonly Channel<string> _sttResultChannel;
        private readonly Channel<string> _translationChannel;
        
        // ArrayPool для минимизации GC-пауз
        private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
        private readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;
        
        // Cancellation и состояние
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isDisposed = false;
        private bool _isCapturing = false;
        
        // MMCSS handles для приоритетов
        private IntPtr _captureThreadHandle = IntPtr.Zero;
        private IntPtr _normalizeThreadHandle = IntPtr.Zero;
        
        // Рабочие задачи
        private Task? _normalizeTask;
        private Task? _sttTask;
        private Task? _translationTask;
        
        // Константы для стабильности
        private const int CHANNEL_CAPACITY = 64;
        private const int TARGET_SAMPLE_RATE = 16000;
        private const int TARGET_CHANNELS = 1;
        private const int AUDIO_LATENCY_MS = 30;
        
        #endregion

        #region Events
        
        public event Action<string>? OnTextRecognized;
        public event Action<string>? OnTextTranslated;
        public event Action<string>? OnError;
        public event Action<string>? OnStatusChanged;
        
        #endregion

        public StableAudioCapture()
        {
            // Инициализация MediaFoundation для ресемплинга
            if (!_mediaFoundationInitialized)
            {
                MediaFoundationApi.Startup();
                _mediaFoundationInitialized = true;
            }
            
            // Создание bounded channels с backpressure
            var channelOptions = new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.DropOldest // Дропаем старые при переполнении
            };
            
            _rawAudioChannel = Channel.CreateBounded<byte[]>(channelOptions);
            _normalizedAudioChannel = Channel.CreateBounded<float[]>(channelOptions);
            _sttResultChannel = Channel.CreateBounded<string>(channelOptions);
            _translationChannel = Channel.CreateBounded<string>(channelOptions);
            
            // Предотвращение сна системы во время работы
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
        }

        #region Device Notification Client (Simplified)
        
        private class DeviceNotificationClient
        {
            public Action? OnDefaultChanged { get; set; }
            
            // Simplified device monitoring - can be enhanced later
            public void StartMonitoring()
            {
                // TODO: Implement device monitoring if needed
                // For now, just a placeholder
            }
            
            public void StopMonitoring()
            {
                // TODO: Implement device monitoring cleanup
            }
        }
        
        #endregion

        #region Public Methods
        
        public async Task StartCaptureAsync()
        {
            if (_isCapturing || _isDisposed)
                return;
                
            try
            {
                OnStatusChanged?.Invoke("🚀 Инициализация стабильного аудио-захвата...");
                
                // Настройка горячего переподключения устройств
                await SetupDeviceMonitoringAsync();
                
                // Запуск захвата
                await StartAudioCaptureAsync();
                
                // Запуск конвейера обработки
                StartProcessingPipeline();
                
                _isCapturing = true;
                OnStatusChanged?.Invoke("✅ Стабильный аудио-захват активен");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"❌ Ошибка запуска захвата: {ex.Message}");
                await StopCaptureAsync();
            }
        }
        
        public async Task StopCaptureAsync()
        {
            if (!_isCapturing)
                return;
                
            OnStatusChanged?.Invoke("⏹️ Остановка аудио-захвата...");
            
            _isCapturing = false;
            
            // Остановка захвата
            await StopAudioCaptureAsync();
            
            // Закрытие каналов
            _rawAudioChannel.Writer.TryComplete();
            _normalizedAudioChannel.Writer.TryComplete();
            _sttResultChannel.Writer.TryComplete();
            _translationChannel.Writer.TryComplete();
            
            // Ожидание завершения задач
            await Task.WhenAll(
                _normalizeTask ?? Task.CompletedTask,
                _sttTask ?? Task.CompletedTask,
                _translationTask ?? Task.CompletedTask
            );
            
            OnStatusChanged?.Invoke("✅ Аудио-захват остановлен");
        }
        
        #endregion

        #region Private Methods - Device Management
        
        private async Task SetupDeviceMonitoringAsync()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceNotificationClient = new DeviceNotificationClient
            {
                OnDefaultChanged = async () => await RestartCaptureSafeAsync()
            };
            
            // Simplified monitoring setup
            _deviceNotificationClient.StartMonitoring();
            
            // TODO: Implement proper device change detection
            // For now, monitoring is placeholder
        }
        
        private async Task RestartCaptureSafeAsync()
        {
            OnStatusChanged?.Invoke("🔄 Переподключение к новому аудио-устройству...");
            
            try
            {
                // Остановка текущего захвата (без закрытия каналов)
                await StopAudioCaptureAsync();
                
                // Пауза для стабилизации
                await Task.Delay(100);
                
                // Перезапуск с новым устройством
                await StartAudioCaptureAsync();
                
                OnStatusChanged?.Invoke("✅ Успешно переподключен к новому устройству");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"❌ Ошибка переподключения: {ex.Message}");
            }
        }
        
        #endregion

        #region Private Methods - Audio Capture
        
        private async Task StartAudioCaptureAsync()
        {
            if (_deviceEnumerator == null)
                throw new InvalidOperationException("Device enumerator not initialized");
                
            // Получение дефолтного устройства воспроизведения
            var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            
            // Создание захвата с минимальной латентностью
            _capture = new WasapiLoopbackCapture(defaultDevice);
            
            // Event-driven обработка с ArrayPool
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            
            OnStatusChanged?.Invoke($"🎧 Захват: {defaultDevice.FriendlyName} ({_capture.WaveFormat})");
            
            // Запуск записи
            _capture.StartRecording();
        }
        
        private async Task StopAudioCaptureAsync()
        {
            if (_capture != null)
            {
                try
                {
                    if (_capture.CaptureState == CaptureState.Capturing)
                    {
                        _capture.StopRecording();
                    }
                    
                    // Отписка от событий
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    
                    _capture.Dispose();
                    _capture = null;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"⚠️ Ошибка остановки захвата: {ex.Message}");
                }
            }
            
            // Освобождение MMCSS handles
            if (_captureThreadHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(_captureThreadHandle);
                _captureThreadHandle = IntPtr.Zero;
            }
        }
        
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || _cancellationTokenSource.Token.IsCancellationRequested)
                return;
                
            // MMCSS приоритет для capture thread (только при первом вызове)
            if (_captureThreadHandle == IntPtr.Zero)
            {
                _captureThreadHandle = AvSetMmThreadCharacteristics("Audio", out _);
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            
            // Аренда буфера из pool (минимизация GC)
            var buffer = _bytePool.Rent(e.BytesRecorded);
            
            try
            {
                // Копирование данных
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
                
                // Отправка в канал с backpressure
                if (!_rawAudioChannel.Writer.TryWrite(buffer))
                {
                    // Канал переполнен - дропаем кадр, возвращаем в pool
                    _bytePool.Return(buffer);
                }
            }
            catch
            {
                // При любой ошибке возвращаем буфер в pool
                _bytePool.Return(buffer);
            }
        }
        
        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                OnError?.Invoke($"❌ Захват остановлен с ошибкой: {e.Exception.Message}");
                _rawAudioChannel.Writer.TryComplete(e.Exception);
            }
        }
        
        #endregion

        #region Private Methods - Processing Pipeline
        
        private void StartProcessingPipeline()
        {
            var ct = _cancellationTokenSource.Token;
            
            // Задача нормализации: raw PCM → 16kHz mono float
            _normalizeTask = Task.Run(async () => await NormalizeAudioLoop(ct), ct);
            
            // Задача STT: normalized audio → text
            _sttTask = Task.Run(async () => await SttProcessingLoop(ct), ct);
            
            // Задача перевода: text → translated text
            _translationTask = Task.Run(async () => await TranslationLoop(ct), ct);
        }
        
        private async Task NormalizeAudioLoop(CancellationToken ct)
        {
            // MMCSS приоритет для normalize thread
            _normalizeThreadHandle = AvSetMmThreadCharacteristics("Audio", out _);
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            
            WaveFormat? sourceFormat = null;
            BufferedWaveProvider? provider = null;
            MediaFoundationResampler? resampler = null;
            
            try
            {
                await foreach (var rawBuffer in _rawAudioChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // Инициализация при первом кадре
                        if (provider == null && _capture != null)
                        {
                            sourceFormat = _capture.WaveFormat;
                            provider = new BufferedWaveProvider(sourceFormat)
                            {
                                DiscardOnBufferOverflow = true,
                                BufferDuration = TimeSpan.FromSeconds(3)
                            };
                            
                            var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(TARGET_SAMPLE_RATE, TARGET_CHANNELS);
                            resampler = new MediaFoundationResampler(provider, targetFormat)
                            {
                                ResamplerQuality = 60 // Высокое качество
                            };
                            
                            OnStatusChanged?.Invoke($"🔄 Нормализация: {sourceFormat} → {targetFormat}");
                        }
                        
                        if (provider != null && resampler != null)
                        {
                            // Добавление сырых данных в буфер
                            provider.AddSamples(rawBuffer, 0, rawBuffer.Length);
                            
                            // Чтение нормализованных данных
                            await ProcessNormalizedAudio(resampler, ct);
                        }
                    }
                    finally
                    {
                        // Возврат буфера в pool
                        _bytePool.Return(rawBuffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальная отмена
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"❌ Ошибка нормализации: {ex.Message}");
            }
            finally
            {
                resampler?.Dispose();
                provider?.ClearBuffer();
                
                if (_normalizeThreadHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(_normalizeThreadHandle);
                    _normalizeThreadHandle = IntPtr.Zero;
                }
            }
        }
        
        private async Task ProcessNormalizedAudio(MediaFoundationResampler resampler, CancellationToken ct)
        {
            var bytesPerFrame = sizeof(float) * TARGET_CHANNELS;
            var maxFramesPerRead = TARGET_SAMPLE_RATE; // 1 секунда максимум
            var byteBuffer = _bytePool.Rent(maxFramesPerRead * bytesPerFrame);
            
            try
            {
                int bytesRead = resampler.Read(byteBuffer, 0, byteBuffer.Length);
                if (bytesRead <= 0)
                    return;
                    
                var frameCount = bytesRead / bytesPerFrame;
                var floatBuffer = _floatPool.Rent(frameCount);
                
                try
                {
                    // Конвертация byte[] → float[]
                    Buffer.BlockCopy(byteBuffer, 0, floatBuffer, 0, bytesRead);
                    
                    // Обработка stereo → mono если нужно
                    var processedAudio = ProcessAudioChannels(floatBuffer.AsSpan(0, frameCount));
                    
                    // 🔇 АУДИО-ГЕЙТИНГ: Проверка RMS уровня для фильтрации тишины
                    var rms = CalculateRMS(processedAudio);
                    const float SILENCE_THRESHOLD = 0.001f; // Минимальный порог громкости
                    
                    if (rms < SILENCE_THRESHOLD)
                    {
                        // Тишина - не отправляем в STT, возвращаем буфер
                        _floatPool.Return(processedAudio);
                        return;
                    }
                    
                    // Отправка в STT канал
                    if (!_normalizedAudioChannel.Writer.TryWrite(processedAudio))
                    {
                        // Backpressure - дропаем сегмент
                        _floatPool.Return(processedAudio);
                    }
                }
                catch
                {
                    _floatPool.Return(floatBuffer);
                    throw;
                }
            }
            finally
            {
                _bytePool.Return(byteBuffer);
            }
        }
        
        private float[] ProcessAudioChannels(ReadOnlySpan<float> source)
        {
            if (_capture?.WaveFormat.Channels == 1)
            {
                // Уже mono - просто копируем
                var result = _floatPool.Rent(source.Length);
                source.CopyTo(result);
                return result;
            }
            else if (_capture?.WaveFormat.Channels == 2)
            {
                // Stereo → mono downmix
                var monoFrames = source.Length / 2;
                var result = _floatPool.Rent(monoFrames);
                StereoToMono(source, result.AsSpan(0, monoFrames));
                return result;
            }
            else
            {
                // Многоканальный → mono (берем первый канал)
                var channels = _capture?.WaveFormat.Channels ?? 1;
                var monoFrames = source.Length / channels;
                var result = _floatPool.Rent(monoFrames);
                
                for (int i = 0; i < monoFrames; i++)
                {
                    result[i] = source[i * channels];
                }
                
                return result;
            }
        }
        
        private static void StereoToMono(ReadOnlySpan<float> stereo, Span<float> mono)
        {
            int sourceIndex = 0;
            for (int i = 0; i < mono.Length; i++, sourceIndex += 2)
            {
                mono[i] = 0.5f * (stereo[sourceIndex] + stereo[sourceIndex + 1]);
            }
        }
        
        private async Task SttProcessingLoop(CancellationToken ct)
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            
            // 🔧 Счетчики для диагностики (не спамят логи)
            var processedSegments = 0;
            var lastStatsTime = DateTime.Now;
            
            try
            {
                await foreach (var audioSegment in _normalizedAudioChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // TODO: Здесь будет интеграция с Whisper
                        // var recognizedText = await WhisperRecognizeAsync(audioSegment, ct);
                        
                        await Task.Delay(10, ct);
                        processedSegments++;
                        
                        // � УМНАЯ ДИАГНОСТИКА: Показываем статистику раз в 5 секунд
                        var now = DateTime.Now;
                        if ((now - lastStatsTime).TotalSeconds >= 5.0)
                        {
                            OnStatusChanged?.Invoke($"📊 STT: Обработано {processedSegments} сегментов за {(now - lastStatsTime).TotalSeconds:F1}с");
                            processedSegments = 0;
                            lastStatsTime = now;
                        }
                        
                        // 🧪 ТЕСТОВАЯ ЗАГЛУШКА: Можно включить для тестов конкретных функций
                        string? recognizedText = null;
                        
                        // Раскомментируйте для тестирования пайплайна:
                        // if (audioSegment.Length > 4000) // Только для "длинных" сегментов
                        //     recognizedText = $"[TEST] Audio {audioSegment.Length} samples, RMS: {CalculateRMS(audioSegment):F4}";
                        
                        if (!string.IsNullOrWhiteSpace(recognizedText))
                        {
                            OnTextRecognized?.Invoke(recognizedText);
                            await _sttResultChannel.Writer.WriteAsync(recognizedText, ct);
                        }
                    }
                    finally
                    {
                        _floatPool.Return(audioSegment);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальная отмена
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"❌ Ошибка STT: {ex.Message}");
            }
        }
        
        private async Task TranslationLoop(CancellationToken ct)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
            
            try
            {
                await foreach (var text in _sttResultChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // TODO: Здесь будет интеграция с переводчиком
                        // var translatedText = await TranslateAsync(text, "ru", "en", ct);
                        
                        // Временная заглушка
                        await Task.Delay(50, ct);
                        var translatedText = $"[TRANSLATED] {text}";
                        
                        OnTextTranslated?.Invoke(translatedText);
                        await _translationChannel.Writer.WriteAsync(translatedText, ct);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"❌ Ошибка перевода: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальная отмена
            }
        }
        
        /// <summary>
        /// Вычисляет RMS (Root Mean Square) уровень громкости аудио сигнала
        /// </summary>
        /// <param name="audioData">Аудио данные в формате float</param>
        /// <returns>RMS значение (0.0 - тишина, 1.0 - максимум)</returns>
        private static float CalculateRMS(ReadOnlySpan<float> audioData)
        {
            if (audioData.Length == 0)
                return 0.0f;
                
            double sum = 0.0;
            for (int i = 0; i < audioData.Length; i++)
            {
                var sample = audioData[i];
                sum += sample * sample;
            }
            
            return (float)Math.Sqrt(sum / audioData.Length);
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (_isDisposed)
                return;
                
            _isDisposed = true;
            
            // Остановка захвата
            StopCaptureAsync().GetAwaiter().GetResult();
            
            // Отмена всех операций
            _cancellationTokenSource.Cancel();
            
            // Очистка ресурсов
            _deviceEnumerator?.Dispose();
            _deviceNotificationClient?.StopMonitoring();
            _capture?.Dispose();
            
            _cancellationTokenSource.Dispose();
            
            // Восстановление power state
            SetThreadExecutionState(ES_CONTINUOUS);
            
            // MediaFoundation cleanup
            if (_mediaFoundationInitialized)
            {
                MediaFoundationApi.Shutdown();
                _mediaFoundationInitialized = false;
            }
        }
        
        #endregion
    }
}