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
            this.btnStartCapture = new Button();
            this.btnStopCapture = new Button();
            this.lblStatus = new Label();
            this.txtRecognizedText = new TextBox();
            this.txtTranslatedText = new TextBox();
            this.lblRecognized = new Label();
            this.lblTranslated = new Label();
            this.progressBar = new ProgressBar();
            this.cbSpeakerDevices = new ComboBox();
            this.lblSpeakers = new Label();
            this.txtLogs = new TextBox();
            this.lblLogs = new Label();
            this.cbSourceLang = new ComboBox();
            this.cbTargetLang = new ComboBox();
            this.lblSourceLang = new Label();
            this.lblTargetLang = new Label();
            this.numThreshold = new NumericUpDown();
            this.lblThreshold = new Label();
            this.btnTestTTS = new Button();
            this.chkAutoTranslate = new CheckBox();
            this.lblAudioLevel = new Label();
            this.progressAudioLevel = new ProgressBar();
            this.cbProcessingMode = new ComboBox();
            this.lblProcessingMode = new Label();
            ((System.ComponentModel.ISupportInitialize)(this.numThreshold)).BeginInit();
            this.SuspendLayout();
            // 
            // btnStartCapture
            // 
            this.btnStartCapture.BackColor = Color.LightGreen;
            this.btnStartCapture.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            this.btnStartCapture.Location = new Point(12, 12);
            this.btnStartCapture.Name = "btnStartCapture";
            this.btnStartCapture.Size = new Size(150, 40);
            this.btnStartCapture.TabIndex = 0;
            this.btnStartCapture.Text = "🎧 Начать захват";
            this.btnStartCapture.UseVisualStyleBackColor = false;
            this.btnStartCapture.Click += this.btnStartCapture_Click;
            // 
            // btnStopCapture
            // 
            this.btnStopCapture.BackColor = Color.LightCoral;
            this.btnStopCapture.Enabled = false;
            this.btnStopCapture.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            this.btnStopCapture.Location = new Point(180, 12);
            this.btnStopCapture.Name = "btnStopCapture";
            this.btnStopCapture.Size = new Size(150, 40);
            this.btnStopCapture.TabIndex = 1;
            this.btnStopCapture.Text = "⏹️ Остановить";
            this.btnStopCapture.UseVisualStyleBackColor = false;
            this.btnStopCapture.Click += this.btnStopCapture_Click;
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            this.lblStatus.ForeColor = Color.Blue;
            this.lblStatus.Location = new Point(350, 22);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new Size(130, 19);
            this.lblStatus.TabIndex = 2;
            this.lblStatus.Text = "🔇 Готов к захвату";
            // 
            // cbSpeakerDevices
            // 
            this.cbSpeakerDevices.DropDownStyle = ComboBoxStyle.DropDownList;
            this.cbSpeakerDevices.Location = new Point(120, 70);
            this.cbSpeakerDevices.Name = "cbSpeakerDevices";
            this.cbSpeakerDevices.Size = new Size(350, 23);
            this.cbSpeakerDevices.TabIndex = 3;
            // 
            // lblSpeakers
            // 
            this.lblSpeakers.AutoSize = true;
            this.lblSpeakers.Location = new Point(12, 73);
            this.lblSpeakers.Name = "lblSpeakers";
            this.lblSpeakers.Size = new Size(102, 15);
            this.lblSpeakers.TabIndex = 4;
            this.lblSpeakers.Text = "🔊 Устройство:";
            // 
            // cbSourceLang
            // 
            this.cbSourceLang.DropDownStyle = ComboBoxStyle.DropDownList;
            this.cbSourceLang.Location = new Point(120, 110);
            this.cbSourceLang.Name = "cbSourceLang";
            this.cbSourceLang.Size = new Size(120, 23);
            this.cbSourceLang.TabIndex = 5;
            // 
            // lblSourceLang
            // 
            this.lblSourceLang.AutoSize = true;
            this.lblSourceLang.Location = new Point(12, 113);
            this.lblSourceLang.Name = "lblSourceLang";
            this.lblSourceLang.Size = new Size(92, 15);
            this.lblSourceLang.TabIndex = 6;
            this.lblSourceLang.Text = "🌍 Из языка:";
            // 
            // cbTargetLang
            // 
            this.cbTargetLang.DropDownStyle = ComboBoxStyle.DropDownList;
            this.cbTargetLang.Location = new Point(350, 110);
            this.cbTargetLang.Name = "cbTargetLang";
            this.cbTargetLang.Size = new Size(120, 23);
            this.cbTargetLang.TabIndex = 7;
            // 
            // lblTargetLang
            // 
            this.lblTargetLang.AutoSize = true;
            this.lblTargetLang.Location = new Point(260, 113);
            this.lblTargetLang.Name = "lblTargetLang";
            this.lblTargetLang.Size = new Size(84, 15);
            this.lblTargetLang.TabIndex = 8;
            this.lblTargetLang.Text = "🎯 На язык:";
            // 
            // numThreshold
            // 
            this.numThreshold.DecimalPlaces = 3;
            this.numThreshold.Increment = new decimal(new int[] { 5, 0, 0, 196608 });
            this.numThreshold.Location = new Point(600, 110);
            this.numThreshold.Maximum = new decimal(new int[] { 1, 0, 0, 0 });
            this.numThreshold.Minimum = new decimal(new int[] { 1, 0, 0, 196608 });
            this.numThreshold.Name = "numThreshold";
            this.numThreshold.Size = new Size(80, 23);
            this.numThreshold.TabIndex = 9;
            this.numThreshold.Value = new decimal(new int[] { 50, 0, 0, 196608 }); // 0.050
            // 
            // lblThreshold
            // 
            this.lblThreshold.AutoSize = true;
            this.lblThreshold.Location = new Point(490, 113);
            this.lblThreshold.Name = "lblThreshold";
            this.lblThreshold.Size = new Size(104, 15);
            this.lblThreshold.TabIndex = 10;
            this.lblThreshold.Text = "🎚️ Порог звука:";
            // 
            // lblAudioLevel
            // 
            this.lblAudioLevel.AutoSize = true;
            this.lblAudioLevel.Location = new Point(500, 73);
            this.lblAudioLevel.Name = "lblAudioLevel";
            this.lblAudioLevel.Size = new Size(94, 15);
            this.lblAudioLevel.TabIndex = 11;
            this.lblAudioLevel.Text = "📊 Уровень: 0%";
            // 
            // progressAudioLevel
            // 
            this.progressAudioLevel.Location = new Point(600, 70);
            this.progressAudioLevel.Name = "progressAudioLevel";
            this.progressAudioLevel.Size = new Size(180, 23);
            this.progressAudioLevel.TabIndex = 12;
            // 
            // chkAutoTranslate
            // 
            this.chkAutoTranslate.AutoSize = true;
            this.chkAutoTranslate.Checked = true;
            this.chkAutoTranslate.CheckState = CheckState.Checked;
            this.chkAutoTranslate.Location = new Point(300, 150);
            this.chkAutoTranslate.Name = "chkAutoTranslate";
            this.chkAutoTranslate.Size = new Size(151, 19);
            this.chkAutoTranslate.TabIndex = 13;
            this.chkAutoTranslate.Text = "🔄 Автоперевод + TTS";
            this.chkAutoTranslate.UseVisualStyleBackColor = true;
            // 
            // cbProcessingMode
            // 
            this.cbProcessingMode.DropDownStyle = ComboBoxStyle.DropDownList;
            this.cbProcessingMode.Location = new Point(120, 147);
            this.cbProcessingMode.Name = "cbProcessingMode";
            this.cbProcessingMode.Size = new Size(160, 23);
            this.cbProcessingMode.TabIndex = 22;
            // 
            // lblProcessingMode
            // 
            this.lblProcessingMode.AutoSize = true;
            this.lblProcessingMode.Location = new Point(12, 150);
            this.lblProcessingMode.Name = "lblProcessingMode";
            this.lblProcessingMode.Size = new Size(102, 15);
            this.lblProcessingMode.TabIndex = 23;
            this.lblProcessingMode.Text = "⚙️ Режим работы:";
            // 
            // btnTestTTS
            // 
            this.btnTestTTS.Location = new Point(180, 147);
            this.btnTestTTS.Name = "btnTestTTS";
            this.btnTestTTS.Size = new Size(100, 25);
            this.btnTestTTS.TabIndex = 14;
            this.btnTestTTS.Text = "🔊 Тест TTS";
            this.btnTestTTS.UseVisualStyleBackColor = true;
            this.btnTestTTS.Click += this.btnTestTTS_Click;
            // 
            // lblRecognized
            // 
            this.lblRecognized.AutoSize = true;
            this.lblRecognized.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.lblRecognized.Location = new Point(12, 185);
            this.lblRecognized.Name = "lblRecognized";
            this.lblRecognized.Size = new Size(143, 15);
            this.lblRecognized.TabIndex = 15;
            this.lblRecognized.Text = "🎤 Распознанный текст:";
            // 
            // txtRecognizedText
            // 
            this.txtRecognizedText.BackColor = Color.LightYellow;
            this.txtRecognizedText.Font = new Font("Segoe UI", 10F);
            this.txtRecognizedText.Location = new Point(12, 205);
            this.txtRecognizedText.Multiline = true;
            this.txtRecognizedText.Name = "txtRecognizedText";
            this.txtRecognizedText.ReadOnly = true;
            this.txtRecognizedText.ScrollBars = ScrollBars.Vertical;
            this.txtRecognizedText.Size = new Size(380, 80);
            this.txtRecognizedText.TabIndex = 16;
            this.txtRecognizedText.Text = "Ожидание распознавания...";
            // 
            // lblTranslated
            // 
            this.lblTranslated.AutoSize = true;
            this.lblTranslated.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.lblTranslated.Location = new Point(400, 185);
            this.lblTranslated.Name = "lblTranslated";
            this.lblTranslated.Size = new Size(132, 15);
            this.lblTranslated.TabIndex = 17;
            this.lblTranslated.Text = "🌐 Переведенный текст:";
            // 
            // txtTranslatedText
            // 
            this.txtTranslatedText.BackColor = Color.LightCyan;
            this.txtTranslatedText.Font = new Font("Segoe UI", 10F);
            this.txtTranslatedText.Location = new Point(400, 205);
            this.txtTranslatedText.Multiline = true;
            this.txtTranslatedText.Name = "txtTranslatedText";
            this.txtTranslatedText.ReadOnly = true;
            this.txtTranslatedText.ScrollBars = ScrollBars.Vertical;
            this.txtTranslatedText.Size = new Size(380, 80);
            this.txtTranslatedText.TabIndex = 18;
            this.txtTranslatedText.Text = "Ожидание перевода...";
            // 
            // progressBar
            // 
            this.progressBar.Location = new Point(12, 300);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new Size(768, 23);
            this.progressBar.Style = ProgressBarStyle.Marquee;
            this.progressBar.TabIndex = 19;
            this.progressBar.Visible = false;
            // 
            // lblLogs
            // 
            this.lblLogs.AutoSize = true;
            this.lblLogs.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.lblLogs.Location = new Point(12, 335);
            this.lblLogs.Name = "lblLogs";
            this.lblLogs.Size = new Size(122, 15);
            this.lblLogs.TabIndex = 20;
            this.lblLogs.Text = "📝 Логи обработки:";
            // 
            // txtLogs
            // 
            this.txtLogs.BackColor = Color.Black;
            this.txtLogs.Font = new Font("Consolas", 9F);
            this.txtLogs.ForeColor = Color.Lime;
            this.txtLogs.Location = new Point(12, 355);
            this.txtLogs.Multiline = true;
            this.txtLogs.Name = "txtLogs";
            this.txtLogs.ReadOnly = true;
            this.txtLogs.ScrollBars = ScrollBars.Vertical;
            this.txtLogs.Size = new Size(768, 200);
            this.txtLogs.TabIndex = 21;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(800, 570);
            this.Controls.Add(this.txtLogs);
            this.Controls.Add(this.lblLogs);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.txtTranslatedText);
            this.Controls.Add(this.lblTranslated);
            this.Controls.Add(this.txtRecognizedText);
            this.Controls.Add(this.lblRecognized);
            this.Controls.Add(this.btnTestTTS);
            this.Controls.Add(this.lblProcessingMode);
            this.Controls.Add(this.cbProcessingMode);
            this.Controls.Add(this.chkAutoTranslate);
            this.Controls.Add(this.progressAudioLevel);
            this.Controls.Add(this.lblAudioLevel);
            this.Controls.Add(this.lblThreshold);
            this.Controls.Add(this.numThreshold);
            this.Controls.Add(this.lblTargetLang);
            this.Controls.Add(this.cbTargetLang);
            this.Controls.Add(this.lblSourceLang);
            this.Controls.Add(this.cbSourceLang);
            this.Controls.Add(this.lblSpeakers);
            this.Controls.Add(this.cbSpeakerDevices);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnStopCapture);
            this.Controls.Add(this.btnStartCapture);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "🎧 Speaker STT → Translate → TTS Tester";
            this.FormClosing += this.Form1_FormClosing;
            ((System.ComponentModel.ISupportInitialize)(this.numThreshold)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
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
    }
}
