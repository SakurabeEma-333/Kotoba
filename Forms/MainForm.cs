using Kotoba.Models;
using Kotoba.Services;
using System.Runtime.InteropServices;

namespace Kotoba.Forms;

public partial class MainForm : Form
{
    // ---- 全局热键（F8 触发一次翻译）----
    private const int HOTKEY_ID = 0x4B54;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>F8 是否注册成功（被其他程序占用时会失败）</summary>
    public static bool HotkeyRegistered { get; private set; }

    private readonly AppConfig _cfg;
    private Profile? _current;
    private bool _loading;

    private readonly ListBox _lstProfiles = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnDelete = new();

    private readonly TextBox _txtName = new();
    private readonly TextBox _txtPrompt = new();
    private readonly CheckBox _chkShowSource = new();
    private readonly NumericUpDown _numFont = new();
    private readonly TrackBar _trkOpacity = new();
    private readonly Label _lblOpacity = new();
    private readonly CheckBox _chkAuto = new();
    private readonly Button _btnStart = new();

    private TableLayoutPanel? _tlp;
    private SplitContainer? _split;
    private Panel? _rightPanel;
    private bool _dpiScaled;

    // ---- 运行时会话 ----
    private readonly Label _lblUsage = new();
    private NotifyIcon? _tray;
    private bool _reallyExit;
    private readonly TranslationPipeline _pipeline = new();
    private CaptureForm? _capture;
    private OverlayForm? _overlay;
    private System.Windows.Forms.Timer? _autoTimer;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _sessionRunning;
    private bool _autoPausedByBall;

    /// <summary>输入框防抖用：停止 500ms 后才真正写盘</summary>
    private readonly System.Windows.Forms.Timer _persistTimer = new() { Interval = 500 };
    private MiniBallForm? _miniBall;

    /// <summary>存上下文用：中文不转 \uXXXX，config.json 里能直接看懂</summary>
    private static readonly System.Text.Json.JsonSerializerOptions HistoryJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private int[]? _autoPrev;
    private int[]? _autoTranslated;
    private DateTime _autoLastTrigger = DateTime.MinValue;
    private DateTime _autoDiffSince = DateTime.MinValue;
    private DateTime _autoLastChange = DateTime.MinValue;

    /// <summary>前文对话（原文+译文），供上下文使用；切配置时清空</summary>
    private readonly List<(string Src, string Dst)> _history = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDC, uint nFlags);

    /// <summary>UI 坐标系是物理像素，非 96DPI 时手动整树缩放</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey();
    }

    private void RestoreWindowBounds()
    {
        var s = _cfg.MainBounds;
        if (string.IsNullOrWhiteSpace(s)) return;

        var parts = s.Split(',');
        if (parts.Length != 4) return;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h)) return;

        var r = new Rectangle(x, y, w, h);
        // 校验窗口至少有一部分在某个屏幕里，否则用默认位置
        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
            Bounds = r;
    }

    /// <summary>处理全局热键：F8 触发翻译（游戏在前台也能用）</summary>
    protected override void WndProc(ref Message m)
    {
        // F8 全局热键：游戏在前台也能触发
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            // 小球状态下不响应：窗口都藏着，翻了也看不见，白花钱
            if (_sessionRunning && !_busy && _miniBall == null)
            {
                _ = TranslateOnceAsync();
            }
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_dpiScaled) return;
        _dpiScaled = true;

        float sc = DeviceDpi / 96f;
        if (sc > 1.01f)
        {
            Scale(new SizeF(sc, sc));
            if (_split != null) _split.SplitterDistance = (int)(210 * sc);

        }

        RestoreWindowBounds();
        SetupTray();
    }

    public MainForm(string? shotPath = null)
    {
        _cfg = ConfigStore.Load();

        // 启动时恢复深色主题（之前只在 --dark 或关闭设置窗时生效，重启就丢）
        if (_cfg.DarkTheme) Theme.Dark = true;

        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            Persist();
        };

        // 旧版内置提示词在配置里，现在改由代码内置：启动时迁掉旧分类（保留用户自己加的）
        if (PresetMigration.Run(_cfg)) ConfigStore.Save(_cfg);

        Text = $"Kotoba v{typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(940, 700);
        MinimumSize = new Size(820, 620);
        Font = new Font("Microsoft YaHei UI", 9F);

        

        BuildUi();
        RefreshProfileList();
        Theme.Apply(this);

        if (!string.IsNullOrEmpty(shotPath))
        {
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var diag = "DeviceDpi=" + DeviceDpi
                        + "\nClientSize=" + ClientSize.Width + "x" + ClientSize.Height
                        + "\nBounds=" + Bounds
                        + "\nAutoScaleMode=" + AutoScaleMode
                        + "\nAutoScaleFactor=" + AutoScaleFactor
                        + "\nAutoScaleDimensions=" + AutoScaleDimensions
                        + "\nCurrentAutoScaleDimensions=" + CurrentAutoScaleDimensions
                        + "\nsplit.Width=" + (_split?.Width.ToString() ?? "null")
                        + " split.Dist=" + (_split?.SplitterDistance.ToString() ?? "null")
                        + "\nlst.Width=" + _lstProfiles.Width
                        + " lst.Height=" + _lstProfiles.Height
                        + "\nright.Width=" + (_rightPanel?.Width.ToString() ?? "null")
                        + "\ntlp=" + (_tlp?.Width.ToString() ?? "null") + "x" + (_tlp?.Height.ToString() ?? "null")
                        + "\ntxtPrompt.Width=" + _txtPrompt.Width + " Height=" + _txtPrompt.Height
                        + "\nbtnStart.Width=" + _btnStart.Width;
                    File.WriteAllText(Path.ChangeExtension(shotPath!, ".txt"), diag);

                    float sc = DeviceDpi / 96f;
                    int sw = Width;
                    int sh = Height;
                    using var bmp = new Bitmap(sw, sh);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(Handle, hdc, 2);
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save(shotPath!, System.Drawing.Imaging.ImageFormat.Png);
                    _reallyExit = true;   // 调试截图模式要真退出，不能进托盘
                    Console.WriteLine("shot saved: " + shotPath + " " + sw + "x" + sh + " scale=" + sc);
                }
                catch (Exception ex) { Console.WriteLine("shot failed: " + ex.Message); }
                Close();
            };
            t.Start();
        }
    }

    // ---------------- UI ----------------

    private void BuildUi()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 210,
            IsSplitterFixed = false
        };
        _split = split;

        // ---- 左侧：配置列表 ----
        _lstProfiles.Dock = DockStyle.Fill;
        _lstProfiles.IntegralHeight = false;
        _lstProfiles.Font = new Font("Microsoft YaHei UI", 10F);
        _lstProfiles.SelectedIndexChanged += (_, _) => OnProfileSelected();
        _lstProfiles.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int idx = _lstProfiles.IndexFromPoint(e.Location);
            if (idx >= 0) _lstProfiles.SelectedIndex = idx;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("重命名…", null, (_, _) => RenameProfile());
        menu.Items.Add("查看上下文…", null, (_, _) => OpenContextView());
        menu.Items.Add("术语表…", null, (_, _) => OpenGlossary());
        menu.Items.Add("记忆…", null, (_, _) => OpenMemory());
        menu.Items.Add("编辑系统提示词", null, (_, _) =>
        {
            _txtPrompt.Focus();
            _txtPrompt.SelectionStart = _txtPrompt.TextLength;
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清空上下文", null, (_, _) => ClearHistory());
        menu.Items.Add("复制此配置", null, (_, _) => DuplicateProfile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除此配置", null, (_, _) => DeleteProfile());
        _lstProfiles.ContextMenuStrip = menu;

        var leftBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };

        _btnNew.Text = "新建";
        _btnNew.Width = 88;
        _btnNew.Height = 30;
        _btnNew.Click += (_, _) => NewProfile();

        _btnDelete.Text = "删除";
        _btnDelete.Width = 88;
        _btnDelete.Height = 30;
        _btnDelete.Click += (_, _) => DeleteProfile();

        leftBottom.Controls.Add(_btnNew);
        leftBottom.Controls.Add(_btnDelete);

        split.Panel1.Padding = new Padding(8);
        split.Panel1.Controls.Add(_lstProfiles);
        split.Panel1.Controls.Add(leftBottom);

        // ---- 右侧：编辑区 ----
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8), AutoScroll = true };
        _rightPanel = right;

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = false
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _tlp = tlp;

        int row = 0;
        void AddRow(string label, Control ctrl, int height)
        {
            tlp.RowCount = row + 1;
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            tlp.Controls.Add(new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoSize = false
            }, 0, row);
            ctrl.Dock = DockStyle.Fill;
            tlp.Controls.Add(ctrl, 1, row);
            row++;
        }

        AddRow("配置名称", _txtName, 32);
        _txtName.TextChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _current.Name = _txtName.Text;
            RefreshProfileListLabels();
            SchedulePersist();
        };

        AddRow("系统提示词", _txtPrompt, 210);
        _txtPrompt.Multiline = true;
        _txtPrompt.ScrollBars = ScrollBars.Vertical;
        _txtPrompt.AcceptsReturn = true;
        _txtPrompt.Font = new Font("Microsoft YaHei UI", 9.5F);
        _txtPrompt.TextChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _current.SystemPrompt = _txtPrompt.Text;
            SchedulePersist();
        };

        var promptBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };

        var btnPresets = new Button { Text = "预设 ▾", Width = 116, Height = 28 };
        btnPresets.Click += (_, _) => ShowPresetMenu(btnPresets);
        promptBtns.Controls.Add(btnPresets);

        AddRow("", promptBtns, 36);

        // 模型选择、API Key 已移到「设置」里（见 SettingsForm）

        AddRow("显示原文", _chkShowSource, 30);
        _chkShowSource.Text = "在译文窗里同时显示识别到的日文原文";
        _chkShowSource.CheckedChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _current.ShowSource = _chkShowSource.Checked;
            Persist();
        };

        AddRow("译文字号", _numFont, 32);
        _numFont.Minimum = 10;
        _numFont.Maximum = 72;
        _numFont.ValueChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _current.FontSize = (int)_numFont.Value;
            _overlay?.ApplyStyle(_current.FontSize, _current.BgOpacity, DeviceDpi);
            Persist();
        };

        var opacityPanel = new Panel { Dock = DockStyle.Fill };
        _trkOpacity.Minimum = 0;
        _trkOpacity.Maximum = 255;
        _trkOpacity.TickFrequency = 20;
        _trkOpacity.Dock = DockStyle.Fill;
        _trkOpacity.ValueChanged += (_, _) =>
        {
            _lblOpacity.Text = _trkOpacity.Value.ToString();
            if (_loading || _current == null) return;
            _current.BgOpacity = _trkOpacity.Value;
            _overlay?.ApplyStyle(_current.FontSize, _current.BgOpacity, DeviceDpi);
            Persist();
        };
        _lblOpacity.Text = "200";
        _lblOpacity.AutoSize = false;
        _lblOpacity.Width = 42;
        _lblOpacity.Dock = DockStyle.Right;
        _lblOpacity.TextAlign = ContentAlignment.MiddleCenter;
        opacityPanel.Controls.Add(_trkOpacity);
        opacityPanel.Controls.Add(_lblOpacity);
        AddRow("背景不透明度", opacityPanel, 36);

        AddRow("自动翻译", _chkAuto, 30);
        _chkAuto.Text = "捕获框内文字变化后自动翻译（无需手动点击）";
        _chkAuto.CheckedChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _current.AutoMode = _chkAuto.Checked;
            _capture?.SetAutoState(_current.AutoMode);
            if (_sessionRunning)
            {
                if (_current.AutoMode) StartAutoTimer();
                else { _autoTimer?.Stop(); _autoTimer?.Dispose(); _autoTimer = null; }
            }
            Persist();
        };

        var startPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
        _btnStart.Text = "▶  开始（显示捕获框）";
        _btnStart.Width = 220;
        _btnStart.Height = 38;
        _btnStart.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _btnStart.Click += (_, _) => OnStartClicked();

        var btnSettings = new Button
        {
            Text = "⚙  设置",
            Width = 110,
            Height = 38,
            Margin = new Padding(12, 0, 0, 0)
        };
        btnSettings.Click += (_, _) => OpenSettings();

        var btnClearCtx = new Button
        {
            Text = "清空历史",
            Width = 120,
            Height = 38,
            Margin = new Padding(12, 0, 0, 0)
        };
        btnClearCtx.Click += (_, _) => ClearHistory();

        startPanel.Controls.Add(_btnStart);
        startPanel.Controls.Add(btnSettings);
        startPanel.Controls.Add(btnClearCtx);
        AddRow("", startPanel, 52);

        _lblUsage.Text = UsageTracker.Summary();
        _lblUsage.AutoSize = true;
        _lblUsage.ForeColor = Color.FromArgb(120, 126, 138);
        _lblUsage.Font = new Font("Microsoft YaHei UI", 8.5F);
        AddRow("", _lblUsage, 26);

        right.Controls.Add(tlp);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
    }

    // ---------------- 数据 ----------------

    private void RefreshProfileList()
    {
        _loading = true;
        _lstProfiles.Items.Clear();
        foreach (var p in _cfg.Profiles) _lstProfiles.Items.Add(p.Name);

        var idx = _cfg.Profiles.FindIndex(p => p.Id == _cfg.LastProfileId);
        if (idx < 0 && _cfg.Profiles.Count > 0) idx = 0;
        if (idx >= 0) _lstProfiles.SelectedIndex = idx;
        _loading = false;

        LoadProfileIntoUi();

        // 启动时就把磁盘上的上下文读进内存，否则没开会话前点「查看上下文」会看到空白
        LoadHistoryFromProfile();
    }

    private void RefreshProfileListLabels()
    {
        _loading = true;
        var sel = _lstProfiles.SelectedIndex;
        _lstProfiles.Items.Clear();
        foreach (var p in _cfg.Profiles) _lstProfiles.Items.Add(p.Name);
        if (sel >= 0 && sel < _lstProfiles.Items.Count) _lstProfiles.SelectedIndex = sel;
        _loading = false;
    }

    private void OnProfileSelected()
    {
        if (_loading) return;
        var i = _lstProfiles.SelectedIndex;
        if (i < 0 || i >= _cfg.Profiles.Count) return;

        // 会话中用旧配置的捕获框/译文窗还在跑，此时切配置会把旧框的坐标写进新配置。
        // 直接结束会话，避免 Roi/Out 归属错乱。
        if (_sessionRunning) StopSession(restoreWindow: true);

        _cfg.LastProfileId = _cfg.Profiles[i].Id;
        _history.Clear();          // 换配置 = 换游戏，上下文自然作废
        // 同时重置自动模式的状态，否则新配置可能被误判为「已翻译过」
        _autoPrev = null;
        _autoTranslated = null;
        _autoLastTrigger = DateTime.MinValue;
        _autoDiffSince = DateTime.MinValue;
        _autoLastChange = DateTime.MinValue;
        Persist();
        LoadProfileIntoUi();
        LoadHistoryFromProfile();
        _overlay?.ClearDisplay();
    }

    private void RenameProfile()
    {
        if (_current == null) return;
        using var dlg = new InputForm("重命名配置", "配置名称：", _current.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.Value)) return;

        _current.Name = dlg.Value;
        Persist();
        RefreshProfileListLabels();
        LoadProfileIntoUi();
    }

    private void ShowPresetMenu(Control anchor)
    {
        if (_current == null) return;

        var menu = PresetMenuBuilder.Build(_cfg,
            applyPreset: ApplyPreset,
            onNewPreset: NewPreset,
            onManage: ManagePresets,
            onSaveCurrent: SaveCurrentAsPreset);

        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void ApplyPreset(string content)
    {
        if (_current == null) return;

        _current.SystemPrompt = content;
        _loading = true;
        _txtPrompt.Text = NormalizeNewlines(content);
        _loading = false;
        Persist();
    }

    private void SaveCurrentAsPreset()
    {
        if (_current == null) return;

        using var dlg = new InputForm("保存为预设", "预设名称：", "");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.Value)) return;

        // 存进「我的」分类（旧字段 _cfg.Presets 已废弃，不再被读取）
        var cat = _cfg.Categories.FirstOrDefault(c => !c.BuiltIn);
        if (cat == null)
        {
            cat = new PresetCategory { Name = "我的" };
            _cfg.Categories.Add(cat);
        }

        var name = dlg.Value.Trim();
        var exist = cat.Presets.FirstOrDefault(p => p.Name == name);
        if (exist != null) exist.Content = _current.SystemPrompt;
        else cat.Presets.Add(new PromptPreset { Name = name, Content = _current.SystemPrompt });

        Persist();
        MessageBox.Show(this, $"已保存到「{cat.Name}」：{name}", "Kotoba");
    }

    private void NewPreset()
    {
        using var dlg = new PresetEditForm(_cfg);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var cat = dlg.TargetCategory;
        if (cat == null) return;

        // 同名则覆盖内容
        var existing = cat.Presets.FirstOrDefault(p => p.Name == dlg.PresetName);
        if (existing != null) existing.Content = dlg.PresetContent;
        else cat.Presets.Add(new PromptPreset { Name = dlg.PresetName, Content = dlg.PresetContent });

        Persist();
        MessageBox.Show(this, $"已保存到「{cat.Name}」。", "Kotoba");
    }

    private void ManagePresets()
    {
        using var dlg = new PresetManagerForm(_cfg);
        dlg.ShowDialog(this);
        Persist();
    }

    private void OpenContextView()
    {
        if (_current == null) return;

        using var dlg = new ContextForm(_current, _cfg, _history);
        dlg.HistoryImported += (_, _) => SaveHistoryToProfile();
        var result = dlg.ShowDialog(this);
        // 只要生成过总结就保存（否则点 × 关窗会把总结丢掉）；没总结时仍尊重取消
        if (result != DialogResult.OK && !dlg.MemoryUpdated) return;

        if (dlg.MemoryUpdated)
        {
            _current.Memory = dlg.NewMemory;
            Persist();
        }
    }

    private void OpenGlossary()
    {
        if (_current == null) return;

        using var dlg = new GlossaryForm(_current);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _current.Glossary = dlg.ResultGlossary;
        Persist();
    }

    private void OpenMemory()
    {
        if (_current == null) return;

        using var dlg = new MemoryForm(_current.Name, _current.Memory);
        dlg.Cleared += (_, _) =>
        {
            _current.Memory = "";
            Persist();
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _current.Memory = dlg.Memory;
        Persist();
    }

    private void DuplicateProfile()
    {
        if (_current == null) return;

        // 会话中换配置会把旧捕获框的坐标写进新配置，先停会话
        if (_sessionRunning) StopSession(restoreWindow: true);

        var copy = _current.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = _current.Name + " 副本";

        _cfg.Profiles.Add(copy);
        _cfg.LastProfileId = copy.Id;
        _history.Clear();
        copy.SavedHistory = "";
        _overlay?.ClearDisplay();
        Persist();
        RefreshProfileList();
    }

    private void LoadProfileIntoUi()
    {
        _loading = true;
        _current = _cfg.EnsureProfile();

        _txtName.Text = _current.Name;
        // TextBox 只认 \r\n，预设/默认提示词里是 \n，不转就全挤成一行
        _txtPrompt.Text = NormalizeNewlines(_current.SystemPrompt);
        _chkShowSource.Checked = _current.ShowSource;
        _numFont.Value = Math.Clamp(_current.FontSize, (int)_numFont.Minimum, (int)_numFont.Maximum);
        _trkOpacity.Value = Math.Clamp(_current.BgOpacity, _trkOpacity.Minimum, _trkOpacity.Maximum);
        _lblOpacity.Text = _trkOpacity.Value.ToString();
        _chkAuto.Checked = _current.AutoMode;

        _loading = false;
    }

    /// <summary>
    /// 弹模态对话框时临时藏起浮窗。
    /// 浮窗是 TopMost，会盖在 MessageBox 上面；而 MessageBox 会阻塞 UI 线程，
    /// 导致浮窗的拖动也失效——用户两边都点不到，看起来就是卡死。
    /// </summary>
    /// <summary>
    /// 主窗口不在最小化状态时，浮窗取消置顶。
    /// 否则浮窗（TopMost）会盖住主窗口、设置窗口、MessageBox，
    /// 而 MessageBox 会阻塞 UI 线程，导致两边都点不到 → 看起来卡死。
    /// </summary>
    private void UpdateOverlayTopMost()
    {
        bool top = WindowState == FormWindowState.Minimized;

        if (_capture != null && !_capture.IsDisposed) _capture.TopMost = top;
        if (_overlay != null && !_overlay.IsDisposed) _overlay.TopMost = top;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateOverlayTopMost();
    }

    private void HideOverlaysTemporarily(Action dialog)
    {
        bool capWasVisible = _capture?.Visible == true;
        bool ovWasVisible = _overlay?.Visible == true;

        if (capWasVisible) _capture!.Hide();
        if (ovWasVisible) _overlay!.Hide();

        try
        {
            dialog();
        }
        finally
        {
            if (capWasVisible && _capture != null && !_capture.IsDisposed)
            {
                _capture.Show();
                KeepOnTop(_capture);
            }
            if (ovWasVisible && _overlay != null && !_overlay.IsDisposed)
            {
                _overlay.Show();
                KeepOnTop(_overlay);
            }
        }
    }

    /// <summary>Hide/Show 之后 WS_EX_TOPMOST 会失效，这里强制重新置顶（否则调完设置译文窗就被别的窗口盖住了）</summary>
    private static void KeepOnTop(Form? f)
    {
        if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
        try
        {
            f.TopMost = false;
            f.TopMost = true;
        }
        catch { }
    }

    private static string NormalizeNewlines(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private void OpenSettings()
    {
        if (_current == null) return;

        DialogResult result = DialogResult.Cancel;
        HideOverlaysTemporarily(() =>
        {
            using var dlg = new SettingsForm(_cfg, _current, _pipeline, ResetWindowLayout);
            result = dlg.ShowDialog(this);
        });

        if (result != DialogResult.OK) return;

        Theme.Dark = _cfg.DarkTheme;
        Theme.Apply(this);
        RegisterHotkey();
        Invalidate(true);

        Persist();
        LoadProfileIntoUi();
        ApplyRuntimeStyle();
    }

    /// <summary>把捕获框和译文窗的位置按默认比例重新排一遍</summary>
    private void ResetWindowLayout()
    {
        if (_current == null) return;

        _current.RoiX = 0.10;
        _current.RoiY = 0.62;
        _current.RoiW = 0.80;
        _current.RoiH = 0.26;
        _current.OutX = 0.10;
        _current.OutY = 0.12;
        _current.OutW = 0.62;
        _current.OutH = 0.20;

        ApplyWindowLayout();
        Persist();
    }

    private void ApplyWindowLayout()
    {
        if (_current == null) return;
        var vs = VirtualScreenRect;

        if (_capture != null && !_capture.IsDisposed)
        {
            var roi = new Rectangle(
                vs.X + (int)(_current.RoiX * vs.Width),
                vs.Y + (int)(_current.RoiY * vs.Height),
                Math.Max(200, (int)(_current.RoiW * vs.Width)),
                Math.Max(120, (int)(_current.RoiH * vs.Height)));
            _capture.ApplyBounds(roi, _current.Locked);
        }

        if (_overlay != null && !_overlay.IsDisposed)
        {
            _overlay.Bounds = new Rectangle(
                vs.X + (int)(_current.OutX * vs.Width),
                vs.Y + (int)(_current.OutY * vs.Height),
                Math.Max(240, (int)(_current.OutW * vs.Width)),
                Math.Max(100, (int)(_current.OutH * vs.Height)));
        }
    }

    private void ApplyRuntimeStyle()
    {
        if (_current == null) return;
        _overlay?.ApplyStyle(_current.FontSize, _current.BgOpacity, DeviceDpi);
        _capture?.SetCaptureOpacity(_current.CaptureOpacity);
        if (_capture != null) _capture.BarTransparent = _current.BarTransparent;
        if (_capture != null) _capture.ShowBorderLine = _current.ShowBorderLine;
        if (_capture != null) _capture.ShowCornerGrips = _current.ShowCornerGrips;
        _capture?.SetFrameStyle(_current.CaptureBandWidth, _current.CaptureLineWidth, _current.CaptureRevealPad);
    }

    private void NewProfile()
    {
        // 会话中换配置会把旧捕获框的坐标写进新配置，先停会话
        if (_sessionRunning) StopSession(restoreWindow: true);

        var p = new Profile { Name = "新配置 " + (_cfg.Profiles.Count + 1) };
        _cfg.Profiles.Add(p);
        _cfg.LastProfileId = p.Id;
        Persist();
        RefreshProfileList();
    }

    private void DeleteProfile()
    {
        var i = _lstProfiles.SelectedIndex;
        if (i < 0 || _cfg.Profiles.Count <= 1)
        {
            HideOverlaysTemporarily(() => MessageBox.Show(this,
                "至少要保留一套配置。", "Kotoba", MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }

        var p = _cfg.Profiles[i];

        // 会话中换配置会把旧捕获框的坐标写进新配置，先停会话
        if (_sessionRunning) StopSession(restoreWindow: true);

        DialogResult answer = DialogResult.No;
        HideOverlaysTemporarily(() =>
        {
            answer = MessageBox.Show(this, $"确定删除配置「{p.Name}」？", "Kotoba",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        });

        if (answer != DialogResult.Yes) return;

        _cfg.Profiles.RemoveAt(i);
        _cfg.LastProfileId = _cfg.Profiles[Math.Max(0, i - 1)].Id;
        Persist();
        RefreshProfileList();
    }

    /// <summary>输入框这类高频改动做 500ms 防抖，避免每敲一个字就整份写盘</summary>
    private void SchedulePersist()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void Persist() => ConfigStore.Save(_cfg);

    /// <summary>缩成小球：藏起捕获框和译文窗，只留一个可点还原的小球（会话继续跑）</summary>
    private void MinimizeToBall()
    {
        if (!_sessionRunning || _miniBall != null) return;

        // 收起时暂停自动翻译：看不见结果还在后台翻，等于白花钱
        _autoPausedByBall = _autoTimer != null;
        _autoTimer?.Stop();
        _autoTimer?.Dispose();
        _autoTimer = null;
        _capture?.SetAutoState(false);

        var vs = VirtualScreenRect;
        _miniBall = new MiniBallForm();
        _miniBall.Location = new Point(vs.X + 16, vs.Y + 16);
        _miniBall.Restored += (_, _) => RestoreFromBall();

        try { _capture?.Hide(); } catch { }
        try { _overlay?.Hide(); } catch { }

        _miniBall.Show();
    }

    private void RestoreFromBall()
    {
        var ball = _miniBall;
        _miniBall = null;
        try { ball?.Hide(); } catch { }
        try { ball?.Dispose(); } catch { }

        if (_capture != null && !_capture.IsDisposed)
        {
            _capture.Show();
            KeepOnTop(_capture);
            _capture.SetCaptureOpacity(_current?.CaptureOpacity ?? 45);
        }

        if (_overlay != null && !_overlay.IsDisposed)
        {
            _overlay.Show();
            KeepOnTop(_overlay);
            if (_current != null) _overlay.ApplyStyle(_current.FontSize, _current.BgOpacity, DeviceDpi);
        }

        // 还原后把自动模式恢复回去
        if (_autoPausedByBall && _current?.AutoMode == true) StartAutoTimer();
        _autoPausedByBall = false;
        _capture?.SetAutoState(_current?.AutoMode == true);
    }

    private void OnStartClicked()
    {
        if (_current == null) return;

        if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
        {
            HideOverlaysTemporarily(() => MessageBox.Show(this,
                "还没有填 API Key。\n\n请点「⚙ 设置」填入你的 DeepSeek Key。",
                "Kotoba", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return;
        }

        Persist();

        if (_sessionRunning) StopSession(restoreWindow: false);
        else StartSession();
    }

    private static Rectangle VirtualScreenRect => SystemInformation.VirtualScreen;

    private void StartSession()
    {
        if (_current == null) return;
        _sessionRunning = true;
        _btnStart.Text = "■  停止";

        var vs = VirtualScreenRect;

        var roi = new Rectangle(
            vs.X + (int)(_current.RoiX * vs.Width),
            vs.Y + (int)(_current.RoiY * vs.Height),
            Math.Max(200, (int)(_current.RoiW * vs.Width)),
            Math.Max(120, (int)(_current.RoiH * vs.Height)));

        _capture = new CaptureForm(_current.Name);
        _capture.SetCaptureOpacity(_current.CaptureOpacity);
        _capture.BarTransparent = _current.BarTransparent;
        _capture.ShowBorderLine = _current.ShowBorderLine;
        _capture.ShowCornerGrips = _current.ShowCornerGrips;
        _capture.SetFrameStyle(_current.CaptureBandWidth, _current.CaptureLineWidth, _current.CaptureRevealPad);
        _capture.TranslateRequested += async (_, _) => await TranslateOnceAsync();
        _capture.RetranslateRequested += async (_, _) => await TranslateOnceAsync(ignoreCache: true);
        // 「关闭」= 和托盘菜单的「关闭悬浮窗」一致：直接结束会话
        _capture.CloseRequested += (_, _) => OnStartClicked();
        // 「缩」= 两个窗藏起来只留一个小球，不结束会话
        _capture.ShrinkRequested += (_, _) => MinimizeToBall();
        // 捕获框上的「清空」：与主界面「清空历史」完全一致（清上下文 + 清输出框显示）
        _capture.ClearContextRequested += (_, _) => ClearHistory();
        _capture.SetAutoState(_current.AutoMode);
        _capture.AutoToggleRequested += (_, _) =>
        {
            if (_current == null || _capture == null) return;
            _current.AutoMode = !_current.AutoMode;

            _loading = true;
            _chkAuto.Checked = _current.AutoMode;
            _loading = false;

            _capture.SetAutoState(_current.AutoMode);

            if (_current.AutoMode) StartAutoTimer();
            else { _autoTimer?.Stop(); _autoTimer?.Dispose(); _autoTimer = null; }

            Persist();
        };
        _capture.RoiChanged += (_, _) => SaveRoiToProfile();
        _capture.LockChanged += (_, _) =>
        {
            if (_current == null || _capture == null) return;
            _current.Locked = _capture.Locked;
            Persist();
        };
        _capture.ApplyBounds(roi, _current.Locked);
        _capture.Show();
        KeepOnTop(_capture);

        var outRect = new Rectangle(
            vs.X + (int)(_current.OutX * vs.Width),
            vs.Y + (int)(_current.OutY * vs.Height),
            Math.Max(240, (int)(_current.OutW * vs.Width)),
            Math.Max(100, (int)(_current.OutH * vs.Height)));

        _overlay = new OverlayForm();
        _overlay.Bounds = outRect;
        _overlay.ApplyStyle(_current.FontSize, _current.BgOpacity, DeviceDpi);
        _overlay.BoundsChanged += (_, _) => SaveOutToProfile();
        _overlay.Show();
        KeepOnTop(_overlay);
        _overlay.ShowWaiting("已就绪 — 点捕获框上的「⚡ 翻译」");

        if (_current.AutoMode) StartAutoTimer();

        // 恢复上次保存的上下文（同一配置切回来也能接着用）
        LoadHistoryFromProfile();

        WindowState = FormWindowState.Minimized;
    }

    private void StopSession(bool restoreWindow = true)
    {
        _sessionRunning = false;
        _btnStart.Text = "▶  开始（显示捕获框）";

        // 如果还停在小球状态，把球一起收掉
        if (_miniBall != null)
        {
            try { _miniBall.Hide(); } catch { }
            try { _miniBall.Dispose(); } catch { }
            _miniBall = null;
        }

        _autoTimer?.Stop();
        _autoTimer?.Dispose();
        _autoTimer = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_capture != null) { _capture.Close(); _capture.Dispose(); _capture = null; }
        if (_overlay != null) { _overlay.Close(); _overlay.Dispose(); _overlay = null; }

        if (restoreWindow) Show();
        if (restoreWindow) Activate();
    }

    private void StartAutoTimer()
    {
        _autoTimer?.Stop();
        _autoTimer?.Dispose();
        _autoPrev = null;
        _autoTranslated = null;
        _autoLastTrigger = DateTime.MinValue;
        _autoDiffSince = DateTime.MinValue;
        _autoLastChange = DateTime.MinValue;

        int interval = Math.Clamp(_current?.AutoIntervalMs ?? 250, 150, 400);
        int cooldown = Math.Clamp(_current?.AutoStableMs ?? 1200, 500, 5000);
        int quietMs = Math.Clamp(_current?.AutoQuietMs ?? 800, 300, 5000);

        _autoTimer = new System.Windows.Forms.Timer { Interval = interval };
        _autoTimer.Tick += async (_, _) =>
        {
            if (_busy || _capture == null || _current == null || !_capture.IsHandleCreated) return;
            try
            {
                var roi = _capture.RoiRect;
                roi.Intersect(VirtualScreenRect);
                if (roi.Width < 8 || roi.Height < 8) return;

                using var probe = ScreenCapturer.Capture(roi);
                var now = DateTime.UtcNow;
                var samples = ScreenCapturer.Samples(probe);

                // 和“已经翻过的那一帧”差多少：少于 4 个采样点算没变
                int diffTranslated = ScreenCapturer.DiffCount(_autoTranslated, samples);
                // 和上一帧比：≤2 个点算这一瞬间没动（光标闪烁正好落在这个范围）
                int diffPrev = ScreenCapturer.DiffCount(_autoPrev, samples);
                _autoPrev = samples;
                if (diffPrev > 2 || _autoLastChange == DateTime.MinValue) _autoLastChange = now;

                if (diffTranslated < 4)
                {
                    _autoDiffSince = DateTime.MinValue;   // 和已翻内容一样，清掉等待计时
                    return;
                }

                if (_autoDiffSince == DateTime.MinValue) _autoDiffSince = now;

                // 关键：不只是“这一瞬间没动”，而是“已经安静了 800ms”。
                // 打字机中途会有短暂停顿，只比单帧会提前翻。
                bool quietLongEnough = (now - _autoLastChange).TotalMilliseconds >= quietMs;
                // 兜底：持续 6 秒都在变（常驻动画），也翻一次
                bool force = (now - _autoDiffSince).TotalSeconds >= 6;
                if (!quietLongEnough && !force) return;

                if ((now - _autoLastTrigger).TotalMilliseconds < cooldown) return;

                _autoTranslated = samples;
                _autoLastTrigger = now;
                _autoDiffSince = DateTime.MinValue;
                await TranslateOnceAsync();
            }
            catch { /* 自动模式容错：单次失败不影响下一轮 */ }
        };
        _autoTimer.Start();
    }

    private async Task TranslateOnceAsync(bool ignoreCache = false)
    {
        if (_current == null || _capture == null || _overlay == null) return;
        if (_busy) return;
        _busy = true;

        try
        {
            var roi = _capture.RoiRect;
            roi.Intersect(VirtualScreenRect);
            if (roi.Width < 8 || roi.Height < 8)
            {
                _overlay.ShowError("捕获区域无效");
                return;
            }

            _overlay.ShowWaiting(ignoreCache ? "重翻中…" : "识别中…");
            _overlay.SetStatus(ignoreCache ? "重翻中…" : "识别中…");

            _cts?.Dispose();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            // 显示用历史：不管发不发给模型，都要显示
            // 0 = 不显示也不发送，别再被 Math.Max(1, …) 抬成 1 条
            int showCount = Math.Max(0, _cfg.ContextCount);
            List<(string Src, string Dst)> displayHistory;
            if (_history.Count > 0 && showCount > 0)
            {
                int take = Math.Min(showCount, _history.Count);
                displayHistory = _history.Skip(_history.Count - take).ToList();
            }
            else
            {
                displayHistory = new List<(string, string)>();
            }

            // 发给模型的：仅在启用上下文时
            List<(string Src, string Dst)>? modelHistory = _cfg.ContextEnabled ? displayHistory : null;

            using var bmp = CaptureWithoutSelf(roi);

            if (_cfg.Streaming)
            {
                void OnUi(Action a)
                {
                    if (_overlay == null || !_overlay.IsHandleCreated) return;
                    if (_overlay.InvokeRequired) _overlay.BeginInvoke(a);
                    else a();
                }

                var sr = await _pipeline.RunStreamAsync(bmp, _current, _cfg, modelHistory,
                    pairs => OnUi(() =>
                    {
                        _overlay?.SetStatus("翻译中…");
                        _overlay?.StreamPairs(pairs, displayHistory, _cfg.ContextShowSource, _current.ShowSource);
                    }),
                    _cts.Token, ignoreCache);

                AddToHistory(sr.Source, sr.Target);
                _overlay?.SetStatus("完成", 3000);
                return;
            }

            var result = await _pipeline.RunAsync(bmp, _current, _cfg, modelHistory, _cts.Token, ignoreCache);

            AddToHistory(result.Source, result.Target);

            _overlay.ShowPairs(result.Pairs, displayHistory, _cfg.ContextShowSource,
                _current.ShowSource, _current.FontSize, _current.BgOpacity, DeviceDpi);
            _overlay.SetStatus(result.FromCache ? "完成（缓存）" : "完成", 3000);
        }
        catch (OperationCanceledException)
        {
            _overlay?.ShowError("请求超时");
            _overlay?.SetStatus("超时");
        }
        catch (Exception ex)
        {
            var friendly = FriendlyError(ex);
            _overlay?.ShowError(friendly);
            _overlay?.SetStatus("出错", 4000);
        }
        finally
        {
            _busy = false;
            // 翻译完刷新用量标签，否则永远显示启动那一刻的数字
            try { _lblUsage.Text = UsageTracker.Summary(); } catch { }
        }
    }

    /// <summary>
    /// 截取捕获区。捕获框自身只占据边框与工具条（均在 RoiRect 之外），
    /// 中间区域完全透明，所以不需要隐藏窗口也不会截到自己。
    /// </summary>
    private Bitmap CaptureWithoutSelf(Rectangle roi)
    {
        return ScreenCapturer.Capture(roi);
    }

    /// <summary>把各种异常翻译成用户能看懂的中文</summary>
    private static string FriendlyError(Exception ex)
    {
        if (ex is OperationCanceledException) return "请求超时（超过 120 秒无响应）";

        var m = ex.Message ?? "";
        if (m.Contains("401")) return "API Key 无效或已过期，请到设置里检查";
        if (m.Contains("402")) return "账户余额不足，请充值";
        if (m.Contains("429")) return "请求太频繁，稍等几秒再试";
        if (m.Contains("500") || m.Contains("502") || m.Contains("503")) return "服务器暂时不可用，稍后重试";
        if (m.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "网络超时，检查一下网络";
        if (m.Contains("SSL") || m.Contains("TLS")) return "网络连接被拦截（检查代理或防火墙）";

        if (ex is HttpRequestException) return "网络连接失败，检查网络或代理";
        if (ex is System.Text.Json.JsonException) return "接口返回格式异常";

        return m.Length > 120 ? m[..120] + "…" : m;
    }

    private void SaveRoiToProfile()
    {
        if (_capture == null || _current == null) return;
        var vs = VirtualScreenRect;
        var r = _capture.Bounds;
        _current.RoiX = (double)(r.X - vs.X) / vs.Width;
        _current.RoiY = (double)(r.Y - vs.Y) / vs.Height;
        _current.RoiW = (double)r.Width / vs.Width;
        _current.RoiH = (double)r.Height / vs.Height;
        Persist();
    }

    private void SaveOutToProfile()
    {
        if (_overlay == null || _current == null) return;
        var vs = VirtualScreenRect;
        var r = _overlay.Bounds;
        _current.OutX = (double)(r.X - vs.X) / vs.Width;
        _current.OutY = (double)(r.Y - vs.Y) / vs.Height;
        _current.OutW = (double)r.Width / vs.Width;
        _current.OutH = (double)r.Height / vs.Height;
        Persist();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 记住窗口位置
        _cfg.MainBounds = $"{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}";
        Persist();

        // 点 × 只是收进托盘，真正退出走托盘菜单的「退出」
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        UnregisterHotKey(Handle, HOTKEY_ID);
        if (_sessionRunning) StopSession(restoreWindow: false);
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}