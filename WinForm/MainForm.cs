using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoProcessor
{
    // ══════════════════════════════════════════════════════════════════════
    //  PROGRESS PROTOCOL — Python script gửi JSON dòng này lên stdout:
    //  {"type":"progress","file":"input.mp4","step":"Transcribe","pct":40}
    //  {"type":"done","file":"input.mp4"}
    //  {"type":"error","file":"input.mp4","msg":"..."}
    //  {"type":"log","level":"INFO","msg":"..."}
    // ══════════════════════════════════════════════════════════════════════

    public partial class MainForm : Form
    {
        // ── Concurrency & cancellation ────────────────────────────────────
        private CancellationTokenSource? _cts;

        // SemaphoreSlim giới hạn số video xử lý SONG SONG.
        // Mặc định 2: đủ để GPU luôn bận nhưng không OOM VRAM.
        // User có thể chỉnh qua nudParallel (1-4).
        private SemaphoreSlim? _sem;

        // Thread-safe counters
        private int _successCount;
        private int _failCount;
        private int _startedCount;

        // Per-file status map: fileName → current step string
        private readonly ConcurrentDictionary<string, string> _fileStatus = new();

        // ── Controls ─────────────────────────────────────────────────────
        private RadioButton rdoSingle = new();
        private RadioButton rdoFolder = new();
        private TextBox txtInput = new();
        private Button btnBrowse = new();
        private Button btnOutput = new();
        private TextBox txtOutput = new();
        private ComboBox cboSrcLang = new();
        private ComboBox cboTgtLang = new();
        private NumericUpDown nudZoom = new();
        private NumericUpDown nudSpeed = new();
        private NumericUpDown nudVolumeDuck = new();
        private NumericUpDown nudParallel = new();   // ★ NEW: số luồng song song
        private CheckBox chkTranslate = new();
        private CheckBox chkDub = new();
        private CheckBox chkSub = new();
        private CheckBox chkZoom = new();
        private CheckBox chkSpeed = new();
        private RichTextBox rtbLog = new();
        private ProgressBar pgBarTotal = new();      // Overall batch progress
        private Label lblStatus = new();
        private Label lblFileProgress = new();       // "3 / 10 videos"
        private Button btnStart = new();
        private Button btnCancel = new();
        private Button btnSetup = new();
        private FlowLayoutPanel pnlMode = new();
        private Panel pnlActiveFiles = new();        // ★ Live per-file status strips

        public MainForm()
        {
            InitializeComponent();
            BuildUI();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI BUILDER
        // ══════════════════════════════════════════════════════════════════
        private void BuildUI()
        {
            Text = "🎬 Video Processor Pro — GPU Batch Edition";
            Size = new Size(980, 900);
            MinimumSize = new Size(860, 780);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(18, 18, 26);
            ForeColor = Color.FromArgb(220, 220, 232);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                ColumnCount = 1,
                Padding = new Padding(12),
                BackColor = Color.Transparent
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170)); // Input/Output
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250)); // Options
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110)); // Active-files panel
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));  // Buttons + overall bar
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Log
            Controls.Add(root);

            root.Controls.Add(BuildInputGroup(), 0, 0);
            root.Controls.Add(BuildOptionsGroup(), 0, 1);
            root.Controls.Add(BuildActiveFilesGroup(), 0, 2);
            root.Controls.Add(BuildButtonBar(), 0, 3);
            root.Controls.Add(BuildLogGroup(), 0, 4);
        }

        // ── Input / Output ────────────────────────────────────────────────
        private GroupBox BuildInputGroup()
        {
            var grp = MakeGroup("📁 Input / Output");
            grp.Dock = DockStyle.Fill;

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 3,
                Padding = new Padding(8)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            pnlMode = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            rdoSingle.Text = "1 Video"; rdoSingle.ForeColor = Color.White; rdoSingle.Checked = true; rdoSingle.AutoSize = true;
            rdoSingle.CheckedChanged += ModeChanged;
            rdoFolder.Text = "Thư mục (tối đa 200)"; rdoFolder.ForeColor = Color.White; rdoFolder.AutoSize = true;
            rdoFolder.Margin = new Padding(20, 0, 0, 0);
            pnlMode.Controls.AddRange(new Control[] { rdoSingle, rdoFolder });

            StyleTextBox(txtInput); txtInput.PlaceholderText = "Chọn file video MP4..."; txtInput.ReadOnly = true;
            StyleTextBox(txtOutput); txtOutput.PlaceholderText = "Thư mục xuất (mặc định cùng thư mục input)..."; txtOutput.ReadOnly = true;
            StyleButton(btnBrowse, "📂 Input", Color.FromArgb(55, 95, 175));
            StyleButton(btnOutput, "📁 Output", Color.FromArgb(55, 115, 75));
            btnBrowse.Click += BtnBrowse_Click;
            btnOutput.Click += BtnOutput_Click;

            tbl.Controls.Add(pnlMode, 0, 0); tbl.SetColumnSpan(pnlMode, 3);
            tbl.Controls.Add(txtInput, 0, 1);
            tbl.Controls.Add(btnBrowse, 1, 1);
            tbl.Controls.Add(new Label(), 2, 1);
            tbl.Controls.Add(txtOutput, 0, 2);
            tbl.Controls.Add(btnOutput, 1, 2);
            tbl.Controls.Add(new Label(), 2, 2);

            grp.Controls.Add(tbl);
            return grp;
        }

        // ── Options ───────────────────────────────────────────────────────
        private GroupBox BuildOptionsGroup()
        {
            var grp = MakeGroup("⚙️ Cấu hình xử lý");
            grp.Dock = DockStyle.Fill;

            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Padding = new Padding(6, 2, 6, 2) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            tbl.Controls.Add(BuildCheckPanel(), 0, 0);
            tbl.Controls.Add(BuildNumericPanel(), 1, 0);
            grp.Controls.Add(tbl);
            return grp;
        }

        private Panel BuildCheckPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill };

            string[] langs = { "auto", "vi", "en", "zh", "ja", "ko", "fr", "de", "es", "ru", "th", "id", "pt" };

            // Lang row
            var langFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(2, 0, 0, 0)
            };
            cboSrcLang.Items.AddRange(langs); cboSrcLang.SelectedItem = "auto";
            cboTgtLang.Items.AddRange(langs); cboTgtLang.SelectedItem = "vi";
            foreach (var c in new[] { cboSrcLang, cboTgtLang })
            {
                c.BackColor = Color.FromArgb(32, 32, 46); c.ForeColor = Color.White;
                c.FlatStyle = FlatStyle.Flat; c.Width = 72; c.DropDownStyle = ComboBoxStyle.DropDownList;
                c.Margin = new Padding(0, 4, 12, 0);
            }
            langFlow.Controls.AddRange(new Control[]
            {
                MakeLabel("Ngôn ngữ gốc:"), cboSrcLang,
                MakeLabel("Dịch sang:"), cboTgtLang
            });

            // Checkboxes
            chkSub = MakeCheck("📝 Thêm phụ đề (nền đen, chữ trắng)", true);
            chkTranslate = MakeCheck("🌐 Dịch phụ đề sang ngôn ngữ đích", true);
            chkDub = MakeCheck("🔊 Lồng tiếng (Piper-TTS, offline)", true);
            chkZoom = MakeCheck("🔍 Zoom 130%", true);
            chkSpeed = MakeCheck("⚡ Tua 1.3×", true);

            chkTranslate.CheckedChanged += (s, e) => cboTgtLang.Enabled = chkTranslate.Checked;
            chkDub.CheckedChanged += (s, e) => nudVolumeDuck.Enabled = chkDub.Checked;

            var checkFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Dock = DockStyle.Fill,
                Padding = new Padding(4, 4, 0, 0)
            };
            checkFlow.Controls.AddRange(new Control[] { chkSub, chkTranslate, chkDub, chkZoom, chkSpeed });

            langFlow.Dock = DockStyle.Bottom; langFlow.Height = 36;
            panel.Controls.Add(checkFlow);
            panel.Controls.Add(langFlow);
            checkFlow.Dock = DockStyle.Fill;
            return panel;
        }

        private Panel BuildNumericPanel()
        {
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                ColumnCount = 2,
                Padding = new Padding(8, 4, 4, 4)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            for (int i = 0; i < 5; i++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            nudVolumeDuck.Minimum = -60; nudVolumeDuck.Maximum = 0; nudVolumeDuck.Value = -20; StyleNumeric(nudVolumeDuck);
            nudZoom.Minimum = 100; nudZoom.Maximum = 300; nudZoom.Value = 130; nudZoom.Increment = 5; StyleNumeric(nudZoom);
            nudSpeed.Minimum = new decimal(0.5); nudSpeed.Maximum = 3; nudSpeed.Value = new decimal(1.3);
            nudSpeed.DecimalPlaces = 2; nudSpeed.Increment = new decimal(0.1); StyleNumeric(nudSpeed);

            // ★ Parallel workers
            nudParallel.Minimum = 1; nudParallel.Maximum = 4; nudParallel.Value = 2;
            nudParallel.Width = 60;
            StyleNumeric(nudParallel);

            tbl.Controls.Add(MakeLabel("Volume gốc (dB):"), 0, 0); tbl.Controls.Add(nudVolumeDuck, 1, 0);
            tbl.Controls.Add(MakeLabel("Zoom (%):"), 0, 1); tbl.Controls.Add(nudZoom, 1, 1);
            tbl.Controls.Add(MakeLabel("Tốc độ (×):"), 0, 2); tbl.Controls.Add(nudSpeed, 1, 2);
            tbl.Controls.Add(MakeLabel("⚡ Luồng song song:"), 0, 3); tbl.Controls.Add(nudParallel, 1, 3);
            tbl.Controls.Add(MakeLabel("(1=an toàn VRAM, 2-4=nhanh hơn)"), 0, 4); tbl.SetColumnSpan(tbl.Controls[^1], 2);

            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(tbl);
            return panel;
        }

        // ── Active Files Panel ────────────────────────────────────────────
        private GroupBox BuildActiveFilesGroup()
        {
            var grp = MakeGroup("🔄 Tiến trình xử lý song song");
            grp.Dock = DockStyle.Fill;

            pnlActiveFiles = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(14, 14, 22),
                Padding = new Padding(6)
            };
            grp.Controls.Add(pnlActiveFiles);
            return grp;
        }

        // ── Buttons + progress bar ────────────────────────────────────────
        private Panel BuildButtonBar()
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 2, 0, 2)
            };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ─ Progress row
            var progressRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));

            pgBarTotal.Dock = DockStyle.Fill;
            pgBarTotal.Style = ProgressBarStyle.Continuous;
            pgBarTotal.Minimum = 0; pgBarTotal.Maximum = 100; pgBarTotal.Value = 0;
            pgBarTotal.ForeColor = Color.FromArgb(60, 190, 110);
            pgBarTotal.BackColor = Color.FromArgb(35, 35, 50);

            lblFileProgress.AutoSize = false; lblFileProgress.Dock = DockStyle.Fill;
            lblFileProgress.TextAlign = ContentAlignment.MiddleRight;
            lblFileProgress.ForeColor = Color.FromArgb(140, 200, 255);
            lblFileProgress.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            lblFileProgress.Text = "0 / 0 videos";

            progressRow.Controls.Add(pgBarTotal, 0, 0);
            progressRow.Controls.Add(lblFileProgress, 1, 0);

            // ─ Button row
            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };

            StyleButton(btnSetup, "🔧 Cài Dependencies", Color.FromArgb(90, 55, 140));
            StyleButton(btnStart, "▶  Bắt đầu Batch", Color.FromArgb(38, 135, 75));
            StyleButton(btnCancel, "⏹  Dừng", Color.FromArgb(155, 55, 55));
            btnSetup.Width = 175; btnStart.Width = 160; btnCancel.Width = 110;
            btnCancel.Enabled = false;

            lblStatus.AutoSize = false; lblStatus.Width = 280;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.ForeColor = Color.FromArgb(150, 200, 150);
            lblStatus.Text = "Sẵn sàng";

            btnSetup.Click += BtnSetup_Click;
            btnStart.Click += BtnStart_Click;
            btnCancel.Click += BtnCancel_Click;

            btnRow.Controls.AddRange(new Control[] { btnSetup, btnStart, btnCancel, lblStatus });

            outer.Controls.Add(progressRow, 0, 0);
            outer.Controls.Add(btnRow, 0, 1);
            return outer;
        }

        // ── Log ───────────────────────────────────────────────────────────
        private GroupBox BuildLogGroup()
        {
            var grp = MakeGroup("📋 Log");
            grp.Dock = DockStyle.Fill;
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.BackColor = Color.FromArgb(10, 10, 16);
            rtbLog.ForeColor = Color.FromArgb(170, 220, 170);
            rtbLog.Font = new Font("Consolas", 8.5f);
            rtbLog.ReadOnly = true; rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            grp.Controls.Add(rtbLog);
            return grp;
        }

        // ══════════════════════════════════════════════════════════════════
        //  EVENTS
        // ══════════════════════════════════════════════════════════════════
        private void ModeChanged(object? sender, EventArgs e)
        {
            txtInput.Text = "";
            txtInput.PlaceholderText = rdoSingle.Checked ? "Chọn file video MP4..." : "Chọn thư mục chứa video...";
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            if (rdoSingle.Checked)
            {
                using var ofd = new OpenFileDialog { Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.webm|All|*.*" };
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtInput.Text = ofd.FileName;
                    if (string.IsNullOrEmpty(txtOutput.Text))
                        txtOutput.Text = Path.GetDirectoryName(ofd.FileName) ?? "";
                }
            }
            else
            {
                using var fbd = new FolderBrowserDialog { Description = "Chọn thư mục chứa video" };
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    txtInput.Text = fbd.SelectedPath;
                    if (string.IsNullOrEmpty(txtOutput.Text))
                        txtOutput.Text = Path.Combine(fbd.SelectedPath, "Processed");
                }
            }
        }

        private void BtnOutput_Click(object? sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog { Description = "Chọn thư mục xuất" };
            if (fbd.ShowDialog() == DialogResult.OK) txtOutput.Text = fbd.SelectedPath;
        }

        private async void BtnSetup_Click(object? sender, EventArgs e)
        {
            btnSetup.Enabled = false;
            Log("Đang cài đặt dependencies Python...", LogLevel.Info);
            try
            {
                await RunPythonScriptAsync("setup.py", "", CancellationToken.None);
                Log("✓ Cài đặt hoàn tất!", LogLevel.Success);
            }
            catch (Exception ex) { Log($"Lỗi cài đặt: {ex.Message}", LogLevel.Error); }
            finally { btnSetup.Enabled = true; }
        }

        private async void BtnStart_Click(object? sender, EventArgs e)
        {
            var inputPath = txtInput.Text.Trim();
            if (string.IsNullOrEmpty(inputPath))
            {
                MessageBox.Show("Vui lòng chọn File hoặc Thư mục input!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ── Collect files ─────────────────────────────────────────────
            var exts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" };
            List<string> files;

            if (rdoSingle.Checked)
            {
                if (!File.Exists(inputPath)) { MessageBox.Show("File không tồn tại!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                files = new List<string> { inputPath };
            }
            else
            {
                if (!Directory.Exists(inputPath)) { MessageBox.Show("Thư mục không tồn tại!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                files = Directory.GetFiles(inputPath, "*.*")
                                 .Where(f => exts.Contains(Path.GetExtension(f).ToLower()))
                                 .Take(200).ToList();
                if (!files.Any()) { MessageBox.Show("Không tìm thấy video nào!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            }

            int total = files.Count;
            int parallel = (int)nudParallel.Value;

            // ── Reset state ───────────────────────────────────────────────
            _successCount = 0; _failCount = 0; _startedCount = 0;
            _fileStatus.Clear();
            _cts = new CancellationTokenSource();
            _sem = new SemaphoreSlim(parallel, parallel);

            SetProgress(0, total);
            SetStatus($"Chuẩn bị {total} video | {parallel} luồng song song...");

            LockUI(true);
            rtbLog.Clear();
            RefreshActiveFilesPanel();

            Log($"🚀 Batch: {total} videos | {parallel} luồng song song", LogLevel.Info);
            Log($"   Output: {txtOutput.Text.Trim()}", LogLevel.Info);

            var sw = Stopwatch.StartNew();

            try
            {
                // ── Fire all tasks — semaphore controls concurrency ────────
                var tasks = files.Select(f => ProcessWithSemaphoreAsync(f, txtOutput.Text.Trim(), total, _cts.Token)).ToList();
                await Task.WhenAll(tasks);

                sw.Stop();
                bool cancelled = _cts.Token.IsCancellationRequested;
                string summary = cancelled
                    ? $"⏹ Đã dừng. ✓{_successCount} ✗{_failCount}"
                    : $"✅ Hoàn tất {_successCount}/{total} video | ✗{_failCount} lỗi | ⏱ {sw.Elapsed:mm\\:ss}";

                Log("\n" + new string('═', 55), LogLevel.Info);
                Log(summary, cancelled ? LogLevel.Warn : LogLevel.Success);
                SetStatus(cancelled ? "Đã dừng" : "Batch hoàn tất ✓");
                SetProgress(total, total);

                if (!cancelled && total > 1)
                {
                    var outDir = txtOutput.Text.Trim();
                    if (Directory.Exists(outDir) &&
                        MessageBox.Show($"{summary}\n\nMở thư mục output?", "Hoàn tất", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start("explorer.exe", outDir);
                }
            }
            catch (Exception ex)
            {
                Log($"Batch error: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                LockUI(false);
                _sem?.Dispose();
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            btnCancel.Enabled = false;
            SetStatus("Đang hủy, chờ video hiện tại hoàn thành...");
        }

        // ══════════════════════════════════════════════════════════════════
        //  PARALLEL CORE
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Wraps ProcessSingleVideoAsync with semaphore + global counter updates.</summary>
        private async Task ProcessWithSemaphoreAsync(string filePath, string outDir, int total, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            await _sem!.WaitAsync(ct); // Block until a slot is free
            int idx = Interlocked.Increment(ref _startedCount);
            string name = Path.GetFileName(filePath);

            UpdateFileStatus(name, "🔄 Đang xử lý...");
            Log($"[{idx}/{total}] ▶ Bắt đầu: {name}", LogLevel.Info);

            try
            {
                await ProcessSingleVideoAsync(filePath, outDir, name, idx, total, ct);
                Interlocked.Increment(ref _successCount);
                UpdateFileStatus(name, "✅ Xong");
                Log($"[{idx}/{total}] ✓ Hoàn tất: {name}", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                UpdateFileStatus(name, "⏹ Đã dừng");
                Log($"[{idx}/{total}] ⏹ Dừng: {name}", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failCount);
                UpdateFileStatus(name, $"❌ Lỗi: {ex.Message[..Math.Min(40, ex.Message.Length)]}");
                Log($"[{idx}/{total}] ❌ Lỗi {name}: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                int done = _successCount + _failCount;
                SetProgress(done, total);
                SetStatus($"✓{_successCount}  ✗{_failCount}  ⏳{total - done} còn lại");
                _sem.Release();
            }
        }

        /// <summary>Full pipeline for one video. Runs in whatever thread the semaphore grants.</summary>
        private async Task ProcessSingleVideoAsync(
            string inputFilePath, string targetOutDir,
            string displayName, int idx, int total,
            CancellationToken ct)
        {
            // Isolated temp directory per video — safe for parallel execution
            string workDir = Path.Combine(Path.GetTempPath(), $"VP_{Guid.NewGuid():N}"[..16]);
            Directory.CreateDirectory(workDir);

            try
            {
                // ── Safe path (avoid Vietnamese chars in FFmpeg/Python args)
                var safeInput = Path.Combine(workDir, "input.mp4");
                UpdateFileStatus(displayName, "📋 Copy video...");
                await Task.Run(() => File.Copy(inputFilePath, safeInput, true), ct);

                // ── Determine output path
                if (string.IsNullOrEmpty(targetOutDir))
                    targetOutDir = Path.GetDirectoryName(inputFilePath)!;
                Directory.CreateDirectory(targetOutDir);

                var baseName = Path.GetFileNameWithoutExtension(inputFilePath);
                var finalOutputPath = Path.Combine(targetOutDir, baseName + "_processed.mp4");
                var safeOutput = Path.Combine(workDir, "output.mp4");

                // ── Step 1: Video info
                UpdateFileStatus(displayName, "📊 Đọc info...");
                var videoInfo = await GetVideoInfoAsync(safeInput, ct);

                string srtPath = "";
                string dubbedAudioPath = "";
                string segmentsJson = Path.Combine(workDir, "segments.json");

                var tgtLang = cboTgtLang.SelectedItem?.ToString() ?? "vi";
                var srcLangRaw = cboSrcLang.SelectedItem?.ToString() ?? "auto";
                var srcLang = srcLangRaw == "auto" ? "" : srcLangRaw;

                if (chkSub.Checked || chkDub.Checked)
                {
                    // ── Step 2: Extract audio
                    UpdateFileStatus(displayName, "🎵 Trích audio...");
                    ct.ThrowIfCancellationRequested();
                    var audioPath = Path.Combine(workDir, "audio.wav");
                    await ExtractAudioAsync(safeInput, audioPath, ct);

                    // ── Step 3: Transcribe  (Python handles GPU — we just call it)
                    UpdateFileStatus(displayName, "🎤 Nhận dạng...");
                    ct.ThrowIfCancellationRequested();
                    var transcribeArgs = BuildPythonArgs(safeInput, audioPath, segmentsJson, srtPath, dubbedAudioPath, workDir, videoInfo, tgtLang, srcLang, "transcribe");
                    await RunPythonWithProgressAsync("video_processor.py", transcribeArgs, displayName, ct);

                    // ── Step 4: Translate
                    if (chkTranslate.Checked)
                    {
                        UpdateFileStatus(displayName, "🌐 Dịch...");
                        ct.ThrowIfCancellationRequested();
                        var translateArgs = BuildPythonArgs(safeInput, audioPath, segmentsJson, srtPath, dubbedAudioPath, workDir, videoInfo, tgtLang, srcLang, "translate");
                        await RunPythonWithProgressAsync("video_processor.py", translateArgs, displayName, ct);
                    }

                    // ── Step 5: Write SRT
                    if (chkSub.Checked)
                    {
                        srtPath = Path.Combine(workDir, "subtitles.srt");
                        ct.ThrowIfCancellationRequested();
                        var srtArgs = BuildPythonArgs(safeInput, audioPath, segmentsJson, srtPath, dubbedAudioPath, workDir, videoInfo, tgtLang, srcLang, "srt");
                        await RunPythonWithProgressAsync("video_processor.py", srtArgs, displayName, ct);
                    }

                    // ── Step 6: TTS dub
                    if (chkDub.Checked)
                    {
                        dubbedAudioPath = Path.Combine(workDir, "dubbed.aac");
                        UpdateFileStatus(displayName, "🔊 TTS...");
                        ct.ThrowIfCancellationRequested();
                        var ttsArgs = BuildPythonArgs(safeInput, audioPath, segmentsJson, srtPath, dubbedAudioPath, workDir, videoInfo, tgtLang, srcLang, "tts");
                        await RunPythonWithProgressAsync("video_processor.py", ttsArgs, displayName, ct);
                    }
                }

                // ── Step 7: FFmpeg render
                UpdateFileStatus(displayName, "🎬 Render...");
                ct.ThrowIfCancellationRequested();
                await RenderFinalVideoAsync(safeInput, srtPath, dubbedAudioPath, safeOutput, videoInfo, ct);

                if (!File.Exists(safeOutput))
                    throw new Exception("FFmpeg không tạo được output. Xem log.");

                await Task.Run(() => File.Copy(safeOutput, finalOutputPath, true), ct);

                if (rdoSingle.Checked)
                {
                    Invoke(() =>
                    {
                        if (MessageBox.Show($"Xong!\n{finalOutputPath}\n\nMở thư mục?", "Hoàn tất", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            Process.Start("explorer.exe", $"/select,\"{finalOutputPath}\"");
                    });
                }
            }
            finally
            {
                try { Directory.Delete(workDir, true); } catch { }
            }
        }

        /// <summary>Builds the Python CLI args for a given step.</summary>
        private string BuildPythonArgs(
            string safeInput, string audioPath, string segmentsJson,
            string srtPath, string dubbedAudioPath,
            string workDir, VideoInfo info,
            string tgtLang, string srcLang, string step)
        {
            return step switch
            {
                "transcribe" =>
                    $"--input \"{safeInput}\" --audio \"{audioPath}\" --transcribe " +
                    $"--segments-json \"{segmentsJson}\"" +
                    (srcLang.Length > 0 ? $" --src-lang {srcLang}" : ""),

                "translate" =>
                    $"--input \"{safeInput}\" --translate --tgt-lang {tgtLang} " +
                    $"--segments-json \"{segmentsJson}\"",

                "srt" =>
                    $"--input \"{safeInput}\" --srt-output \"{srtPath}\" " +
                    $"--tgt-lang {tgtLang} --segments-json \"{segmentsJson}\"",

                "tts" =>
                    $"--input \"{safeInput}\" --tts " +
                    $"--tts-dir \"{Path.Combine(workDir, "tts")}\" " +
                    $"--dubbed-audio \"{dubbedAudioPath}\" " +
                    $"--video-duration {info.Duration:F3} " +
                    $"--tgt-lang {tgtLang} --segments-json \"{segmentsJson}\"",

                _ => throw new ArgumentException($"Unknown step: {step}")
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  FFMPEG HELPERS
        // ══════════════════════════════════════════════════════════════════
        private async Task<VideoInfo> GetVideoInfoAsync(string videoPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                Arguments = $"-v quiet -print_format json -show_streams -show_format \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            var sb = new System.Text.StringBuilder();
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            while (!proc.StandardOutput.EndOfStream)
                sb.Append(await proc.StandardOutput.ReadToEndAsync());
            await proc.WaitForExitAsync(ct);

            var json = sb.ToString();
            return string.IsNullOrEmpty(json)
                ? new VideoInfo { Duration = 60, Width = 1920, Height = 1080, Fps = 30 }
                : ParseVideoInfo(json);
        }

        private static VideoInfo ParseVideoInfo(string json)
        {
            var info = new VideoInfo();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("format", out var fmt))
                    info.Duration = double.Parse(fmt.GetProperty("duration").GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                if (root.TryGetProperty("streams", out var streams))
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (s.TryGetProperty("codec_type", out var ct2) && ct2.GetString() == "video")
                        {
                            info.Width = s.GetProperty("width").GetInt32();
                            info.Height = s.GetProperty("height").GetInt32();
                            if (s.TryGetProperty("r_frame_rate", out var fps))
                            {
                                var p = fps.GetString()!.Split('/');
                                if (p.Length == 2) info.Fps = double.Parse(p[0]) / double.Parse(p[1]);
                            }
                            break;
                        }
                    }
            }
            catch { }
            return info;
        }

        private async Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken ct)
            => await RunProcessAsync("ffmpeg", $"-y -i \"{videoPath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{audioPath}\"", null, ct);

        private async Task RenderFinalVideoAsync(
            string inputPath, string srtPath, string dubbedAudioPath,
            string outputPath, VideoInfo info, CancellationToken ct)
        {
            double speed = (double)nudSpeed.Value;
            double zoom = (double)nudZoom.Value / 100.0;
            double volDb = (double)nudVolumeDuck.Value;

            var vfParts = new List<string>();

            // Crop + scale for zoom
            if (chkZoom.Checked)
            {
                int cw = MakeEven((int)Math.Round(info.Width / zoom));
                int ch = MakeEven((int)Math.Round(info.Height / zoom));
                int cx = MakeEven((info.Width - cw) / 2);
                int cy = MakeEven((info.Height - ch) / 2);
                vfParts.Add($"crop={cw}:{ch}:{cx}:{cy},scale={info.Width}:{info.Height}");
            }

            // ASS subtitle burn-in
            if (chkSub.Checked && File.Exists(srtPath))
            {
                var assPath = Path.Combine(Path.GetTempPath(), $"vp_{Guid.NewGuid():N}"[..8] + ".ass");
                ConvertSrtToAss(srtPath, assPath);
                var escaped = assPath.Replace("\\", "/").Replace(":", "\\:");
                vfParts.Add($"ass=filename='{escaped}'");
            }

            // Speed
            if (chkSpeed.Checked && speed != 1.0)
                vfParts.Add($"setpts={(1.0 / speed).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}*PTS");

            string vf = vfParts.Count > 0 ? string.Join(",", vfParts) : "";

            double linearVol = Math.Pow(10, volDb / 20.0);
            string volFilter = $"volume={linearVol:F4}";
            string atempoFilter = (chkSpeed.Checked && speed != 1.0)
                ? $"atempo={speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
                : "";

            bool hasDub = chkDub.Checked && File.Exists(dubbedAudioPath);
            bool hasVF = vfParts.Count > 0;

            var cmd = new System.Text.StringBuilder();
            cmd.Append($"-y -i \"{inputPath}\" ");
            if (hasDub) cmd.Append($"-i \"{dubbedAudioPath}\" ");

            if (hasDub)
            {
                string vc = hasVF ? $"[0:v]{vf}[vout];" : "";
                string mapV = hasVF ? "\"[vout]\"" : "0:v";
                string atempo = atempoFilter.Length > 0 ? $",{atempoFilter}" : "";
                cmd.Append($"-filter_complex \"{vc}[0:a]{volFilter}[orig];[orig][1:a]amix=inputs=2:duration=first:dropout_transition=0:normalize=0{atempo}[aout]\" ");
                cmd.Append($"-map {mapV} -map \"[aout]\" ");
            }
            else if (hasVF)
            {
                string af = volFilter + (atempoFilter.Length > 0 ? $",{atempoFilter}" : "");
                cmd.Append($"-filter_complex \"[0:v]{vf}[vout]\" -map \"[vout]\" -map 0:a -af \"{af}\" ");
            }
            else
            {
                string af = volFilter + (atempoFilter.Length > 0 ? $",{atempoFilter}" : "");
                cmd.Append($"-map 0:v -map 0:a -af \"{af}\" ");
            }

            // Try NVENC, fall back to CPU libx264 on error
            cmd.Append($"-c:v h264_nvenc -preset p4 -cq 18 -b:v 5M -c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"");

            try
            {
                await RunProcessAsync("ffmpeg", cmd.ToString(), null, ct);
            }
            catch
            {
                // Fallback to CPU if NVENC unavailable
                Log("⚠️ NVENC không khả dụng, chuyển sang CPU libx264...", LogLevel.Warn);
                var cpuCmd = cmd.ToString()
                    .Replace("-c:v h264_nvenc -preset p4 -cq 18 -b:v 5M", "-c:v libx264 -preset fast -crf 20");
                await RunProcessAsync("ffmpeg", cpuCmd, null, ct);
            }
        }

        private static int MakeEven(int v) => v % 2 != 0 ? v - 1 : v;

        private void ConvertSrtToAss(string srtPath, string assPath)
        {
            const string header = @"[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,46,&H00FFFFFF,&H000000FF,&H00000000,&HB0000000,0,0,0,0,100,100,0,0,3,1,0,2,10,10,38,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
";
            var sb = new System.Text.StringBuilder(header);
            var lines = File.ReadAllLines(srtPath, System.Text.Encoding.UTF8);
            int i = 0;
            while (i < lines.Length)
            {
                if (!int.TryParse(lines[i].Trim(), out _)) { i++; continue; }
                i++;
                if (i >= lines.Length) break;
                var tl = lines[i].Trim(); i++;
                if (!tl.Contains("-->")) continue;
                var parts = tl.Split(new[] { " --> " }, StringSplitOptions.None);
                if (parts.Length < 2) continue;
                var textLines = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0) { textLines.Add(lines[i].Trim()); i++; }
                while (i < lines.Length && lines[i].Trim().Length == 0) i++;
                sb.AppendLine($"Dialogue: 0,{SrtToAss(parts[0])},{SrtToAss(parts[1])},Default,,0,0,0,,{string.Join("\\N", textLines)}");
            }
            File.WriteAllText(assPath, sb.ToString(), System.Text.Encoding.UTF8);
        }

        private static string SrtToAss(string t)
        {
            var p = t.Trim().Replace(",", ".").Split(':');
            if (p.Length < 3) return "0:00:00.00";
            var sm = p[2].Split('.');
            int ms = sm.Length > 1 ? int.Parse(sm[1][..Math.Min(2, sm[1].Length)]) : 0;
            return $"{int.Parse(p[0])}:{int.Parse(p[1]):D2}:{int.Parse(sm[0]):D2}.{ms:D2}";
        }

        // ══════════════════════════════════════════════════════════════════
        //  PROCESS RUNNERS
        // ══════════════════════════════════════════════════════════════════
        private string GetPythonScript(string name)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var p in new[] { Path.Combine(dir, "Python", name), Path.Combine(dir, "..", "Python", name), Path.Combine(dir, name) })
                if (File.Exists(p)) return Path.GetFullPath(p);
            return Path.Combine(dir, "Python", name);
        }

        private async Task RunPythonScriptAsync(string script, string args, CancellationToken ct)
            => await RunProcessAsync(FindPython(), $"\"{GetPythonScript(script)}\" {args}", null, ct);

        /// <summary>
        /// Runs Python and parses JSON progress lines.
        /// Python prints:  {"type":"progress","step":"Transcribe","pct":40}
        /// Other lines are logged as Debug.
        /// </summary>
        private async Task RunPythonWithProgressAsync(string script, string args, string displayName, CancellationToken ct)
        {
            await RunProcessAsync(FindPython(), $"\"{GetPythonScript(script)}\" {args}",
                line =>
                {
                    if (line.StartsWith("{"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("type", out var t) && t.GetString() == "progress")
                            {
                                var step = root.TryGetProperty("step", out var s) ? s.GetString() : "";
                                var pct = root.TryGetProperty("pct", out var p) ? p.GetInt32() : -1;
                                UpdateFileStatus(displayName, pct >= 0 ? $"{step} {pct}%" : step ?? "...");
                                return; // Don't log progress JSON as text
                            }
                        }
                        catch { }
                    }
                    // Normal text line
                    LogFromProcess(line);
                },
                ct);
        }

        private static string FindPython()
        {
            foreach (var c in new[] { "python", "python3", "py" })
                try
                {
                    var p = Process.Start(new ProcessStartInfo(c, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(2000);
                    if (p?.ExitCode == 0) return c;
                }
                catch { }
            return "python";
        }

        /// <param name="stdoutHandler">Called on each stdout line (null = log as Debug).</param>
        private async Task RunProcessAsync(string exe, string args, Action<string>? stdoutHandler, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (stdoutHandler != null) stdoutHandler(e.Data);
                else LogFromProcess(e.Data);
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    LogFromProcess(e.Data, e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase));
            };
            process.Exited += (s, e) => tcs.TrySetResult(true);

            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                tcs.TrySetCanceled();
            });

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await tcs.Task;

            if (!ct.IsCancellationRequested && process.ExitCode != 0)
                throw new Exception($"'{Path.GetFileName(exe)}' exited {process.ExitCode}");
        }

        // ══════════════════════════════════════════════════════════════════
        //  ACTIVE FILES PANEL — live per-file status strips
        // ══════════════════════════════════════════════════════════════════
        private readonly Dictionary<string, Label> _stripLabels = new();

        private void UpdateFileStatus(string fileName, string status)
        {
            _fileStatus[fileName] = status;

            if (InvokeRequired) { Invoke(() => UpdateFileStatus(fileName, status)); return; }

            if (!_stripLabels.TryGetValue(fileName, out var lbl))
            {
                lbl = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Height = 20,
                    Dock = DockStyle.Top,
                    Font = new Font("Consolas", 8f),
                    ForeColor = Color.FromArgb(180, 210, 255),
                    BackColor = Color.FromArgb(18, 22, 34),
                    Padding = new Padding(4, 2, 4, 0),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                _stripLabels[fileName] = lbl;
                pnlActiveFiles.Controls.Add(lbl);
                pnlActiveFiles.Controls.SetChildIndex(lbl, 0);
            }

            string shortName = fileName.Length > 38 ? "..." + fileName[^35..] : fileName;
            lbl.Text = $"  {shortName.PadRight(40)} {status}";

            // Color by status
            lbl.ForeColor = status.StartsWith("✅") ? Color.FromArgb(100, 220, 130)
                          : status.StartsWith("❌") ? Color.FromArgb(255, 100, 100)
                          : status.StartsWith("⏹") ? Color.FromArgb(180, 180, 100)
                          : Color.FromArgb(160, 200, 255);
        }

        private void RefreshActiveFilesPanel()
        {
            if (InvokeRequired) { Invoke(RefreshActiveFilesPanel); return; }
            pnlActiveFiles.Controls.Clear();
            _stripLabels.Clear();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI HELPERS
        // ══════════════════════════════════════════════════════════════════
        private void LockUI(bool processing)
        {
            if (InvokeRequired) { Invoke(() => LockUI(processing)); return; }
            btnStart.Enabled = !processing;
            btnCancel.Enabled = processing;
            btnSetup.Enabled = !processing;
            pnlMode.Enabled = !processing;
            btnBrowse.Enabled = !processing;
            btnOutput.Enabled = !processing;
        }

        private void SetProgress(int done, int total)
        {
            if (InvokeRequired) { Invoke(() => SetProgress(done, total)); return; }
            pgBarTotal.Maximum = total > 0 ? total : 1;
            pgBarTotal.Value = Math.Min(done, pgBarTotal.Maximum);
            lblFileProgress.Text = $"{done} / {total} videos";
        }

        private void SetStatus(string msg)
        {
            if (InvokeRequired) { Invoke(() => SetStatus(msg)); return; }
            lblStatus.Text = msg;
        }

        private GroupBox MakeGroup(string title)
        {
            return new GroupBox
            {
                Text = title,
                ForeColor = Color.FromArgb(130, 175, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(24, 24, 36)
            };
        }

        private static void StyleTextBox(TextBox tb)
        {
            tb.BackColor = Color.FromArgb(32, 32, 46); tb.ForeColor = Color.FromArgb(210, 210, 230);
            tb.BorderStyle = BorderStyle.FixedSingle; tb.Dock = DockStyle.Fill; tb.Margin = new Padding(2);
        }

        private static void StyleButton(Button btn, string text, Color bg)
        {
            btn.Text = text; btn.BackColor = bg; btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderSize = 0;
            btn.Height = 32; btn.Margin = new Padding(4, 2, 4, 2);
            btn.Cursor = Cursors.Hand; btn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        }

        private static CheckBox MakeCheck(string text, bool chk)
            => new() { Text = text, Checked = chk, ForeColor = Color.FromArgb(200, 212, 232), AutoSize = true, Margin = new Padding(2, 4, 2, 4) };

        private static Label MakeLabel(string text)
            => new() { Text = text, ForeColor = Color.FromArgb(160, 170, 200), TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };

        private static void StyleNumeric(NumericUpDown n)
        {
            n.BackColor = Color.FromArgb(32, 32, 46); n.ForeColor = Color.White; n.Dock = DockStyle.Fill;
        }

        // ══════════════════════════════════════════════════════════════════
        //  LOGGING
        // ══════════════════════════════════════════════════════════════════
        enum LogLevel { Debug, Info, Warn, Error, Success }

        private void Log(string msg, LogLevel level = LogLevel.Info)
        {
            if (InvokeRequired) { Invoke(() => Log(msg, level)); return; }
            var color = level switch
            {
                LogLevel.Debug => Color.FromArgb(100, 115, 140),
                LogLevel.Warn => Color.FromArgb(255, 195, 90),
                LogLevel.Error => Color.FromArgb(255, 95, 95),
                LogLevel.Success => Color.FromArgb(95, 220, 125),
                _ => Color.FromArgb(175, 205, 255)
            };
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionColor = Color.FromArgb(90, 110, 130);
            rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
            rtbLog.SelectionColor = color;
            rtbLog.AppendText(msg + "\n");
            rtbLog.ScrollToCaret();
        }

        private void LogFromProcess(string msg, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            Log(msg.Trim(), isError ? LogLevel.Error : LogLevel.Debug);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DATA MODELS
    // ══════════════════════════════════════════════════════════════════════
    public class VideoInfo
    {
        public double Duration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
    }
}