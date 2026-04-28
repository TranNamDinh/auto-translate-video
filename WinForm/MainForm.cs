using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoProcessor
{
    public partial class MainForm : Form
    {
        private string _inputVideo = "";
        private string _workDir = "";
        private CancellationTokenSource? _cts;
        private ProcessingConfig _config = new ProcessingConfig();

        // ── Controls ─────────────────────────────────────
        private TextBox txtInput = new();
        private Button btnBrowse = new();
        private Button btnOutput = new();
        private TextBox txtOutput = new();
        private ComboBox cboSrcLang = new();
        private ComboBox cboTgtLang = new();
        private NumericUpDown nudVolume = new();
        private NumericUpDown nudZoom = new();
        private NumericUpDown nudSpeed = new();
        private NumericUpDown nudVolumeDuck = new();
        private CheckBox chkTranslate = new();
        private CheckBox chkDub = new();
        private CheckBox chkSub = new();
        private CheckBox chkZoom = new();
        private CheckBox chkSpeed = new();
        private RichTextBox rtbLog = new();
        private ProgressBar pgBar = new();
        private Label lblStatus = new();
        private Button btnStart = new();
        private Button btnCancel = new();
        private Button btnSetup = new();
        private Panel pnlTop = new();
        private Panel pnlOptions = new();
        private Panel pnlLog = new();

        public MainForm()
        {
            InitializeComponent();
            BuildUI();
            LoadDefaults();
        }

        private void BuildUI()
        {
            this.Text = "🎬 Video Processor Pro";
            this.Size = new Size(900, 780);
            this.MinimumSize = new Size(800, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9f);
            this.BackColor = Color.FromArgb(22, 22, 30);
            this.ForeColor = Color.FromArgb(220, 220, 230);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1,
                Padding = new Padding(14),
                BackColor = Color.Transparent
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // Input
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 260)); // Options
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // Buttons
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Log
            this.Controls.Add(mainLayout);

            // ── Input Panel ──────────────────────────────
            var grpInput = MakeGroup("📁 Input / Output");
            grpInput.Dock = DockStyle.Fill;

            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 3, Padding = new Padding(8) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            StyleTextBox(txtInput); txtInput.PlaceholderText = "Chọn file video MP4..."; txtInput.ReadOnly = true;
            StyleTextBox(txtOutput); txtOutput.PlaceholderText = "Thư mục xuất (mặc định cùng thư mục input)..."; txtOutput.ReadOnly = true;

            StyleButton(btnBrowse, "📂 Video", Color.FromArgb(60, 100, 180));
            StyleButton(btnOutput, "📁 Output", Color.FromArgb(60, 120, 80));

            btnBrowse.Click += BtnBrowse_Click;
            btnOutput.Click += BtnOutput_Click;

            tbl.Controls.Add(txtInput, 0, 0); tbl.SetColumnSpan(txtInput, 1);
            tbl.Controls.Add(btnBrowse, 1, 0);
            tbl.Controls.Add(new Label { Text = "" }, 2, 0);
            tbl.Controls.Add(txtOutput, 0, 1);
            tbl.Controls.Add(btnOutput, 1, 1);
            tbl.Controls.Add(new Label { Text = "" }, 2, 1);

            grpInput.Controls.Add(tbl);
            mainLayout.Controls.Add(grpInput, 0, 0);

            // ── Options Panel ────────────────────────────
            var grpOpts = MakeGroup("⚙️ Cấu hình xử lý");
            grpOpts.Dock = DockStyle.Fill;
            var optLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 2, Padding = new Padding(8, 4, 8, 4) };
            optLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            optLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 3; i++) optLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));

            // Left col
            var leftPanel = new Panel { Dock = DockStyle.Fill };
            var langRow = MakeLangRow();
            var checkRow = MakeCheckRow();
            leftPanel.Controls.Add(checkRow);
            leftPanel.Controls.Add(langRow);
            langRow.Dock = DockStyle.Bottom; langRow.Height = 70;
            checkRow.Dock = DockStyle.Fill;

            // Right col  
            var rightPanel = new Panel { Dock = DockStyle.Fill };
            var numericPanel = MakeNumericPanel();
            rightPanel.Controls.Add(numericPanel);
            numericPanel.Dock = DockStyle.Fill;

            optLayout.Controls.Add(leftPanel, 0, 0); optLayout.SetRowSpan(leftPanel, 3);
            optLayout.Controls.Add(rightPanel, 1, 0); optLayout.SetRowSpan(rightPanel, 3);
            grpOpts.Controls.Add(optLayout);
            mainLayout.Controls.Add(grpOpts, 0, 1);

            // ── Buttons Panel ────────────────────────────
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 4)
            };

            StyleButton(btnSetup, "🔧 Cài đặt Dependencies", Color.FromArgb(100, 60, 150));
            StyleButton(btnStart, "▶  Bắt đầu xử lý", Color.FromArgb(40, 140, 80));
            StyleButton(btnCancel, "⏹  Dừng", Color.FromArgb(160, 60, 60));
            btnSetup.Width = 200; btnStart.Width = 180; btnCancel.Width = 120;
            btnCancel.Enabled = false;

            btnSetup.Click += BtnSetup_Click;
            btnStart.Click += BtnStart_Click;
            btnCancel.Click += BtnCancel_Click;

            // Progress
            pgBar.Dock = DockStyle.Fill;
            pgBar.Style = ProgressBarStyle.Marquee;
            pgBar.Visible = false;
            pgBar.ForeColor = Color.FromArgb(60, 180, 100);
            pgBar.BackColor = Color.FromArgb(40, 40, 50);

            lblStatus.AutoSize = false;
            lblStatus.Dock = DockStyle.Right;
            lblStatus.Width = 200;
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.ForeColor = Color.FromArgb(150, 200, 150);
            lblStatus.Text = "Sẵn sàng";

            btnPanel.Controls.AddRange(new Control[] { btnSetup, btnStart, btnCancel });
            mainLayout.Controls.Add(btnPanel, 0, 2);

            // ── Log Panel ────────────────────────────────
            var grpLog = MakeGroup("📋 Log");
            grpLog.Dock = DockStyle.Fill;

            rtbLog.Dock = DockStyle.Fill;
            rtbLog.BackColor = Color.FromArgb(12, 12, 18);
            rtbLog.ForeColor = Color.FromArgb(180, 230, 180);
            rtbLog.Font = new Font("Consolas", 8.5f);
            rtbLog.ReadOnly = true;
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;

            var logBottom = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.FromArgb(18, 18, 26) };
            logBottom.Controls.Add(lblStatus);
            logBottom.Controls.Add(pgBar);
            pgBar.Dock = DockStyle.Fill;

            grpLog.Controls.Add(rtbLog);
            grpLog.Controls.Add(logBottom);
            mainLayout.Controls.Add(grpLog, 0, 3);
        }

        //private Panel MakeLangRow()
        //{
        //    var panel = new Panel { Padding = new Padding(0, 4, 0, 0) };
        //    string[] langs = { "auto", "vi", "en", "zh", "ja", "ko", "fr", "de", "es", "ru", "th", "id", "pt" };

        //    var lblSrc = new Label { Text = "Ngôn ngữ gốc:", ForeColor = Color.FromArgb(170, 170, 200), AutoSize = true, Location = new Point(0, 8) };
        //    cboSrcLang.Items.AddRange(langs);
        //    cboSrcLang.SelectedItem = "auto";
        //    cboSrcLang.BackColor = Color.FromArgb(35, 35, 48);
        //    cboSrcLang.ForeColor = Color.White;
        //    cboSrcLang.FlatStyle = FlatStyle.Flat;
        //    cboSrcLang.Width = 80; cboSrcLang.Location = new Point(120, 5);
        //    cboSrcLang.DropDownStyle = ComboBoxStyle.DropDownList;

        //    var lblTgt = new Label { Text = "Dịch sang:", ForeColor = Color.FromArgb(170, 170, 200), AutoSize = true, Location = new Point(210, 8) };
        //    cboTgtLang.Items.AddRange(langs);
        //    cboTgtLang.SelectedItem = "vi";
        //    cboTgtLang.BackColor = Color.FromArgb(35, 35, 48);
        //    cboTgtLang.ForeColor = Color.White;
        //    cboTgtLang.FlatStyle = FlatStyle.Flat;
        //    cboTgtLang.Width = 80; cboTgtLang.Location = new Point(295, 5);
        //    cboTgtLang.DropDownStyle = ComboBoxStyle.DropDownList;

        //    panel.Controls.AddRange(new Control[] { lblSrc, cboSrcLang, lblTgt, cboTgtLang });
        //    return panel;
        //}

        private Panel MakeLangRow()
        {
            // Dùng FlowLayoutPanel để tự động căn dòng, chống lỗi đè giao diện khi Scale màn hình
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0),
                AutoSize = true // Tự động co giãn theo nội dung
            };

            string[] langs = { "auto", "vi", "en", "zh", "ja", "ko", "fr", "de", "es", "ru", "th", "id", "pt" };

            var lblSrc = new Label
            {
                Text = "Ngôn ngữ gốc:",
                ForeColor = Color.FromArgb(170, 170, 200),
                AutoSize = true,
                Margin = new Padding(0, 8, 5, 0) // Căn lề trái/phải để không dính sát vào ô chọn
            };

            cboSrcLang.Items.AddRange(langs);
            cboSrcLang.SelectedItem = "auto";
            cboSrcLang.BackColor = Color.FromArgb(35, 35, 48);
            cboSrcLang.ForeColor = Color.White;
            cboSrcLang.FlatStyle = FlatStyle.Flat;
            cboSrcLang.Width = 75;
            cboSrcLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSrcLang.Margin = new Padding(0, 5, 15, 0); // Đẩy nút Dịch sang cách xa 15px

            var lblTgt = new Label
            {
                Text = "Dịch sang:",
                ForeColor = Color.FromArgb(170, 170, 200),
                AutoSize = true,
                Margin = new Padding(0, 8, 5, 0)
            };

            cboTgtLang.Items.AddRange(langs);
            cboTgtLang.SelectedItem = "vi";
            cboTgtLang.BackColor = Color.FromArgb(35, 35, 48);
            cboTgtLang.ForeColor = Color.White;
            cboTgtLang.FlatStyle = FlatStyle.Flat;
            cboTgtLang.Width = 75;
            cboTgtLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTgtLang.Margin = new Padding(0, 5, 0, 0);

            panel.Controls.AddRange(new Control[] { lblSrc, cboSrcLang, lblTgt, cboTgtLang });
            return panel;
        }

        private Panel MakeCheckRow()
        {
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = false, Padding = new Padding(4) };

            chkSub = MakeCheck("📝 Thêm phụ đề (nền đen, chữ trắng)", true);
            chkTranslate = MakeCheck("🌐 Dịch phụ đề sang ngôn ngữ đích", true);
            chkDub = MakeCheck("🔊 Lồng tiếng (Edge-TTS, miễn phí)", true);
            chkZoom = MakeCheck("🔍 Zoom 130%", true);
            chkSpeed = MakeCheck("⚡ Tua 1.3x", true);

            chkTranslate.CheckedChanged += (s, e) => cboTgtLang.Enabled = chkTranslate.Checked;
            chkDub.CheckedChanged += (s, e) => { nudVolumeDuck.Enabled = chkDub.Checked; };

            panel.Controls.AddRange(new Control[] { chkSub, chkTranslate, chkDub, chkZoom, chkSpeed });
            return panel;
        }

        private Panel MakeNumericPanel()
        {
            var panel = new TableLayoutPanel { RowCount = 4, ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(8) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            for (int i = 0; i < 4; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            // Volume duck
            nudVolumeDuck.Minimum = -60; nudVolumeDuck.Maximum = 0;
            nudVolumeDuck.Value = -20; nudVolumeDuck.DecimalPlaces = 0;
            StyleNumeric(nudVolumeDuck);

            // Zoom
            nudZoom.Minimum = 100; nudZoom.Maximum = 300;
            nudZoom.Value = 130; nudZoom.DecimalPlaces = 0;
            nudZoom.Increment = 5;
            StyleNumeric(nudZoom);

            // Speed
            nudSpeed.Minimum = new decimal(0.5); nudSpeed.Maximum = 3;
            nudSpeed.Value = new decimal(1.3); nudSpeed.DecimalPlaces = 2;
            nudSpeed.Increment = new decimal(0.1);
            StyleNumeric(nudSpeed);

            panel.Controls.Add(MakeLabel("Volume gốc (dB):"), 0, 0);
            panel.Controls.Add(nudVolumeDuck, 1, 0);
            panel.Controls.Add(MakeLabel("Zoom (%):"), 0, 1);
            panel.Controls.Add(nudZoom, 1, 1);
            panel.Controls.Add(MakeLabel("Tốc độ (x):"), 0, 2);
            panel.Controls.Add(nudSpeed, 1, 2);
            panel.Controls.Add(MakeLabel(""), 0, 3);
            panel.Controls.Add(new Label(), 1, 3);

            return panel;
        }

        // ── Helpers ──────────────────────────────────────
        private GroupBox MakeGroup(string title)
        {
            var g = new GroupBox { Text = title, ForeColor = Color.FromArgb(140, 180, 255), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            g.BackColor = Color.FromArgb(28, 28, 40);
            return g;
        }

        private void StyleTextBox(TextBox tb)
        {
            tb.BackColor = Color.FromArgb(35, 35, 48);
            tb.ForeColor = Color.FromArgb(210, 210, 230);
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Dock = DockStyle.Fill;
            tb.Margin = new Padding(2);
        }

        private void StyleButton(Button btn, string text, Color bg)
        {
            btn.Text = text;
            btn.BackColor = bg;
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Height = 34;
            btn.Margin = new Padding(4, 2, 4, 2);
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        }

        private CheckBox MakeCheck(string text, bool chk)
        {
            var c = new CheckBox { Text = text, Checked = chk, ForeColor = Color.FromArgb(200, 210, 230), AutoSize = true, Margin = new Padding(2, 4, 2, 4) };
            return c;
        }

        private Label MakeLabel(string text)
        {
            return new Label { Text = text, ForeColor = Color.FromArgb(170, 175, 200), TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        }

        private void StyleNumeric(NumericUpDown n)
        {
            n.BackColor = Color.FromArgb(35, 35, 48);
            n.ForeColor = Color.White;
            n.Dock = DockStyle.Fill;
        }

        private void LoadDefaults()
        {
            // Defaults already set in controls
        }

        // ── Events ───────────────────────────────────────
        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.webm|All files|*.*", Title = "Chọn file video" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                _inputVideo = ofd.FileName;
                txtInput.Text = _inputVideo;
                if (string.IsNullOrEmpty(txtOutput.Text))
                    txtOutput.Text = Path.GetDirectoryName(_inputVideo) ?? "";
            }
        }

        private void BtnOutput_Click(object? sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog { Description = "Chọn thư mục xuất video" };
            if (fbd.ShowDialog() == DialogResult.OK)
                txtOutput.Text = fbd.SelectedPath;
        }

        private async void BtnSetup_Click(object? sender, EventArgs e)
        {
            btnSetup.Enabled = false;
            Log("Đang cài đặt dependencies Python...", LogLevel.Info);
            try
            {
                var scriptPath = GetPythonScript("setup.py");
                await RunPythonAsync(scriptPath, "", CancellationToken.None);
                Log("✓ Cài đặt hoàn tất!", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log($"Lỗi cài đặt: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                btnSetup.Enabled = true;
            }
        }

        private async void BtnStart_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_inputVideo) || !File.Exists(_inputVideo))
            {
                MessageBox.Show("Vui lòng chọn file video hợp lệ!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _cts = new CancellationTokenSource();
            btnStart.Enabled = false;
            btnCancel.Enabled = true;
            pgBar.Visible = true;
            pgBar.Style = ProgressBarStyle.Marquee;
            rtbLog.Clear();

            try
            {
                await ProcessVideoAsync(_cts.Token);
                Log("✅ Xử lý hoàn tất!", LogLevel.Success);
                SetStatus("Hoàn tất ✓");
            }
            catch (OperationCanceledException)
            {
                Log("⏹ Đã dừng xử lý.", LogLevel.Warn);
                SetStatus("Đã dừng");
            }
            catch (Exception ex)
            {
                Log($"❌ Lỗi: {ex.Message}", LogLevel.Error);
                SetStatus("Lỗi");
            }
            finally
            {
                btnStart.Enabled = true;
                btnCancel.Enabled = false;
                pgBar.Visible = false;
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            _cts?.Cancel();
        }

        // ── Main Processing ──────────────────────────────
        private async Task ProcessVideoAsync(CancellationToken ct)
        {
            //var safeInput = Path.Combine(_workDir, "input.mp4");
            //Log("Copy video to safe path...", LogLevel.Info);
            //File.Copy(_inputVideo, safeInput, true);
            //_inputVideo = safeInput;

            //var outDir = txtOutput.Text.Trim();
            //if (string.IsNullOrEmpty(outDir))
            //    outDir = Path.GetDirectoryName(_inputVideo)!;

            //Directory.CreateDirectory(outDir);

            //_workDir = Path.Combine(Path.GetTempPath(), "VideoProc_" + Guid.NewGuid().ToString("N")[..8]);
            //Directory.CreateDirectory(_workDir);
            //Log($"Work dir: {_workDir}", LogLevel.Debug);

            //var baseName = Path.GetFileNameWithoutExtension(_inputVideo);
            ////var outputPath = Path.Combine(outDir, baseName + "_processed.mp4");

            //// Thay vi dung ten file goc lam output
            //var outputPath = Path.Combine(outDir, baseName + "_processed.mp4");

            //// Dung safe path render xong roi copy
            //var safeOutput = Path.Combine(_workDir, "output.mp4");

            // 1. Tao work dir TRUOC
            _workDir = Path.Combine(Path.GetTempPath(), "VideoProc_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_workDir);
            Log($"Work dir: {_workDir}", LogLevel.Debug);

            // 2. Copy input vao safe path
            var safeInput = Path.Combine(_workDir, "input.mp4");
            Log("Copy video to safe path...", LogLevel.Info);
            File.Copy(_inputVideo, safeInput, true);

            // 3. Output paths
            var outDir = txtOutput.Text.Trim();
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.GetDirectoryName(_inputVideo)!;
            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(_inputVideo);
            var outputPath = Path.Combine(outDir, baseName + "_processed.mp4");
            var safeOutput = Path.Combine(_workDir, "output.mp4");

            // ... dung safeOutput trong RenderFinalVideoAsync ...

            // Sau khi render xong
            //File.Copy(safeOutput, outputPath, true);
            Log($"Saved to: {outputPath}", LogLevel.Success);

            // Step 1: Get video info
            Log("📊 Đọc thông tin video...", LogLevel.Info);
            var videoInfo = await GetVideoInfoAsync(_inputVideo, ct);
            Log($"   Duration: {videoInfo.Duration:F2}s | {videoInfo.Width}x{videoInfo.Height} | {videoInfo.Fps}fps", LogLevel.Debug);

            string srtPath = "";
            string dubbedAudioPath = "";
            string segmentsJson = Path.Combine(_workDir, "segments.json");

            if (chkSub.Checked || chkDub.Checked)
            {
                // Step 2: Extract audio
                Log("🎵 Trích xuất audio...", LogLevel.Info);
                SetStatus("Trích xuất audio...");
                var audioPath = Path.Combine(_workDir, "audio.wav");
                ct.ThrowIfCancellationRequested();
                await ExtractAudioAsync(_inputVideo, audioPath, ct);

                // Step 3: Transcribe
                Log("🎤 Đang nhận dạng giọng nói (Whisper)...", LogLevel.Info);
                SetStatus("Nhận dạng giọng nói...");
                ct.ThrowIfCancellationRequested();
                var srcLang = cboSrcLang.SelectedItem?.ToString() == "auto" ? "" : cboSrcLang.SelectedItem?.ToString() ?? "";
                var tgtLang = cboTgtLang.SelectedItem?.ToString() ?? "vi";

                var transcribeArgs = $"--input \"{_inputVideo}\" --audio-extract \"{audioPath}\" --transcribe " +
                    $"--segments-json \"{segmentsJson}\" " +
                    (srcLang.Length > 0 ? $"--src-lang {srcLang} " : "");

                await RunPythonScriptAsync("video_processor.py", transcribeArgs, ct);
                Log("✓ Nhận dạng hoàn tất", LogLevel.Success);

                // Step 4: Translate
                if (chkTranslate.Checked)
                {
                    Log($"🌐 Dịch sang {tgtLang}...", LogLevel.Info);
                    SetStatus("Đang dịch...");
                    ct.ThrowIfCancellationRequested();
                    var translateArgs = $"--input \"{_inputVideo}\" --translate --tgt-lang {tgtLang} " +
                        $"--segments-json \"{segmentsJson}\"";
                    await RunPythonScriptAsync("video_processor.py", translateArgs, ct);
                    Log("✓ Dịch hoàn tất", LogLevel.Success);
                }

                // Write SRT
                if (chkSub.Checked)
                {
                    srtPath = Path.Combine(_workDir, "subtitles.srt");
                    var srtArgs = $"--input \"{_inputVideo}\" --srt-output \"{srtPath}\" " +
                        $"--tgt-lang {tgtLang} --segments-json \"{segmentsJson}\"";
                    await RunPythonScriptAsync("video_processor.py", srtArgs, ct);
                    Log($"✓ SRT: {srtPath}", LogLevel.Success);
                }

                // Step 5: TTS
                if (chkDub.Checked)
                {
                    Log("🔊 Tạo lồng tiếng (Edge-TTS)...", LogLevel.Info);
                    SetStatus("Tạo lồng tiếng...");
                    ct.ThrowIfCancellationRequested();
                    var ttsDir = Path.Combine(_workDir, "tts_segments");
                    dubbedAudioPath = Path.Combine(_workDir, "dubbed.aac");
                    var ttsArgs = $"--input \"{_inputVideo}\" --tts --tts-dir \"{ttsDir}\" " +
                        $"--dubbed-audio \"{dubbedAudioPath}\" --video-duration {videoInfo.Duration:F3} " +
                        $"--tgt-lang {tgtLang} --segments-json \"{segmentsJson}\"";
                    await RunPythonScriptAsync("video_processor.py", ttsArgs, ct);
                    Log("✓ Lồng tiếng hoàn tất", LogLevel.Success);
                }
            }

            // Step 6: FFmpeg final render
            Log("🎬 Xử lý video cuối (FFmpeg)...", LogLevel.Info);
            SetStatus("Render video...");
            ct.ThrowIfCancellationRequested();
            await RenderFinalVideoAsync(_inputVideo, srtPath, dubbedAudioPath, safeOutput, videoInfo, ct);

            // Copy output ve dung ten goc
            if (File.Exists(safeOutput))
            {
                File.Copy(safeOutput, outputPath, true);
                Log($"✓ Saved: {outputPath}", LogLevel.Success);
            }
            else
            {
                throw new Exception("FFmpeg không tạo được file output. Xem log đỏ phía trên.");
            }

            // Mo thu muc
            if (File.Exists(outputPath))
            {
                var result = MessageBox.Show(
                    $"Xử lý xong!\n\nOutput: {outputPath}\n\nMở thư mục chứa file?",
                    "Hoàn tất", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (result == DialogResult.Yes)
                    Process.Start("explorer.exe", $"/select,\"{outputPath}\"");
            }

            // Cleanup
            try { Directory.Delete(_workDir, true); } catch { }
        }

        // ── FFmpeg Helpers ───────────────────────────────
        private async Task<VideoInfo> GetVideoInfoAsync(string videoPath, CancellationToken ct)
        {
            //var args = $"-v quiet -print_format json -show_streams -show_format \"{videoPath}\"";
            //var output = await RunProcessAsync("ffprobe", args, ct, captureOutput: true);
            //return ParseVideoInfo(output);

            var psi = new ProcessStartInfo("ffprobe")
            {
                Arguments = $"-v quiet -print_format json -show_streams -show_format \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // THÊM 2 DÒNG NÀY VÀO:
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            var output = new System.Text.StringBuilder();
            var process = Process.Start(psi)!;

            while (!process.StandardOutput.EndOfStream)
                output.Append(await process.StandardOutput.ReadToEndAsync());

            await process.WaitForExitAsync(ct);

            var json = output.ToString();
            Log($"ffprobe output length: {json.Length}", LogLevel.Debug);

            if (string.IsNullOrEmpty(json))
            {
                Log("ffprobe tra ve rong, dung gia tri mac dinh", LogLevel.Warn);
                return new VideoInfo { Duration = 60, Width = 1920, Height = 1080, Fps = 30 };
            }

            return ParseVideoInfo(json);
        }

        private VideoInfo ParseVideoInfo(string json)
        {
            var info = new VideoInfo();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("format", out var fmt))
                    info.Duration = double.Parse(fmt.GetProperty("duration").GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("codec_type", out var ct2) && ct2.GetString() == "video")
                        {
                            info.Width = stream.GetProperty("width").GetInt32();
                            info.Height = stream.GetProperty("height").GetInt32();
                            if (stream.TryGetProperty("r_frame_rate", out var fps))
                            {
                                var parts = fps.GetString()!.Split('/');
                                if (parts.Length == 2)
                                    info.Fps = double.Parse(parts[0]) / double.Parse(parts[1]);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Parse video info error: {ex.Message}", LogLevel.Warn);
            }
            return info;
        }

        private async Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken ct)
        {
            await RunProcessAsync("ffmpeg", $"-y -i \"{videoPath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{audioPath}\"", ct);
        }

        private async Task RenderFinalVideoAsync(string inputPath, string srtPath, string dubbedAudioPath, string outputPath, VideoInfo info, CancellationToken ct)
        {
            var speed = (double)nudSpeed.Value;
            var zoom = (double)nudZoom.Value / 100.0;
            var volumeDb = (double)nudVolumeDuck.Value;

            var vfParts = new List<string>();

            // 1. CROP & ZOOM (Làm đầu tiên)
            if (chkZoom.Checked)
            {
                int cropW = (int)Math.Round(info.Width / zoom);
                int cropH = (int)Math.Round(info.Height / zoom);
                if (cropW % 2 != 0) cropW--;
                if (cropH % 2 != 0) cropH--;
                int cropX = (info.Width - cropW) / 2;
                int cropY = (info.Height - cropH) / 2;
                if (cropX % 2 != 0) cropX--;
                if (cropY % 2 != 0) cropY--;
                vfParts.Add($"crop={cropW}:{cropH}:{cropX}:{cropY},scale={info.Width}:{info.Height}");
            }

            // 2. ÉP SUB VÀO VIDEO (Làm khi video chưa bị tua nhanh)
            if (chkSub.Checked && File.Exists(srtPath))
            {
                var assPath = Path.Combine(Path.GetTempPath(), "vp_sub.ass");
                ConvertSrtToAss(srtPath, assPath);
                var escapedAss = assPath.Replace("\\", "/").Replace(":", "\\:");
                vfParts.Add($"ass=filename='{escapedAss}'");
            }

            // 3. TUA NHANH VIDEO (Làm cuối cùng cho phần hình)
            if (chkSpeed.Checked && speed != 1.0)
            {
                vfParts.Add($"setpts={(1.0 / speed).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}*PTS");
            }

            string vf = vfParts.Count > 0 ? string.Join(",", vfParts) : "null";

            // 4. XỬ LÝ ÂM THANH
            double linearVol = Math.Pow(10, volumeDb / 20.0);
            string volFilter = $"volume={linearVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}";
            string speedFilter = (chkSpeed.Checked && speed != 1.0) ? $"atempo={speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}" : "";

            bool hasDub = chkDub.Checked && File.Exists(dubbedAudioPath);

            var cmd = new System.Text.StringBuilder();
            cmd.Append($"-y -i \"{inputPath}\" ");
            if (hasDub) cmd.Append($"-i \"{dubbedAudioPath}\" ");

            string videoFilter = vf != "null" ? $"[0:v]{vf}[vout]; " : "";
            string mapV = vf != "null" ? "\"[vout]\"" : "0:v";

            if (hasDub)
            {
                // Trộn TTS vào âm thanh gốc TRƯỚC, rồi mới tua nhanh toàn bộ (atempo)
                string atempoPart = !string.IsNullOrEmpty(speedFilter) ? $",{speedFilter}" : "";
                cmd.Append($"-filter_complex \"{videoFilter}[0:a]{volFilter}[orig];[orig][1:a]amix=inputs=2:duration=first:dropout_transition=0:normalize=0{atempoPart}[aout]\" ");
                cmd.Append($"-map {mapV} -map \"[aout]\" ");
            }
            else
            {
                string af = volFilter;
                if (!string.IsNullOrEmpty(speedFilter)) af += $",{speedFilter}";

                if (vf != "null")
                {
                    cmd.Append($"-filter_complex \"{videoFilter}\" -map \"[vout]\" -map 0:a -af \"{af}\" ");
                }
                else
                {
                    cmd.Append($"-map 0:v -map 0:a -af \"{af}\" ");
                }
            }

            cmd.Append($"-c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"");

            Log($"FFmpeg args: {cmd}", LogLevel.Debug);
            await RunProcessAsync("ffmpeg", cmd.ToString(), ct);
        }

        //    private async Task RenderFinalVideoAsync(string inputPath, string srtPath, string dubbedAudioPath, string outputPath, VideoInfo info, CancellationToken ct)
        //    {
        //        var speed = (double)nudSpeed.Value;
        //        var zoom = (double)nudZoom.Value / 100.0;
        //        var volumeDb = (double)nudVolumeDuck.Value;

        //        var vfParts = new List<string>();

        //        if (chkZoom.Checked)
        //        {
        //            // CapCut Scale 130% (zoom = 1.3) nghĩa là khung hình thực tế chỉ lấy 1/1.3 (~76.92%) diện tích ở giữa.

        //            // 1. Tính toán kích thước vùng trung tâm cần giữ lại
        //            int cropW = (int)Math.Round(info.Width / zoom);
        //            int cropH = (int)Math.Round(info.Height / zoom);

        //            // H.264 yêu cầu kích thước phải là số chẵn
        //            if (cropW % 2 != 0) cropW--;
        //            if (cropH % 2 != 0) cropH--;

        //            // 2. Tính toán tọa độ x, y để cắt đúng từ tâm
        //            int cropX = (info.Width - cropW) / 2;
        //            int cropY = (info.Height - cropH) / 2;

        //            // Đảm bảo tọa độ cắt cũng là số chẵn
        //            if (cropX % 2 != 0) cropX--;
        //            if (cropY % 2 != 0) cropY--;

        //            // 3. Lệnh FFmpeg: Crop trước, Scale sau
        //            vfParts.Add($"crop={cropW}:{cropH}:{cropX}:{cropY},scale={info.Width}:{info.Height}");
        //        }

        //        // Speed
        //        if (chkSpeed.Checked && speed != 1.0)
        //            vfParts.Add($"setpts={(1.0 / speed).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}*PTS");

        //        // Subtitle - dung ASS thay vi SRT de tranh loi force_style
        //        if (chkSub.Checked && File.Exists(srtPath))
        //        {
        //            // Convert SRT -> ASS voi style tuy chinh
        //            var assPath = Path.Combine(Path.GetTempPath(), "vp_sub.ass");
        //            ConvertSrtToAss(srtPath, assPath);

        //            //var escapedAss = assPath.Replace("\\", "/");
        //            var escapedAss = assPath
        //.Replace("\\", "/")
        //.Replace(":", "\\:");
        //            vfParts.Add($"ass=filename='{escapedAss}'");
        //        }

        //        string vf = vfParts.Count > 0 ? string.Join(",", vfParts) : "null";

        //        // Audio
        //        var afParts = new List<string>();
        //        afParts.Add($"volume={volumeDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}dB");
        //        if (chkSpeed.Checked && speed != 1.0)
        //            afParts.Add($"atempo={speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        //        string originalAf = string.Join(",", afParts);

        //        bool hasDub = chkDub.Checked && File.Exists(dubbedAudioPath);

        //        var cmd = new System.Text.StringBuilder();
        //        cmd.Append($"-y -i \"{inputPath}\" ");
        //        if (hasDub) cmd.Append($"-i \"{dubbedAudioPath}\" ");
        //        cmd.Append($"-vf \"{vf}\" ");

        //        if (hasDub)
        //        {
        //            cmd.Append($"-filter_complex \"[0:a]{originalAf}[orig];[orig][1:a]amix=inputs=2:normalize=0[aout]\" ");
        //            cmd.Append($"-map 0:v -map \"[aout]\" ");
        //        }
        //        else
        //        {
        //            cmd.Append($"-af \"{originalAf}\" ");
        //        }

        //        cmd.Append($"-c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"");

        //        Log($"FFmpeg args: {cmd}", LogLevel.Debug);
        //        await RunProcessAsync("ffmpeg", cmd.ToString(), ct);
        //    }

        private void ConvertSrtToAss(string srtPath, string assPath)
        {
            // ASS header voi style nen den chu trang can giua duoi
            var assHeader = @"[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
Collisions: Normal

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,45,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,3,1,0,2,10,10,40,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
";

            var lines = File.ReadAllLines(srtPath, System.Text.Encoding.UTF8);
            var events = new System.Text.StringBuilder();
            events.Append(assHeader);

            int i = 0;
            while (i < lines.Length)
            {
                // Skip index number
                if (int.TryParse(lines[i].Trim(), out _)) i++;
                else { i++; continue; }

                if (i >= lines.Length) break;

                // Timestamp
                var timeLine = lines[i].Trim(); i++;
                if (!timeLine.Contains("-->")) continue;

                var times = timeLine.Split(new string[] { " --> " }, StringSplitOptions.None);
                if (times.Length < 2) continue;

                string start = SrtTimeToAss(times[0].Trim());
                string end = SrtTimeToAss(times[1].Trim());

                // Text lines
                var textLines = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    textLines.Add(lines[i].Trim());
                    i++;
                }
                while (i < lines.Length && lines[i].Trim().Length == 0) i++;

                var text = string.Join("\\N", textLines);
                events.AppendLine($"Dialogue: 0,{start},{end},Default,,0,0,0,,{text}");
            }

            File.WriteAllText(assPath, events.ToString(), System.Text.Encoding.UTF8);
            Log($"ASS subtitle: {assPath}", LogLevel.Debug);
        }

        private string SrtTimeToAss(string srtTime)
        {
            // SRT: 00:00:01,234 -> ASS: 0:00:01.23
            var t = srtTime.Replace(",", ".");
            var parts = t.Split(':');
            if (parts.Length < 3) return "0:00:00.00";
            int h = int.Parse(parts[0]);
            int m = int.Parse(parts[1]);
            var secMs = parts[2].Split('.');
            int s = int.Parse(secMs[0]);
            int ms = secMs.Length > 1 ? int.Parse(secMs[1][..Math.Min(2, secMs[1].Length)]) : 0;
            return $"{h}:{m:D2}:{s:D2}.{ms:D2}";
        }

        // ── Process Runners ──────────────────────────────
        private string GetPythonScript(string scriptName)
        {
            // Look for Python scripts relative to exe
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(exeDir, "Python", scriptName),
                Path.Combine(exeDir, "..", "Python", scriptName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scriptName),
                scriptName
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return Path.GetFullPath(c);
            return Path.Combine(exeDir, "Python", scriptName);
        }

        private async Task RunPythonScriptAsync(string scriptName, string args, CancellationToken ct)
        {
            var scriptPath = GetPythonScript(scriptName);
            await RunPythonAsync(scriptPath, args, ct);
        }

        private async Task RunPythonAsync(string scriptPath, string args, CancellationToken ct)
        {
            var pythonExe = FindPython();
            await RunProcessAsync(pythonExe, $"\"{scriptPath}\" {args}", ct);
        }

        private string FindPython()
        {
            foreach (var candidate in new[] { "python", "python3", "py" })
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(2000);
                    if (p?.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return "python";
        }

        private async Task<string> RunProcessAsync(string exe, string args, CancellationToken ct, bool captureOutput = false)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var tcs = new TaskCompletionSource<string>();
            var outputBuilder = new System.Text.StringBuilder();

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    if (!captureOutput) LogFromProcess(e.Data);
                }
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null && e.Data.Trim().Length > 0)
                    LogFromProcess(e.Data, isError: e.Data.Contains("Error") || e.Data.Contains("error"));
            };
            process.Exited += (s, e) =>
            {
                tcs.TrySetResult(outputBuilder.ToString());
            };

            ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                tcs.TrySetCanceled();
            });

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var result = await tcs.Task;

            if (process.ExitCode != 0 && !ct.IsCancellationRequested)
                throw new Exception($"Process '{exe}' exited with code {process.ExitCode}");

            return result;
        }

        // ── Logging ──────────────────────────────────────
        enum LogLevel { Debug, Info, Warn, Error, Success }

        private void Log(string msg, LogLevel level = LogLevel.Info)
        {
            if (InvokeRequired) { Invoke(() => Log(msg, level)); return; }

            var color = level switch
            {
                LogLevel.Debug => Color.FromArgb(120, 130, 150),
                LogLevel.Info => Color.FromArgb(180, 210, 255),
                LogLevel.Warn => Color.FromArgb(255, 200, 100),
                LogLevel.Error => Color.FromArgb(255, 100, 100),
                LogLevel.Success => Color.FromArgb(100, 220, 130),
                _ => Color.FromArgb(200, 200, 200)
            };

            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = Color.FromArgb(100, 120, 140);
            rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
            rtbLog.SelectionColor = color;
            rtbLog.AppendText(msg + "\n");
            rtbLog.ScrollToCaret();
        }

        private void LogFromProcess(string msg, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            if (InvokeRequired) { Invoke(() => LogFromProcess(msg, isError)); return; }
            Log(msg.Trim(), isError ? LogLevel.Error : LogLevel.Debug);
        }

        private void SetStatus(string msg)
        {
            if (InvokeRequired) { Invoke(() => SetStatus(msg)); return; }
            lblStatus.Text = msg;
        }
    }

    public class ProcessingConfig
    {
        public bool AddSubtitle { get; set; } = true;
        public bool Translate { get; set; } = true;
        public bool Dub { get; set; } = true;
        public bool Zoom { get; set; } = true;
        public bool Speed { get; set; } = true;
        public string SrcLang { get; set; } = "auto";
        public string TgtLang { get; set; } = "vi";
        public double ZoomPercent { get; set; } = 130;
        public double SpeedFactor { get; set; } = 1.3;
        public double VolumeDb { get; set; } = -20;
    }

    public class VideoInfo
    {
        public double Duration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
    }
}
