using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LlmOcr;

public sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly MineruServer _server;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private CancellationTokenSource? _cts;
    private bool _processing;

    // controls
    private readonly Panel _lamp = new();
    private readonly Label _lampText = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly ListBox _lstIn = new();
    private readonly Button _btnPick = new();
    private readonly Button _btnRemove = new();
    private readonly Button _btnClear = new();
    private readonly TextBox _txtOut = new();
    private readonly RadioButton _rbRag = new();
    private readonly RadioButton _rbHtml = new();
    private readonly CheckBox _chkSaveImages = new();
    private readonly ComboBox _cmbBackend = new();
    private readonly ComboBox _cmbLang = new();
    private readonly ComboBox _cmbEffort = new();
    private readonly ComboBox _cmbImgAnalysis = new();
    private readonly ComboBox _cmbMirror = new();
    private readonly Button _btnProcess = new();
    private readonly Button _btnCancel = new();
    private readonly ProgressBar _progress = new();
    private readonly TrackBar _gpuLoad = new();
    private readonly Label _gpuLoadLabel = new();
    private readonly SplitContainer _split = new();
    private readonly TextBox _logFiles = new();
    private readonly TextBox _logSys = new();

    private readonly GpuMonitor _gpuMon;
    private volatile int _targetTempValue = 75; // live target max GPU temp (°C)
    private volatile int _lastTemp = -1;        // last GPU temp read by the health timer, -1 = unknown
    private GpuThrottle? _throttle;

    // health-monitor state
    private bool? _lastHealthy;
    private bool _autoRestarting;
    private bool _outageActive;
    private bool _userStoppedServer; // user pressed Stop -> don't auto-restart

    private static readonly string[] Supported =
        { ".pdf", ".djvu", ".djv", ".docx", ".pptx", ".xlsx" };

    public MainForm()
    {
        _cfg = AppConfig.Load();
        _server = new MineruServer(_cfg, m => Log(m, LogChannel.System));
        _gpuMon = new GpuMonitor(_cfg.NvidiaSmiPath);
        _targetTempValue = Math.Clamp(_cfg.TargetTempC, 50, 90);

        Text = "LLM OCR — MinerU batch (RAG / HTML)";
        Width = 1000;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildUi();

        _healthTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _healthTimer.Tick += async (_, _) => await RefreshLampAsync();
        _healthTimer.Start();

        FormClosing += OnClosing;
        _ = RefreshLampAsync();
    }

    private void BuildUi()
    {
        int x = 16, y = 14, w = Width - 40;

        // --- server row ---
        _lamp.SetBounds(x, y + 2, 16, 16);
        _lamp.BackColor = Color.Firebrick;
        _lamp.BorderStyle = BorderStyle.FixedSingle;
        _lampText.SetBounds(x + 24, y, 220, 22);
        _lampText.Text = "MinerU сервер";

        _btnStart.SetBounds(x + 250, y - 2, 130, 28);
        _btnStart.Text = "Start MinerU";
        _btnStart.Click += async (_, _) => await StartServerAsync();

        _btnStop.SetBounds(x + 388, y - 2, 130, 28);
        _btnStop.Text = "Stop MinerU";
        _btnStop.Click += (_, _) =>
        {
            _userStoppedServer = true; // intentional stop — suppress auto-restart
            _server.Stop();
            _ = RefreshLampAsync();
        };

        Controls.Add(_lamp);
        Controls.Add(_lampText);
        Controls.Add(_btnStart);
        Controls.Add(_btnStop);

        // --- input files ---
        y += 44;
        AddLabel("Вход (PDF/DJVU/DOCX/PPTX/XLSX):", x, y);
        y += 20;
        _lstIn.SetBounds(x, y, w, 96);
        _lstIn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lstIn.HorizontalScrollbar = true;
        _lstIn.SelectionMode = SelectionMode.MultiExtended;
        Controls.Add(_lstIn);

        y += 100;
        _btnPick.SetBounds(x, y, 160, 28);
        _btnPick.Text = "Выбрать файлы…";
        _btnPick.Click += (_, _) => PickFiles();
        Controls.Add(_btnPick);

        _btnRemove.SetBounds(x + 170, y, 170, 28);
        _btnRemove.Text = "Удалить выделенный";
        _btnRemove.Click += (_, _) => RemoveSelected();
        Controls.Add(_btnRemove);

        _btnClear.SetBounds(x + 350, y, 120, 28);
        _btnClear.Text = "Очистить всё";
        _btnClear.Click += (_, _) => _lstIn.Items.Clear();
        Controls.Add(_btnClear);

        var lblHint = new Label
        {
            Left = x + 480, Top = y + 6, AutoSize = true, ForeColor = Color.Gray,
            Text = "мультивыбор; Del — удалить"
        };
        Controls.Add(lblHint);

        _lstIn.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        // --- output ---
        y += 38;
        AddLabel("Выход (папка для md / html):", x, y);
        y += 20;
        _txtOut.SetBounds(x, y, w - 110, 24);
        _txtOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var bOut = new Button { Text = "Обзор…", Left = x + w - 100, Top = y - 1, Width = 90, Height = 26,
                                Anchor = AnchorStyles.Top | AnchorStyles.Right };
        bOut.Click += (_, _) => BrowseFolder(_txtOut);
        Controls.Add(_txtOut);
        Controls.Add(bOut);

        // --- mode ---
        y += 36;
        var grp = new GroupBox { Text = "Режим вывода", Left = x, Top = y, Width = 420, Height = 80 };
        _rbRag.SetBounds(14, 22, 190, 22);
        _rbRag.Text = "RAG Markdown (AnythingLLM)";
        _rbHtml.SetBounds(210, 22, 190, 22);
        _rbHtml.Text = "HTML с картинками";

        _chkSaveImages.SetBounds(14, 48, 400, 22);
        _chkSaveImages.Text = "Сохранять картинки (папка на файл, ссылки не вырезаются)";
        // restore remembered mode + checkbox state
        bool ragMode = !string.Equals(_cfg.OutputModeName, "html", StringComparison.OrdinalIgnoreCase);
        _rbRag.Checked = ragMode;
        _rbHtml.Checked = !ragMode;
        _chkSaveImages.Checked = _cfg.RagSaveImages;
        _chkSaveImages.Enabled = ragMode; // active only in RAG mode

        _rbRag.CheckedChanged += (_, _) => { _chkSaveImages.Enabled = _rbRag.Checked; PersistUiState(); };
        _rbHtml.CheckedChanged += (_, _) => PersistUiState();
        _chkSaveImages.CheckedChanged += (_, _) => PersistUiState();

        grp.Controls.Add(_rbRag);
        grp.Controls.Add(_rbHtml);
        grp.Controls.Add(_chkSaveImages);
        Controls.Add(grp);

        // --- backend + lang ---
        AddLabel("Backend:", x + 440, y + 6);
        _cmbBackend.SetBounds(x + 512, y + 2, 150, 24);
        _cmbBackend.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbBackend.Items.AddRange(new object[] { "pipeline", "hybrid-engine", "vlm-engine" });
        _cmbBackend.SelectedItem = _cmbBackend.Items.Contains(_cfg.Backend) ? _cfg.Backend : "hybrid-engine";
        _cmbBackend.SelectedIndexChanged += (_, _) => PersistUiState();
        Controls.Add(_cmbBackend);

        AddLabel("Язык (OCR):", x + 440, y + 34);
        _cmbLang.SetBounds(x + 512, y + 30, 150, 24);
        _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbLang.Items.AddRange(new object[]
            { "(по умолчанию)", "ch", "cyrillic", "east_slavic", "korean", "arabic", "devanagari", "el", "th", "ta", "te", "ka" });
        _cmbLang.SelectedItem = string.IsNullOrEmpty(_cfg.Lang) ? "(по умолчанию)"
                                 : (_cmbLang.Items.Contains(_cfg.Lang) ? _cfg.Lang : "(по умолчанию)");
        _cmbLang.SelectedIndexChanged += (_, _) => PersistUiState();
        Controls.Add(_cmbLang);

        // second right column: effort (hybrid only) + image analysis (hybrid/vlm)
        AddLabel("Effort:", x + 684, y + 6);
        _cmbEffort.SetBounds(x + 748, y + 2, 120, 24);
        _cmbEffort.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbEffort.Items.AddRange(new object[] { "medium", "high" });
        _cmbEffort.SelectedItem = _cmbEffort.Items.Contains(_cfg.Effort) ? _cfg.Effort : "medium";
        _cmbEffort.SelectedIndexChanged += (_, _) => PersistUiState();
        Controls.Add(_cmbEffort);

        AddLabel("Картинки:", x + 684, y + 34);
        _cmbImgAnalysis.SetBounds(x + 748, y + 30, 120, 24);
        _cmbImgAnalysis.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbImgAnalysis.Items.AddRange(new object[] { "(авто)", "вкл", "выкл" });
        _cmbImgAnalysis.SelectedItem = _cfg.ImageAnalysis switch
        {
            "on" => "вкл",
            "off" => "выкл",
            _ => "(авто)"
        };
        _cmbImgAnalysis.SelectedIndexChanged += (_, _) => PersistUiState();
        Controls.Add(_cmbImgAnalysis);

        // mirror (model source) — choices come from config; ModelSource = default pick
        AddLabel("Зеркало:", x + 440, y + 62);
        _cmbMirror.SetBounds(x + 512, y + 58, 150, 24);
        _cmbMirror.DropDownStyle = ComboBoxStyle.DropDownList;
        var mirrors = (_cfg.ModelSources is { Length: > 0 })
            ? _cfg.ModelSources
            : new[] { "huggingface", "modelscope", "local" };
        _cmbMirror.Items.AddRange(mirrors);
        _cmbMirror.SelectedItem = _cmbMirror.Items.Contains(_cfg.ModelSource) ? _cfg.ModelSource : mirrors[0];
        _cmbMirror.SelectedIndexChanged += (_, _) => PersistUiState();
        Controls.Add(_cmbMirror);

        _cmbBackend.SelectedIndexChanged += (_, _) => UpdateBackendDependentControls();
        UpdateBackendDependentControls();

        // --- process / cancel ---
        y += 90;
        _btnProcess.SetBounds(x, y, 200, 32);
        _btnProcess.Text = "Обработать пачку";
        _btnProcess.Click += async (_, _) => await ProcessAsync();
        Controls.Add(_btnProcess);

        _btnCancel.SetBounds(x + 210, y, 120, 32);
        _btnCancel.Text = "Отмена";
        _btnCancel.Enabled = false;
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        // --- GPU target-temperature slider (closed-loop: suspend the mineru tree while
        //     the GPU is hotter than target, resume once it cools; no admin) ---
        _gpuLoadLabel.SetBounds(x + 345, y + 8, 250, 20);
        Controls.Add(_gpuLoadLabel);
        _gpuLoad.SetBounds(x + 600, y - 2, w - 600, 40);
        _gpuLoad.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gpuLoad.Minimum = 50; _gpuLoad.Maximum = 90;
        _gpuLoad.TickFrequency = 5; _gpuLoad.LargeChange = 5; _gpuLoad.SmallChange = 1;
        _gpuLoad.Value = Math.Clamp(_cfg.TargetTempC, 50, 90);
        _targetTempValue = _gpuLoad.Value;
        _gpuLoad.Scroll += (_, _) =>
        {
            _targetTempValue = _gpuLoad.Value;   // live: affects the running batch
            UpdateGpuLabel();
            _cfg.TargetTempC = _gpuLoad.Value;
            try { _cfg.Save(); } catch { }
        };
        Controls.Add(_gpuLoad);
        UpdateGpuLabel();

        // --- progress ---
        y += 40;
        _progress.SetBounds(x, y, w, 20);
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progress.Minimum = 0; _progress.Maximum = 1000;
        Controls.Add(_progress);

        // --- log (split: files | system) ---
        y += 30;
        _split.SetBounds(x, y, w, Height - y - 56);
        _split.Orientation = Orientation.Vertical; // vertical splitter -> panes side by side
        _split.SplitterWidth = 6;
        _split.Panel1MinSize = 180;
        _split.Panel2MinSize = 180;
        _split.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        BuildLogPane(_split.Panel1, _logFiles, "Прогресс по файлам");
        BuildLogPane(_split.Panel2, _logSys, "Системные сообщения");
        Controls.Add(_split);
        // SplitterDistance must be set after the control has its width.
        try { _split.SplitterDistance = Math.Max(_split.Panel1MinSize, w / 2); } catch { /* ignore */ }
    }

    private static void BuildLogPane(SplitterPanel panel, TextBox box, string header)
    {
        var hdr = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = header,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Gainsboro,
            Padding = new Padding(4, 0, 0, 0),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        box.Multiline = true;
        box.ReadOnly = true;
        box.WordWrap = false;
        box.ScrollBars = ScrollBars.Both;
        box.BackColor = Color.White;
        box.Font = new Font("Consolas", 8.5f);
        box.Dock = DockStyle.Fill;

        panel.Controls.Add(box);   // fill (added first)
        panel.Controls.Add(hdr);   // top  (added last -> docks above fill)
    }

    private void AddLabel(string text, int x, int y)
    {
        var l = new Label { Text = text, Left = x, Top = y, AutoSize = true };
        Controls.Add(l);
    }

    private void UpdateGpuLabel()
    {
        string cur = _lastTemp >= 0 ? $"сейчас {_lastTemp}°C" : "сейчас —";
        string tgt = _targetTempValue >= 90 ? "без ограничений" : $"≤ {_targetTempValue}°C";
        _gpuLoadLabel.Text = $"Держать GPU {tgt}   ({cur})";
    }

    /// <summary>Writes the current UI selections into config.json so they are
    /// restored on next launch (engine, language, effort, image-analysis, output mode, save-images).</summary>
    private void PersistUiState()
    {
        try
        {
            _cfg.Backend = _cmbBackend.SelectedItem?.ToString() ?? _cfg.Backend;
            string lang = _cmbLang.SelectedItem?.ToString() ?? "(по умолчанию)";
            _cfg.Lang = lang.StartsWith("(") ? "" : lang;
            _cfg.Effort = _cmbEffort.SelectedItem?.ToString() ?? "medium";
            _cfg.ImageAnalysis = (_cmbImgAnalysis.SelectedItem?.ToString()) switch
            {
                "вкл" => "on",
                "выкл" => "off",
                _ => "auto"
            };
            _cfg.ModelSource = _cmbMirror.SelectedItem?.ToString() ?? _cfg.ModelSource;
            _cfg.OutputModeName = _rbHtml.Checked ? "html" : "rag";
            _cfg.RagSaveImages = _chkSaveImages.Checked;
            _cfg.Save();
        }
        catch { /* config persistence is best-effort */ }
    }

    /// <summary>Enable/disable effort &amp; image-analysis by the selected backend:
    /// effort → hybrid only; image-analysis → hybrid &amp; vlm. Also persists state.</summary>
    private void UpdateBackendDependentControls()
    {
        string b = _cmbBackend.SelectedItem?.ToString() ?? "";
        bool isHybrid = b.StartsWith("hybrid", StringComparison.OrdinalIgnoreCase);
        bool isVlm = b.StartsWith("vlm", StringComparison.OrdinalIgnoreCase);
        _cmbEffort.Enabled = isHybrid;
        _cmbImgAnalysis.Enabled = isHybrid || isVlm;
        PersistUiState();
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файлы (PDF/DJVU/DOCX/PPTX/XLSX)",
            Multiselect = true,
            Filter = "Документы (*.pdf;*.djvu;*.djv;*.docx;*.pptx;*.xlsx)|*.pdf;*.djvu;*.djv;*.docx;*.pptx;*.xlsx|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        foreach (var f in dlg.FileNames)
        {
            if (!Supported.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                continue;
            if (!_lstIn.Items.Contains(f))
                _lstIn.Items.Add(f);
        }
    }

    private void RemoveSelected()
    {
        foreach (int i in _lstIn.SelectedIndices.Cast<int>().OrderByDescending(i => i))
            _lstIn.Items.RemoveAt(i);
    }

    private void BrowseFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(target.Text)) dlg.SelectedPath = target.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private async Task StartServerAsync()
    {
        _btnStart.Enabled = false;
        _userStoppedServer = false; // user wants it up again
        try { await _server.EnsureStartedAsync(); }
        catch (Exception ex) { Log("Ошибка запуска: " + ex.Message, LogChannel.System); }
        finally { _btnStart.Enabled = true; await RefreshLampAsync(); }
    }

    private async Task RefreshLampAsync()
    {
        bool healthy = await _server.IsHealthyAsync();
        int? t = await _gpuMon.ReadTempAsync();
        if (t.HasValue) _lastTemp = t.Value;
        if (IsDisposed) return;

        void Apply()
        {
            _lamp.BackColor = healthy ? Color.ForestGreen : Color.Firebrick;
            UpdateGpuLabel();
        }
        if (InvokeRequired) BeginInvoke(Apply); else Apply();

        if (healthy)
        {
            if (_outageActive)
            {
                Log("✓ сервер MinerU снова доступен", LogChannel.System);
                _outageActive = false;
            }
            _lastHealthy = true;
            _autoRestarting = false;
        }
        else
        {
            // React only to a genuine outage: server was up, now down, we're neither
            // already restarting nor mid-batch, and the user didn't stop it on purpose.
            if (_lastHealthy == true && !_autoRestarting && !_processing && !_userStoppedServer)
            {
                _outageActive = true;
                _autoRestarting = true;
                Log("✗ сервер MinerU недоступен — пытаюсь поднять заново", LogChannel.System);
                _ = Task.Run(async () =>
                {
                    try { await _server.EnsureStartedAsync(); }
                    catch { /* details already logged inside EnsureStartedAsync */ }
                    finally { _autoRestarting = false; }
                });
            }
            _lastHealthy = false;
        }
    }

    private async Task ProcessAsync()
    {
        if (_processing) return;

        var files = _lstIn.Items.Cast<string>().ToList();
        string outDir = _txtOut.Text.Trim();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "Добавьте хотя бы один PDF/DJVU файл.", "LLM OCR",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show(this, "Укажите выходную папку.", "LLM OCR",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // apply UI choices to config for this run
        _cfg.Backend = _cmbBackend.SelectedItem?.ToString() ?? "pipeline";
        string lang = _cmbLang.SelectedItem?.ToString() ?? "(по умолчанию)";
        _cfg.Lang = lang.StartsWith("(") ? "" : lang;

        var mode = _rbHtml.Checked ? OutputMode.Html : OutputMode.Rag;
        bool ragSaveImages = _rbRag.Checked && _chkSaveImages.Checked;
        PersistUiState();

        SetProcessing(true);
        _progress.Value = 0;
        _cts = new CancellationTokenSource();
        Log($"=== Старт: {files.Count} файлов, режим {(mode == OutputMode.Rag ? "RAG-md" : "HTML")}"
            + (mode == OutputMode.Rag && ragSaveImages ? " (+картинки, папка на файл)" : "")
            + $", backend {_cfg.Backend}"
            + (_cmbEffort.Enabled ? $", effort {_cfg.Effort}" : "")
            + (_cmbImgAnalysis.Enabled && _cfg.ImageAnalysis != "auto" ? $", картинки {_cfg.ImageAnalysis}" : "")
            + (string.IsNullOrEmpty(_cfg.Lang) ? "" : $", lang {_cfg.Lang}") + " ===", LogChannel.Files);

        try
        {
            _userStoppedServer = false; // starting a batch — server is expected up
            Log("Проверяю/поднимаю MinerU…", LogChannel.System);
            if (!await _server.EnsureStartedAsync(_cts.Token))
            {
                Log("✗ Не удалось поднять MinerU. Обработка отменена.", LogChannel.System);
                Log("✗ Обработка отменена: сервер не поднялся.", LogChannel.Files);
                return;
            }
            await RefreshLampAsync();

            var proc = new BatchProcessor(_cfg,
                m => Log(m, LogChannel.Files),
                m => Log(m, LogChannel.System));
            var progress = new Progress<double>(p =>
            {
                int v = Math.Clamp((int)Math.Round(p * 1000), 0, 1000);
                if (InvokeRequired) BeginInvoke(() => _progress.Value = v);
                else _progress.Value = v;
            });

            // Thermal GPU throttle for the batch (suspends the GPU workers when hot,
            // never the process owning the API port so status polls keep working).
            _throttle = new GpuThrottle(() => _server.ServerPid, _cfg.Port, _gpuMon,
                () => _targetTempValue, m => Log(m, LogChannel.System));
            _throttle.Start();

            var res = await proc.RunAsync(files, outDir, mode, progress, _cts.Token, ragSaveImages);
            Log($"=== Готово: успешно {res.Ok}, с ошибками {res.Failed} ===", LogChannel.Files);
            if (res.Failed > 0)
                Log("Сбойные файлы: " + string.Join("; ", res.FailedFiles), LogChannel.Files);
        }
        catch (OperationCanceledException)
        {
            Log("=== Отменено пользователем ===", LogChannel.Files);
        }
        catch (Exception ex)
        {
            Log("✗ Ошибка: " + ex.Message, LogChannel.Files);
        }
        finally
        {
            _throttle?.Stop();   // always leaves the mineru tree resumed
            _throttle = null;
            _cts?.Dispose();
            _cts = null;
            SetProcessing(false);
        }
    }

    private void SetProcessing(bool on)
    {
        _processing = on;
        _btnProcess.Enabled = !on;
        _btnCancel.Enabled = on;
        _btnStart.Enabled = !on;
        _btnStop.Enabled = !on;
        _btnPick.Enabled = !on;
        _btnRemove.Enabled = !on;
        _btnClear.Enabled = !on;
    }

    private void Log(string msg, LogChannel channel = LogChannel.System)
    {
        var box = channel == LogChannel.Files ? _logFiles : _logSys;
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
        if (box.IsDisposed) return;
        if (box.InvokeRequired) box.BeginInvoke(() => AppendLog(box, line));
        else AppendLog(box, line);
    }

    private static void AppendLog(TextBox box, string line)
    {
        box.AppendText(line);
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        try { _throttle?.Stop(); } catch { } // never leave the tree suspended
        _healthTimer.Stop();
        _server.Dispose(); // kills mineru-api + worker tree
    }
}
