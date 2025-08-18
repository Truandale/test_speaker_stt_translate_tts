# 🚀 СТАБИЛЬНАЯ АРХИТЕКТУРА STT→ПЕРЕВОД→TTS

## Обзор новой архитектуры

Кардинально улучшенная система аудио-захвата и обработки с устранением основных проблем стабильности:

### 🎯 Решенные проблемы
- ✅ **Дропы аудио** - event-driven WASAPI с короткой латентностью (20-50мс)
- ✅ **Смена устройств** - горячее переподключение при изменении дефолтных динамиков
- ✅ **Джиттер/переполнения** - bounded Channels с backpressure защитой
- ✅ **GC-паузы** - ArrayPool для всех буферов, минимизация аллокаций
- ✅ **Блокировки UI** - полностью асинхронная обработка в отдельных потоках
- ✅ **Нестабильность TTS** - единый воркер с очередью и умным разбиением

---

## 🏗️ Архитектурные компоненты

### 1. **StableAudioCapture** - Стабильный аудио-захват
```csharp
// Event-driven захват с минимальной латентностью
private WasapiLoopbackCapture _capture;
private readonly Channel<byte[]> _rawAudioChannel;

// MMCSS приоритеты для аудио потоков
private IntPtr _captureThreadHandle = AvSetMmThreadCharacteristics("Audio", out _);
```

**Особенности:**
- 🎧 **Event-driven WASAPI** с латентностью 30мс
- 🔄 **Горячее переподключение** при смене устройств
- 📊 **Bounded Channels** (64 элемента) с DropOldest
- 🧠 **ArrayPool** для нуль-аллокаций в горячем пути
- ⚡ **MMCSS приоритеты** для аудио потоков

### 2. **SlidingWindowAggregator** - Умное окно с VAD
```csharp
// Скользящее окно 3 секунды с перекрытием 0.5 сек
private const float WINDOW_DURATION_SEC = 3.0f;
private const float OVERLAP_DURATION_SEC = 0.5f;
private const float RMS_THRESHOLD = 0.001f;
```

**Функциональность:**
- 🎚️ **Скользящее окно** 3с с перекрытием 0.5с 
- 🔇 **Базовый VAD** по RMS + спектральной энергии
- 🔀 **Умное слияние** текста с защитой от дублирования
- ⏰ **Триггеры**: размер окна, пауза >600мс, интервал 1.5с
- 📝 **Агрегация текста** до 400 символов для оптимального TTS

### 3. **StableTtsEngine** - Надежная озвучка
```csharp
// Единый воркер с bounded очередью
private readonly Channel<TtsRequest> _ttsQueue;
private const int MAX_TEXT_LENGTH = 500;
```

**Преимущества:**
- 🎤 **Единый воркер** - нет накладывания озвучек
- 📝 **Умное разбиение** длинных текстов на части ≤500 символов
- 🚫 **Backpressure** - дропание старых при переполнении
- 🌐 **Автопереключение языков** с кешированием голосов
- ⏹️ **Graceful отмена** через CancellationToken

---

## 🔄 Поток данных

```
🎧 WASAPI Loopback (30мс)
    ↓ (ArrayPool буферы)
📊 Raw Audio Channel (bounded 64)
    ↓ (MediaFoundation ресемплер)
🎵 16kHz Mono Float Channel
    ↓ (скользящее окно 3с)
🪟 SlidingWindow Aggregator
    ↓ (Whisper STT)
📝 STT Results Channel
    ↓ (Google Translate)
🌐 Translation Channel  
    ↓ (умное разбиение)
🎙️ Stable TTS Engine
    ↓ (Windows SAPI)
🔊 Audio Output
```

---

## ⚙️ Ключевые оптимизации

### 🚀 Производительность
- **ArrayPool<byte/float>** - нуль аллокаций в аудио пути
- **MediaFoundationResampler** - качественный ресемплинг до 16kHz 
- **MMCSS потоки** - приоритет для критических операций
- **Throttling** - ограничение частоты обновления UI (200мс)
- **Backpressure** - защита от разрастания памяти

### 🔧 Надежность  
- **Горячее переподключение** устройств
- **Exception handling** на каждом уровне
- **Graceful degradation** при ошибках компонентов
- **Resource cleanup** в Dispose patterns
- **Thread-safe** операции через Channels

### 📊 Мониторинг
- **Статистика в реальном времени** каждые 5 секунд
- **Анализ качества аудио** (RMS, клиппинг, спектр)
- **Метрики производительности** (дропы, успешность, латентность)
- **Подробное логирование** всех этапов обработки

---

## 🎮 API интеграции

### Запуск стабильной системы
```csharp
// Инициализация всех компонентов
await stableAudioCapture.StartCaptureAsync();

// События для мониторинга
stableAudioCapture.OnStatusChanged += (status) => LogMessage(status);
slidingWindowAggregator.OnTextReady += async (text) => await ProcessText(text);
stableTtsEngine.OnSpeechCompleted += (text) => LogMessage($"Озвучено: {text}");
```

### Остановка и очистка
```csharp
// Graceful остановка с flush буферов
await stableAudioCapture.StopCaptureAsync();
await slidingWindowAggregator.FlushAsync();
await stableTtsEngine.ClearQueueAsync();

// Автоматическая очистка через IDisposable
stableAudioCapture.Dispose();
```

---

## 📈 Результаты оптимизации

### До оптимизации:
- ❌ Частые дропы аудио при смене устройств
- ❌ Блокировки UI во время обработки  
- ❌ Накладывание TTS озвучек
- ❌ Высокое потребление памяти (GC паузы)
- ❌ Нестабильная работа при высокой нагрузке

### После оптимизации:
- ✅ **Стабильный захват** без дропов и джиттера
- ✅ **Отзывчивый UI** - вся обработка в фоне
- ✅ **Последовательная озвучка** без пересечений
- ✅ **Минимальные GC паузы** через ArrayPool
- ✅ **Автовосстановление** при сбоях устройств

---

## 🛠️ Техническая документация

### Системные требования
- Windows 10+ (для WASAPI Loopback)
- .NET 8.0
- NAudio 2.2.1+
- Whisper.NET
- 4GB+ RAM (для Whisper модели)

### Зависимости
```xml
<PackageReference Include="NAudio" Version="2.2.1" />
<PackageReference Include="NAudio.Wasapi" Version="2.2.1" />
<PackageReference Include="Whisper.net" Version="1.4.7" />
<PackageReference Include="System.Threading.Channels" Version="8.0.0" />
```

### Конфигурация
```csharp
// Настройки производительности
private const int CHANNEL_CAPACITY = 64;           // Размер каналов
private const int TARGET_SAMPLE_RATE = 16000;      // Частота для Whisper
private const int AUDIO_LATENCY_MS = 30;           // Латентность WASAPI
private const float WINDOW_DURATION_SEC = 3.0f;    // Размер окна STT
private const int MAX_TEXT_LENGTH = 500;           // Максимум для TTS

// Пороги активности
private const float RMS_THRESHOLD = 0.001f;        // Порог тишины
private const float SILENCE_THRESHOLD_SEC = 0.6f;  // Пауза для триггера
```

---

## 🚀 Запуск новой архитектуры

1. **Компиляция:**
```bash
dotnet build --configuration Release
```

2. **Запуск:**
- Нажать кнопку "Start Capture" 
- Система автоматически переключится на новую архитектуру
- Мониторинг статистики каждые 5 секунд в логах

3. **Мониторинг:**
- Логи содержат детальную информацию о каждом этапе
- Статистика TTS и агрегатора в реальном времени
- Анализ качества аудио для каждого сегмента

---

*🔧 Архитектура готова к production использованию с кардинально улучшенной стабильностью и производительностью!*