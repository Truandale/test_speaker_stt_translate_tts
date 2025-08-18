namespace test_speaker_stt_translate_tts
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnStartCapture = new Button();
            btnStopCapture = new Button();
            lblStatus = new Label();
            txtRecognizedText = new TextBox();
            txtTranslatedText = new TextBox();
            lblRecognized = new Label();
            lblTranslated = new Label();
            progressBar = new ProgressBar();
            cbSpeakerDevices = new ComboBox();
            lblSpeakers = new Label();
            txtLogs = new TextBox();
            lblLogs = new Label();
            cbSourceLang = new ComboBox();
            cbTargetLang = new ComboBox();
            lblSourceLang = new Label();
            lblTargetLang = new Label();
            numThreshold = new NumericUpDown();
            lblThreshold = new Label();
            btnTestTTS = new Button();
            chkAutoTranslate = new CheckBox();
            lblAudioLevel = new Label();
            progressAudioLevel = new ProgressBar();
            cbProcessingMode = new ComboBox();
            lblProcessingMode = new Label();
            lblStats = new Label();
            chkInfiniteTests = new CheckBox();
            btnTestingGuide = new Button();
            btnDiagnostics = new Button();
            btnPerfDiag = new Button();
            btnAdvancedDiag = new Button();
            btnTextFilterValidation = new Button();
            btnAllDiag = new Button();
            btnEmergencyStop = new Button();
            ((System.ComponentModel.ISupportInitialize)numThreshold).BeginInit();
            SuspendLayout();
            // 
            // btnStartCapture
            // 
            btnStartCapture.BackColor = Color.LightGreen;
            btnStartCapture.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            btnStartCapture.Location = new Point(15, 15);
            btnStartCapture.Margin = new Padding(4, 4, 4, 4);
            btnStartCapture.Name = "btnStartCapture";
            btnStartCapture.Size = new Size(188, 50);
            btnStartCapture.TabIndex = 0;
            btnStartCapture.Text = "🎧 Начать захват";
            btnStartCapture.UseVisualStyleBackColor = false;
            btnStartCapture.Click += btnStartCapture_Click;
            // 
            // btnStopCapture
            // 
            btnStopCapture.BackColor = Color.LightCoral;
            btnStopCapture.Enabled = false;
            btnStopCapture.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            btnStopCapture.Location = new Point(225, 15);
            btnStopCapture.Margin = new Padding(4, 4, 4, 4);
            btnStopCapture.Name = "btnStopCapture";
            btnStopCapture.Size = new Size(188, 50);
            btnStopCapture.TabIndex = 1;
            btnStopCapture.Text = "⏹️ Остановить";
            btnStopCapture.UseVisualStyleBackColor = false;
            btnStopCapture.Click += btnStopCapture_Click;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            lblStatus.ForeColor = Color.Blue;
            lblStatus.Location = new Point(15, 68);
            lblStatus.Margin = new Padding(4, 0, 4, 0);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(167, 23);
            lblStatus.TabIndex = 2;
            lblStatus.Text = "🔇 Готов к захвату";
            // 
            // txtRecognizedText
            // 
            txtRecognizedText.BackColor = Color.LightYellow;
            txtRecognizedText.Font = new Font("Segoe UI", 10F);
            txtRecognizedText.Location = new Point(15, 256);
            txtRecognizedText.Margin = new Padding(4, 4, 4, 4);
            txtRecognizedText.Multiline = true;
            txtRecognizedText.Name = "txtRecognizedText";
            txtRecognizedText.ReadOnly = true;
            txtRecognizedText.ScrollBars = ScrollBars.Vertical;
            txtRecognizedText.Size = new Size(474, 99);
            txtRecognizedText.TabIndex = 16;
            txtRecognizedText.Text = "Ожидание распознавания...";
            // 
            // txtTranslatedText
            // 
            txtTranslatedText.BackColor = Color.LightCyan;
            txtTranslatedText.Font = new Font("Segoe UI", 10F);
            txtTranslatedText.Location = new Point(500, 256);
            txtTranslatedText.Margin = new Padding(4, 4, 4, 4);
            txtTranslatedText.Multiline = true;
            txtTranslatedText.Name = "txtTranslatedText";
            txtTranslatedText.ReadOnly = true;
            txtTranslatedText.ScrollBars = ScrollBars.Vertical;
            txtTranslatedText.Size = new Size(474, 99);
            txtTranslatedText.TabIndex = 18;
            txtTranslatedText.Text = "Ожидание перевода...";
            // 
            // lblRecognized
            // 
            lblRecognized.AutoSize = true;
            lblRecognized.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblRecognized.Location = new Point(15, 231);
            lblRecognized.Margin = new Padding(4, 0, 4, 0);
            lblRecognized.Name = "lblRecognized";
            lblRecognized.Size = new Size(185, 20);
            lblRecognized.TabIndex = 15;
            lblRecognized.Text = "🎤 Распознанный текст:";
            // 
            // lblTranslated
            // 
            lblTranslated.AutoSize = true;
            lblTranslated.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblTranslated.Location = new Point(500, 231);
            lblTranslated.Margin = new Padding(4, 0, 4, 0);
            lblTranslated.Name = "lblTranslated";
            lblTranslated.Size = new Size(189, 20);
            lblTranslated.TabIndex = 17;
            lblTranslated.Text = "🌐 Переведенный текст:";
            // 
            // progressBar
            // 
            progressBar.Location = new Point(15, 375);
            progressBar.Margin = new Padding(4, 4, 4, 4);
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(960, 29);
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.TabIndex = 19;
            progressBar.Visible = false;
            // 
            // cbSpeakerDevices
            // 
            cbSpeakerDevices.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSpeakerDevices.Location = new Point(150, 88);
            cbSpeakerDevices.Margin = new Padding(4, 4, 4, 4);
            cbSpeakerDevices.Name = "cbSpeakerDevices";
            cbSpeakerDevices.Size = new Size(436, 28);
            cbSpeakerDevices.TabIndex = 3;
            // 
            // lblSpeakers
            // 
            lblSpeakers.AutoSize = true;
            lblSpeakers.Location = new Point(15, 91);
            lblSpeakers.Margin = new Padding(4, 0, 4, 0);
            lblSpeakers.Name = "lblSpeakers";
            lblSpeakers.Size = new Size(116, 20);
            lblSpeakers.TabIndex = 4;
            lblSpeakers.Text = "🔊 Устройство:";
            // 
            // txtLogs
            // 
            txtLogs.BackColor = Color.Black;
            txtLogs.Font = new Font("Consolas", 9F);
            txtLogs.ForeColor = Color.Lime;
            txtLogs.Location = new Point(15, 444);
            txtLogs.Margin = new Padding(4, 4, 4, 4);
            txtLogs.Multiline = true;
            txtLogs.Name = "txtLogs";
            txtLogs.ReadOnly = true;
            txtLogs.ScrollBars = ScrollBars.Vertical;
            txtLogs.Size = new Size(959, 249);
            txtLogs.TabIndex = 21;
            // 
            // lblLogs
            // 
            lblLogs.AutoSize = true;
            lblLogs.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblLogs.Location = new Point(15, 419);
            lblLogs.Margin = new Padding(4, 0, 4, 0);
            lblLogs.Name = "lblLogs";
            lblLogs.Size = new Size(155, 20);
            lblLogs.TabIndex = 20;
            lblLogs.Text = "📝 Логи обработки:";
            // 
            // cbSourceLang
            // 
            cbSourceLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSourceLang.Location = new Point(150, 138);
            cbSourceLang.Margin = new Padding(4, 4, 4, 4);
            cbSourceLang.Name = "cbSourceLang";
            cbSourceLang.Size = new Size(149, 28);
            cbSourceLang.TabIndex = 5;
            // 
            // cbTargetLang
            // 
            cbTargetLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cbTargetLang.Location = new Point(438, 138);
            cbTargetLang.Margin = new Padding(4, 4, 4, 4);
            cbTargetLang.Name = "cbTargetLang";
            cbTargetLang.Size = new Size(149, 28);
            cbTargetLang.TabIndex = 7;
            // 
            // lblSourceLang
            // 
            lblSourceLang.AutoSize = true;
            lblSourceLang.Location = new Point(15, 141);
            lblSourceLang.Margin = new Padding(4, 0, 4, 0);
            lblSourceLang.Name = "lblSourceLang";
            lblSourceLang.Size = new Size(100, 20);
            lblSourceLang.TabIndex = 6;
            lblSourceLang.Text = "🌍 Из языка:";
            // 
            // lblTargetLang
            // 
            lblTargetLang.AutoSize = true;
            lblTargetLang.Location = new Point(325, 141);
            lblTargetLang.Margin = new Padding(4, 0, 4, 0);
            lblTargetLang.Name = "lblTargetLang";
            lblTargetLang.Size = new Size(93, 20);
            lblTargetLang.TabIndex = 8;
            lblTargetLang.Text = "🎯 На язык:";
            // 
            // numThreshold
            // 
            numThreshold.DecimalPlaces = 3;
            numThreshold.Increment = new decimal(new int[] { 5, 0, 0, 196608 });
            numThreshold.Location = new Point(750, 138);
            numThreshold.Margin = new Padding(4, 4, 4, 4);
            numThreshold.Maximum = new decimal(new int[] { 1, 0, 0, 0 });
            numThreshold.Minimum = new decimal(new int[] { 1, 0, 0, 196608 });
            numThreshold.Name = "numThreshold";
            numThreshold.Size = new Size(100, 27);
            numThreshold.TabIndex = 9;
            numThreshold.Value = new decimal(new int[] { 50, 0, 0, 196608 });
            // 
            // lblThreshold
            // 
            lblThreshold.AutoSize = true;
            lblThreshold.Location = new Point(612, 141);
            lblThreshold.Margin = new Padding(4, 0, 4, 0);
            lblThreshold.Name = "lblThreshold";
            lblThreshold.Size = new Size(122, 20);
            lblThreshold.TabIndex = 10;
            lblThreshold.Text = "🎚️ Порог звука:";
            // 
            // btnTestTTS
            // 
            btnTestTTS.Location = new Point(225, 184);
            btnTestTTS.Margin = new Padding(4, 4, 4, 4);
            btnTestTTS.Name = "btnTestTTS";
            btnTestTTS.Size = new Size(125, 31);
            btnTestTTS.TabIndex = 14;
            btnTestTTS.Text = "🔊 Тест TTS";
            btnTestTTS.UseVisualStyleBackColor = true;
            btnTestTTS.Click += btnTestTTS_Click;
            // 
            // chkAutoTranslate
            // 
            chkAutoTranslate.AutoSize = true;
            chkAutoTranslate.Checked = true;
            chkAutoTranslate.CheckState = CheckState.Checked;
            chkAutoTranslate.Location = new Point(375, 188);
            chkAutoTranslate.Margin = new Padding(4, 4, 4, 4);
            chkAutoTranslate.Name = "chkAutoTranslate";
            chkAutoTranslate.Size = new Size(190, 24);
            chkAutoTranslate.TabIndex = 13;
            chkAutoTranslate.Text = "🔄 Автоперевод + TTS";
            chkAutoTranslate.UseVisualStyleBackColor = true;
            // 
            // lblAudioLevel
            // 
            lblAudioLevel.AutoSize = true;
            lblAudioLevel.Location = new Point(625, 91);
            lblAudioLevel.Margin = new Padding(4, 0, 4, 0);
            lblAudioLevel.Name = "lblAudioLevel";
            lblAudioLevel.Size = new Size(121, 20);
            lblAudioLevel.TabIndex = 11;
            lblAudioLevel.Text = "📊 Уровень: 0%";
            // 
            // progressAudioLevel
            // 
            progressAudioLevel.Location = new Point(750, 88);
            progressAudioLevel.Margin = new Padding(4, 4, 4, 4);
            progressAudioLevel.Name = "progressAudioLevel";
            progressAudioLevel.Size = new Size(225, 29);
            progressAudioLevel.TabIndex = 12;
            // 
            // cbProcessingMode
            // 
            cbProcessingMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cbProcessingMode.Location = new Point(150, 184);
            cbProcessingMode.Margin = new Padding(4, 4, 4, 4);
            cbProcessingMode.Name = "cbProcessingMode";
            cbProcessingMode.Size = new Size(199, 28);
            cbProcessingMode.TabIndex = 22;
            // 
            // lblProcessingMode
            // 
            lblProcessingMode.AutoSize = true;
            lblProcessingMode.Location = new Point(15, 188);
            lblProcessingMode.Margin = new Padding(4, 0, 4, 0);
            lblProcessingMode.Name = "lblProcessingMode";
            lblProcessingMode.Size = new Size(140, 20);
            lblProcessingMode.TabIndex = 23;
            lblProcessingMode.Text = "⚙️ Режим работы:";
            // 
            // lblStats
            // 
            lblStats.AutoSize = true;
            lblStats.Font = new Font("Segoe UI", 8F);
            lblStats.ForeColor = Color.Gray;
            lblStats.Location = new Point(375, 188);
            lblStats.Margin = new Padding(4, 0, 4, 0);
            lblStats.Name = "lblStats";
            lblStats.Size = new Size(202, 19);
            lblStats.TabIndex = 24;
            lblStats.Text = "📊 Статистика: готов к работе";
            // 
            // chkInfiniteTests
            // 
            chkInfiniteTests.AutoSize = true;
            chkInfiniteTests.Font = new Font("Segoe UI", 9F);
            chkInfiniteTests.ForeColor = Color.DarkBlue;
            chkInfiniteTests.Location = new Point(420, 50);
            chkInfiniteTests.Margin = new Padding(4, 3, 4, 3);
            chkInfiniteTests.Name = "chkInfiniteTests";
            chkInfiniteTests.Size = new Size(210, 24);
            chkInfiniteTests.TabIndex = 25;
            chkInfiniteTests.Text = "🔄 Бесконечные тесты";
            chkInfiniteTests.UseVisualStyleBackColor = true;
            chkInfiniteTests.Checked = false;
            // 
            // btnTestingGuide
            // 
            btnTestingGuide.BackColor = Color.LightSteelBlue;
            btnTestingGuide.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnTestingGuide.Location = new Point(642, 44);
            btnTestingGuide.Margin = new Padding(4, 3, 4, 3);
            btnTestingGuide.Name = "btnTestingGuide";
            btnTestingGuide.Size = new Size(120, 35);
            btnTestingGuide.TabIndex = 26;
            btnTestingGuide.Text = "📋 Справочник";
            btnTestingGuide.UseVisualStyleBackColor = false;
            btnTestingGuide.Click += btnTestingGuide_Click;
            // 
            // btnDiagnostics
            // 
            btnDiagnostics.BackColor = Color.LightBlue;
            btnDiagnostics.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnDiagnostics.Location = new Point(420, 12);
            btnDiagnostics.Margin = new Padding(4, 3, 4, 3);
            btnDiagnostics.Name = "btnDiagnostics";
            btnDiagnostics.Size = new Size(120, 30);
            btnDiagnostics.TabIndex = 27;
            btnDiagnostics.Text = "🔍 Диагностика";
            btnDiagnostics.UseVisualStyleBackColor = false;
            btnDiagnostics.Click += btnDiagnostics_Click;
            // 
            // btnPerfDiag
            // 
            btnPerfDiag.BackColor = Color.LightYellow;
            btnPerfDiag.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            btnPerfDiag.Location = new Point(550, 12);
            btnPerfDiag.Margin = new Padding(4, 3, 4, 3);
            btnPerfDiag.Name = "btnPerfDiag";
            btnPerfDiag.Size = new Size(100, 30);
            btnPerfDiag.TabIndex = 28;
            btnPerfDiag.Text = "📊 Performance";
            btnPerfDiag.UseVisualStyleBackColor = false;
            btnPerfDiag.Click += btnPerfDiag_Click;
            // 
            // btnAdvancedDiag
            // 
            btnAdvancedDiag.BackColor = Color.LightCoral;
            btnAdvancedDiag.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            btnAdvancedDiag.Location = new Point(660, 12);
            btnAdvancedDiag.Margin = new Padding(4, 3, 4, 3);
            btnAdvancedDiag.Name = "btnAdvancedDiag";
            btnAdvancedDiag.Size = new Size(100, 30);
            btnAdvancedDiag.TabIndex = 29;
            btnAdvancedDiag.Text = "🔬 Advanced";
            btnAdvancedDiag.UseVisualStyleBackColor = false;
            btnAdvancedDiag.Click += btnAdvancedDiag_Click;
            // 
            // btnTextFilterValidation
            // 
            btnTextFilterValidation.BackColor = Color.LightGreen;
            btnTextFilterValidation.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            btnTextFilterValidation.Location = new Point(770, 12);
            btnTextFilterValidation.Margin = new Padding(4, 3, 4, 3);
            btnTextFilterValidation.Name = "btnTextFilterValidation";
            btnTextFilterValidation.Size = new Size(100, 30);
            btnTextFilterValidation.TabIndex = 30;
            btnTextFilterValidation.Text = "🔍 Text Filter";
            btnTextFilterValidation.UseVisualStyleBackColor = false;
            btnTextFilterValidation.Click += btnTextFilterValidation_Click;
            // 
            // btnAllDiag
            // 
            btnAllDiag.BackColor = Color.DarkSlateBlue;
            btnAllDiag.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            btnAllDiag.ForeColor = Color.White;
            btnAllDiag.Location = new Point(880, 12);
            btnAllDiag.Margin = new Padding(4, 3, 4, 3);
            btnAllDiag.Name = "btnAllDiag";
            btnAllDiag.Size = new Size(100, 30);
            btnAllDiag.TabIndex = 31;
            btnAllDiag.Text = "🎯 Все тесты";
            btnAllDiag.UseVisualStyleBackColor = false;
            btnAllDiag.Click += btnAllDiag_Click;
            // 
            // btnEmergencyStop
            // 
            btnEmergencyStop.BackColor = Color.Red;
            btnEmergencyStop.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            btnEmergencyStop.ForeColor = Color.White;
            btnEmergencyStop.Location = new Point(990, 12);
            btnEmergencyStop.Margin = new Padding(4, 3, 4, 3);
            btnEmergencyStop.Name = "btnEmergencyStop";
            btnEmergencyStop.Size = new Size(80, 30);
            btnEmergencyStop.TabIndex = 32;
            btnEmergencyStop.Text = "🚨 СТОП";
            btnEmergencyStop.UseVisualStyleBackColor = false;
            btnEmergencyStop.Click += btnEmergencyStop_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(120F, 120F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1080, 712);
            Controls.Add(txtLogs);
            Controls.Add(lblLogs);
            Controls.Add(progressBar);
            Controls.Add(txtTranslatedText);
            Controls.Add(lblTranslated);
            Controls.Add(txtRecognizedText);
            Controls.Add(lblRecognized);
            Controls.Add(btnTestTTS);
            Controls.Add(chkInfiniteTests);
            Controls.Add(btnTestingGuide);
            Controls.Add(btnDiagnostics);
            Controls.Add(btnPerfDiag);
            Controls.Add(btnAdvancedDiag);
            Controls.Add(btnTextFilterValidation);
            Controls.Add(btnAllDiag);
            Controls.Add(btnEmergencyStop);
            Controls.Add(lblStats);
            Controls.Add(lblProcessingMode);
            Controls.Add(cbProcessingMode);
            Controls.Add(chkAutoTranslate);
            Controls.Add(progressAudioLevel);
            Controls.Add(lblAudioLevel);
            Controls.Add(lblThreshold);
            Controls.Add(numThreshold);
            Controls.Add(lblTargetLang);
            Controls.Add(cbTargetLang);
            Controls.Add(lblSourceLang);
            Controls.Add(cbSourceLang);
            Controls.Add(lblSpeakers);
            Controls.Add(cbSpeakerDevices);
            Controls.Add(lblStatus);
            Controls.Add(btnStopCapture);
            Controls.Add(btnStartCapture);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Margin = new Padding(4, 4, 4, 4);
            MaximizeBox = false;
            MinimumSize = new Size(996, 701);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "🎧 Speaker STT → Translate → TTS Tester";
            FormClosing += Form1_FormClosing;
            ((System.ComponentModel.ISupportInitialize)numThreshold).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btnStartCapture;
        private Button btnStopCapture;
        private Label lblStatus;
        private TextBox txtRecognizedText;
        private TextBox txtTranslatedText;
        private Label lblRecognized;
        private Label lblTranslated;
        private ProgressBar progressBar;
        private ComboBox cbSpeakerDevices;
        private Label lblSpeakers;
        private TextBox txtLogs;
        private Label lblLogs;
        private ComboBox cbSourceLang;
        private ComboBox cbTargetLang;
        private Label lblSourceLang;
        private Label lblTargetLang;
        private NumericUpDown numThreshold;
        private Label lblThreshold;
        private Button btnTestTTS;
        private CheckBox chkAutoTranslate;
        private Label lblAudioLevel;
        private ProgressBar progressAudioLevel;
        private ComboBox cbProcessingMode;
        private Label lblProcessingMode;
        private Label lblStats;
        private CheckBox chkInfiniteTests;
        private Button btnTestingGuide;
        private Button btnDiagnostics;
        private Button btnPerfDiag;
        private Button btnAdvancedDiag;
        private Button btnTextFilterValidation;
        private Button btnAllDiag;
        private Button btnEmergencyStop;
    }
}
