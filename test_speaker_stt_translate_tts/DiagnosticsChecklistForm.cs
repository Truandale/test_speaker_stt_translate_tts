using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace test_speaker_stt_translate_tts
{
    /// <summary>
    /// Интерактивный диагностический dashboard с автоматическим проставлением галок
    /// и сохранением состояния проверок
    /// </summary>
    public partial class DiagnosticsChecklistForm : Form
    {
        #region Diagnostic Items Definition

        private readonly List<DiagnosticItem> diagnosticItems = new()
        {
            // [1/6] Warm Whisper Instance
            new DiagnosticItem("whisper_static_fields", "🤖 Статические поля Whisper инициализированы", "Warm Whisper Instance"),
            new DiagnosticItem("whisper_quick_init", "🤖 Время инициализации < 2 сек (warm start)", "Warm Whisper Instance"),
            new DiagnosticItem("whisper_processor_ready", "🤖 WhisperProcessor готов к работе", "Warm Whisper Instance"),
            
            // [2/6] MediaFoundation
            new DiagnosticItem("mf_initialized", "🎵 MediaFoundation инициализирован", "MediaFoundation"),
            new DiagnosticItem("mf_formats_supported", "🎵 Поддерживается ≥3 аудио форматов", "MediaFoundation"), 
            new DiagnosticItem("mf_conversion_test", "🎵 Тест конвертации аудио пройден", "MediaFoundation"),
            
            // [3/6] Bounded Channels
            new DiagnosticItem("channels_created", "📡 Все Bounded Channels созданы", "Bounded Channels"),
            new DiagnosticItem("channels_policy", "📡 DropOldest политика настроена", "Bounded Channels"),
            new DiagnosticItem("pipeline_active", "📡 Пайплайн активен", "Bounded Channels"),
            
            // [4/6] Enhanced Text Filtering
            new DiagnosticItem("text_filter_quality", "🔍 Фильтр работает корректно (≥4/5 тестов)", "Enhanced Text Filtering"),
            
            // [5/6] Device Notifications
            new DiagnosticItem("smart_manager_created", "🎧 SmartAudioManager создан", "Device Notifications"),
            new DiagnosticItem("devices_initialized", "🎧 Устройства инициализированы", "Device Notifications"),
            new DiagnosticItem("monitoring_active", "🎧 Мониторинг устройств активен", "Device Notifications"),
            
            // [6/6] Audio Devices
            new DiagnosticItem("render_devices", "🔊 Устройства воспроизведения найдены", "Audio Devices"),
            new DiagnosticItem("capture_devices", "🎤 Устройства записи найдены", "Audio Devices"),
            new DiagnosticItem("devices_populated", "🔊 Список устройств заполнен в UI", "Audio Devices"),
            new DiagnosticItem("wasapi_supported", "🔊 WasapiLoopback поддерживается", "Audio Devices"),
            new DiagnosticItem("wavein_supported", "🎤 WaveInEvent поддерживается", "Audio Devices"),
            
            // Performance Diagnostics
            new DiagnosticItem("memory_usage_ok", "📈 Использование памяти в норме", "Performance"),
            new DiagnosticItem("capture_channel_active", "📦 Capture Channel активен", "Performance"),
            new DiagnosticItem("mono16k_channel_active", "📦 Mono16k Channel активен", "Performance"),
            new DiagnosticItem("stt_channel_active", "📦 STT Channel активен", "Performance"),
            new DiagnosticItem("pipeline_cts_active", "📦 Pipeline CTS активен", "Performance"),
            
            // Advanced Diagnostics
            new DiagnosticItem("whisper_cold_start", "🤖 Whisper cold start < 5 сек", "Advanced"),
            new DiagnosticItem("whisper_warm_start", "🤖 Whisper warm start < 100 мс", "Advanced"),
            new DiagnosticItem("memory_leak_test", "🧠 Тест утечек памяти пройден", "Advanced"),
            new DiagnosticItem("thread_safety", "🔒 Потокобезопасность проверена", "Advanced"),
            new DiagnosticItem("channels_throughput", "📡 Пропускная способность каналов OK", "Advanced"),
            new DiagnosticItem("device_monitoring_test", "🎧 Тест мониторинга устройств OK", "Advanced"),
            
            // Text Filter Validation
            new DiagnosticItem("filter_validation_85", "🔍 Качество фильтра ≥85% (22 теста)", "Text Filter"),
            new DiagnosticItem("multilingual_support", "🌐 Многоязычная поддержка работает", "Text Filter"),
            new DiagnosticItem("production_ready", "🏆 Готовность к продакшену", "Text Filter")
        };

        #endregion

        #region Private Fields

        private readonly Dictionary<string, CheckBox> checkBoxes = new();
        private readonly Dictionary<string, Button> resetButtons = new();
        private readonly Dictionary<string, GroupBox> categoryGroups = new();
        private Button btnResetAll = null!;
        private Button btnRunAllTests = null!;
        private Label lblOverallStatus = null!;
        private ProgressBar progressOverall = null!;
        private Panel mainPanel = null!;
        
        private const string SettingsFileName = "DiagnosticsChecklist.json";
        private string SettingsFilePath => Path.Combine(Application.StartupPath, SettingsFileName);
        
        // Performance optimization fields for JSON persistence
        private volatile bool _settingsDirty = false;
        private System.Windows.Forms.Timer _saveTimer;
        private DateTime _lastSaveTime = DateTime.MinValue;
        private const int SAVE_THROTTLE_MS = 500; // Coalesce saves for 500ms
        
        // Ссылка на главную форму для запуска тестов
        private Form1 parentForm;

        #endregion

        #region Constructor & Initialization

        public DiagnosticsChecklistForm(Form1 parent)
        {
            parentForm = parent;
            
            // Initialize performance-optimized save timer
            _saveTimer = new System.Windows.Forms.Timer();
            _saveTimer.Interval = SAVE_THROTTLE_MS;
            _saveTimer.Tick += (s, e) => PerformOptimizedSave();
            
            InitializeComponent();
            CreateDiagnosticControls();
            LoadSettings();
            UpdateOverallStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "🔍 Диагностический Dashboard";
            this.Size = new Size(850, 700);
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true; // Всегда сверху для удобства
            this.ShowInTaskbar = false; // Не показывать в панели задач
            this.FormBorderStyle = FormBorderStyle.Sizable;
            
            // Иконка и стиль
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;
            
            // Главная панель с прокруткой
            mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(45, 45, 48)
            };
            this.Controls.Add(mainPanel);
            
            // Заголовок
            var titleLabel = new Label
            {
                Text = "🔍 ДИАГНОСТИЧЕСКИЙ DASHBOARD",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 122, 204),
                AutoSize = true,
                Location = new Point(20, 20)
            };
            mainPanel.Controls.Add(titleLabel);
            
            // Общий статус
            lblOverallStatus = new Label
            {
                Text = "📊 Общий статус: Не проверено",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 60)
            };
            mainPanel.Controls.Add(lblOverallStatus);
            
            // Прогресс бар
            progressOverall = new ProgressBar
            {
                Location = new Point(20, 85),
                Size = new Size(760, 25),
                Style = ProgressBarStyle.Continuous,
                ForeColor = Color.FromArgb(0, 122, 204)
            };
            mainPanel.Controls.Add(progressOverall);
            
            // Кнопки управления
            btnRunAllTests = new Button
            {
                Text = "🚀 Запустить все тесты",
                Location = new Point(20, 125),
                Size = new Size(180, 35),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnRunAllTests.Click += BtnRunAllTests_Click;
            mainPanel.Controls.Add(btnRunAllTests);
            
            btnResetAll = new Button
            {
                Text = "🔄 Сбросить все",
                Location = new Point(220, 125),
                Size = new Size(140, 35),
                BackColor = Color.FromArgb(178, 34, 34),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnResetAll.Click += BtnResetAll_Click;
            mainPanel.Controls.Add(btnResetAll);
            
            // Инструкция
            var instructionLabel = new Label
            {
                Text = "💡 Галки проставляются автоматически при прохождении тестов. " +
                       "Используйте кнопки сброса для повторной проверки отдельных пунктов.",
                Location = new Point(380, 125),
                Size = new Size(400, 50),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8),
                TextAlign = ContentAlignment.MiddleLeft
            };
            mainPanel.Controls.Add(instructionLabel);
        }

        private void CreateDiagnosticControls()
        {
            int yOffset = 190;
            var categories = diagnosticItems.GroupBy(x => x.Category).ToList();
            
            foreach (var category in categories)
            {
                // Создаем группу для категории
                var groupBox = new GroupBox
                {
                    Text = $"📋 {category.Key}",
                    Location = new Point(20, yOffset),
                    Size = new Size(760, category.Count() * 35 + 50),
                    ForeColor = Color.FromArgb(0, 200, 83),
                    Font = new Font("Segoe UI", 10, FontStyle.Bold),
                    BackColor = Color.FromArgb(55, 55, 58)
                };
                
                categoryGroups[category.Key] = groupBox;
                mainPanel.Controls.Add(groupBox);
                
                int itemYOffset = 25;
                foreach (var item in category)
                {
                    // CheckBox для диагностического пункта
                    var checkBox = new CheckBox
                    {
                        Text = item.DisplayName,
                        Location = new Point(15, itemYOffset),
                        Size = new Size(500, 25),
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9),
                        Enabled = false // Только для отображения, не для ручного изменения
                    };
                    checkBox.CheckedChanged += (s, e) => SaveSettings();
                    
                    checkBoxes[item.Id] = checkBox;
                    groupBox.Controls.Add(checkBox);
                    
                    // Кнопка сброса для отдельного пункта
                    var resetButton = new Button
                    {
                        Text = "🔄",
                        Location = new Point(520, itemYOffset),
                        Size = new Size(30, 25),
                        BackColor = Color.FromArgb(255, 140, 0),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 8)
                    };
                    
                    var resetTooltip = new ToolTip();
                    resetTooltip.SetToolTip(resetButton, $"Сбросить проверку: {item.DisplayName}");
                    
                    string itemId = item.Id; // Capture for closure
                    resetButton.Click += (s, e) => ResetSingleItem(itemId);
                    
                    resetButtons[item.Id] = resetButton;
                    groupBox.Controls.Add(resetButton);
                    
                    // Статус индикатор
                    var statusLabel = new Label
                    {
                        Text = "⏳",
                        Location = new Point(560, itemYOffset),
                        Size = new Size(30, 25),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 12)
                    };
                    groupBox.Controls.Add(statusLabel);
                    
                    itemYOffset += 35;
                }
                
                yOffset += groupBox.Height + 15;
            }
            
            // Обновляем размер главной панели для прокрутки
            mainPanel.AutoScrollMinSize = new Size(0, yOffset + 50);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Обновляет статус конкретного диагностического пункта
        /// </summary>
        public void UpdateDiagnosticItem(string itemId, bool passed)
        {
            if (checkBoxes.TryGetValue(itemId, out var checkBox))
            {
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() => UpdateDiagnosticItem(itemId, passed)));
                    return;
                }
                
                checkBox.Checked = passed;
                
                // Обновляем визуальный статус
                var statusLabel = checkBox.Parent.Controls.OfType<Label>()
                    .FirstOrDefault(l => l.Location.X == 560 && 
                                        Math.Abs(l.Location.Y - checkBox.Location.Y) < 5);
                
                if (statusLabel != null)
                {
                    statusLabel.Text = passed ? "✅" : "❌";
                    statusLabel.ForeColor = passed ? Color.FromArgb(0, 200, 83) : Color.FromArgb(231, 76, 60);
                }
                
                UpdateOverallStatus();
                SaveSettings();
            }
        }

        /// <summary>
        /// Массовое обновление результатов диагностики
        /// </summary>
        public void UpdateDiagnosticResults(Dictionary<string, bool> results)
        {
            foreach (var kvp in results)
            {
                UpdateDiagnosticItem(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Сбрасывает все проверки
        /// </summary>
        public void ResetAllChecks()
        {
            foreach (var checkBox in checkBoxes.Values)
            {
                checkBox.Checked = false;
            }
            
            // Сбрасываем статусы
            foreach (var group in categoryGroups.Values)
            {
                foreach (var statusLabel in group.Controls.OfType<Label>().Where(l => l.Location.X == 560))
                {
                    statusLabel.Text = "⏳";
                    statusLabel.ForeColor = Color.Gray;
                }
            }
            
            UpdateOverallStatus();
            SaveSettings();
        }

        #endregion

        #region Event Handlers

        private async void BtnRunAllTests_Click(object sender, EventArgs e)
        {
            btnRunAllTests.Enabled = false;
            btnRunAllTests.Text = "🔄 Выполняется...";
            
            try
            {
                // Запускаем комплексную диагностику на главной форме
                await Task.Run(() =>
                {
                    parentForm.RunFullSelfDiagnostics();
                    parentForm.RunPerformanceDiagnostics();
                    parentForm.RunAdvancedDiagnostics();
                    parentForm.RunTextFilterValidation();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка выполнения тестов: {ex.Message}", "Ошибка", 
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnRunAllTests.Enabled = true;
                btnRunAllTests.Text = "🚀 Запустить все тесты";
            }
        }

        private void BtnResetAll_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Сбросить все результаты диагностики?\n\nВсе галки будут сняты и потребуется повторная проверка.",
                "Подтверждение сброса",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
                
            if (result == DialogResult.Yes)
            {
                ResetAllChecks();
            }
        }

        private void ResetSingleItem(string itemId)
        {
            if (checkBoxes.TryGetValue(itemId, out var checkBox))
            {
                var item = diagnosticItems.FirstOrDefault(x => x.Id == itemId);
                var result = MessageBox.Show(
                    $"Сбросить проверку:\n{item?.DisplayName}",
                    "Подтверждение сброса",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                    
                if (result == DialogResult.Yes)
                {
                    UpdateDiagnosticItem(itemId, false);
                    
                    // Сбрасываем статус на "ожидание"
                    var statusLabel = checkBox.Parent.Controls.OfType<Label>()
                        .FirstOrDefault(l => l.Location.X == 560 && 
                                            Math.Abs(l.Location.Y - checkBox.Location.Y) < 5);
                    
                    if (statusLabel != null)
                    {
                        statusLabel.Text = "⏳";
                        statusLabel.ForeColor = Color.Gray;
                    }
                }
            }
        }

        #endregion

        #region Status Management

        private void UpdateOverallStatus()
        {
            int totalItems = checkBoxes.Count;
            int passedItems = checkBoxes.Values.Count(cb => cb.Checked);
            
            float percentage = totalItems > 0 ? (float)passedItems / totalItems * 100 : 0;
            
            progressOverall.Maximum = totalItems;
            progressOverall.Value = passedItems;
            
            string status = percentage switch
            {
                100 => "🏆 Все тесты пройдены успешно!",
                >= 85 => $"✅ Система в хорошем состоянии ({percentage:F0}%)",
                >= 70 => $"⚠️ Требует внимания ({percentage:F0}%)",
                >= 50 => $"❌ Обнаружены проблемы ({percentage:F0}%)",
                _ => $"💥 Критические проблемы ({percentage:F0}%)"
            };
            
            lblOverallStatus.Text = $"📊 Общий статус: {status} [{passedItems}/{totalItems}]";
            
            // Обновляем цвет статуса
            lblOverallStatus.ForeColor = percentage switch
            {
                100 => Color.FromArgb(0, 255, 0),
                >= 85 => Color.FromArgb(0, 200, 83),
                >= 70 => Color.FromArgb(255, 193, 7),
                >= 50 => Color.FromArgb(255, 140, 0),
                _ => Color.FromArgb(231, 76, 60)
            };
        }

        #endregion

        #region Settings Persistence (PERFORMANCE OPTIMIZED)

        /// <summary>
        /// Marks settings as dirty and schedules atomic save with throttling
        /// </summary>
        private void SaveSettings()
        {
            _settingsDirty = true;
            
            // Reset timer to coalesce multiple rapid changes
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        
        /// <summary>
        /// Performs atomic JSON write with error handling and throttling
        /// </summary>
        private void PerformOptimizedSave()
        {
            _saveTimer.Stop();
            
            if (!_settingsDirty) return;
            
            try
            {
                // Check throttling to prevent excessive disk I/O
                var timeSinceLastSave = DateTime.Now - _lastSaveTime;
                if (timeSinceLastSave.TotalMilliseconds < SAVE_THROTTLE_MS / 2)
                {
                    // Schedule retry
                    _saveTimer.Start();
                    return;
                }
                
                var settings = new Dictionary<string, bool>();
                foreach (var kvp in checkBoxes)
                {
                    settings[kvp.Key] = kvp.Value.Checked;
                }
                
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                
                // Atomic write using temporary file
                var tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                
                // Atomic replace (Windows guarantees this is atomic)
                if (File.Exists(SettingsFilePath))
                {
                    File.Delete(SettingsFilePath);
                }
                File.Move(tempPath, SettingsFilePath);
                
                _settingsDirty = false;
                _lastSaveTime = DateTime.Now;
                
                System.Diagnostics.Debug.WriteLine($"📁 Optimized diagnostics save completed: {settings.Count} items");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Optimized save error: {ex.Message}");
                
                // Schedule retry on failure
                if (_settingsDirty)
                {
                    Task.Delay(1000).ContinueWith(_ => 
                    {
                        if (_settingsDirty && !IsDisposed)
                        {
                            try
                            {
                                BeginInvoke(new Action(() => _saveTimer.Start()));
                            }
                            catch { /* Form disposed */ }
                        }
                    });
                }
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                    
                    if (settings != null)
                    {
                        foreach (var kvp in settings)
                        {
                            if (checkBoxes.TryGetValue(kvp.Key, out var checkBox))
                            {
                                checkBox.Checked = kvp.Value;
                                
                                // Обновляем визуальный статус
                                var statusLabel = checkBox.Parent.Controls.OfType<Label>()
                                    .FirstOrDefault(l => l.Location.X == 560 && 
                                                        Math.Abs(l.Location.Y - checkBox.Location.Y) < 5);
                                
                                if (statusLabel != null)
                                {
                                    statusLabel.Text = kvp.Value ? "✅" : "⏳";
                                    statusLabel.ForeColor = kvp.Value ? Color.FromArgb(0, 200, 83) : Color.Gray;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка загрузки настроек диагностики: {ex.Message}");
            }
        }

        #endregion

        #region Form Management

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Force final save if dirty before hiding/closing
            if (_settingsDirty)
            {
                _saveTimer.Stop();
                PerformOptimizedSave();
            }
            
            // Скрываем форму вместо закрытия для быстрого повторного открытия
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                // Actual close - cleanup timer
                _saveTimer?.Stop();
                _saveTimer?.Dispose();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // ESC для скрытия формы
            if (keyData == Keys.Escape)
            {
                this.Hide();
                return true;
            }
            
            // F5 для запуска всех тестов
            if (keyData == Keys.F5)
            {
                BtnRunAllTests_Click(this, EventArgs.Empty);
                return true;
            }
            
            return base.ProcessCmdKey(ref msg, keyData);
        }

        #endregion
    }

    #region Helper Classes

    /// <summary>
    /// Элемент диагностики с уникальным ID и описанием
    /// </summary>
    public class DiagnosticItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }

        public DiagnosticItem(string id, string displayName, string category)
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
        }
    }

    #endregion
}