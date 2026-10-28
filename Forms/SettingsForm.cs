using Kotoba.Models;
using Kotoba.Services;

namespace Kotoba.Forms;

/// <summary>
/// 设置窗口：左侧分类导航（接口 / 翻译 / 外观 / 维护），右侧按分组显示对应内容。
/// 每页内容超过可视高度时自动出滚动条（150% 缩放下尤其需要）。
/// </summary>
public class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Profile _profile;
    private readonly TranslationPipeline? _pipeline;
    private readonly Action? _resetWindows;
    private readonly string _usage = UsageTracker.Summary();

    // ---- 接口 ----
    private readonly TextBox _txtKey = new();
    private readonly TextBox _txtBaseUrl = new();
    private readonly ComboBox _cmbOcr = new();
    private readonly ComboBox _cmbModel = new();

    // ---- 翻译 ----
    private readonly CheckBox _chkThinking = new();
    private readonly CheckBox _chkStream = new();
    private readonly ComboBox _cmbHotkey = new();
    private readonly CheckBox _chkContext = new();
    private readonly NumericUpDown _numContext = new();
    private readonly CheckBox _chkCtxSource = new();

    // ---- 上下文 / 记忆 ----
    private readonly NumericUpDown _numHistMax = new();
    private readonly NumericUpDown _numHistSave = new();
    private readonly NumericUpDown _numSummaryCount = new();
    private readonly NumericUpDown _numMemoryMax = new();
    private readonly NumericUpDown _numAutoQuiet = new();
    private readonly NumericUpDown _numAutoCooldown = new();
    private readonly CheckBox _chkStrictCache = new();

    // ---- 外观 ----
    private readonly CheckBox _chkDark = new();
    private readonly TrackBar _trkOut = new();
    private readonly Label _lblOut = new();
    private readonly TrackBar _trkCap = new();
    private readonly Label _lblCap = new();
    private readonly NumericUpDown _numBand = new();
    private readonly NumericUpDown _numLine = new();
    private readonly NumericUpDown _numPad = new();

    // ---- 维护 ----
    private readonly Label _lblCache = new();

    // ---- 导航 ----
    private readonly ListBox _nav = new();
    private Panel? _pageApi;
    private Panel? _pageTranslate;
    private Panel? _pageContext;
    private Panel? _pageAppearance;
    private Panel? _pageMaintain;
    private Panel? _pageAbout;

    private static readonly Color SectionColor = Color.FromArgb(96, 104, 116);
    private static readonly Color HintColor = Color.FromArgb(120, 126, 138);
    private static readonly Color DividerColor = Color.FromArgb(228, 232, 238);

    public SettingsForm(AppConfig cfg, Profile profile,
        TranslationPipeline? pipeline = null, Action? resetWindows = null)
    {
        _cfg = cfg;
        _profile = profile;
        _pipeline = pipeline;
        _resetWindows = resetWindows;

        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 620);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();
        LoadValues();
        Theme.Apply(this);
    }

    // ---------------- UI ----------------

    private void BuildUi()
    {
        // 底部按钮条 + 上方分隔线
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 14, 20, 14) };
        var btnOk = new Button { Text = "确定", Width = 96, Height = 36, Dock = DockStyle.Right };
        var btnCancel = new Button { Text = "取消", Width = 96, Height = 36, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        btnCancel.Margin = new Padding(8, 0, 0, 0);

        btnOk.Click += (_, _) => { Apply(); DialogResult = DialogResult.OK; Close(); };

        bottom.Controls.Add(btnCancel);
        bottom.Controls.Add(btnOk);
        CancelButton = btnCancel;

        var divider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DividerColor };

        // 左侧导航
        _nav.Dock = DockStyle.Left;
        _nav.Width = 132;
        _nav.Font = new Font("Microsoft YaHei UI", 10F);
        _nav.BorderStyle = BorderStyle.None;
        _nav.IntegralHeight = false;
        _nav.ItemHeight = 44;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.Items.AddRange(new object[] { "接口", "翻译", "上下文", "外观", "维护", "关于" });
        _nav.SelectedIndex = 0;
        _nav.BackColor = Color.FromArgb(243, 245, 248);
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;

            using (var bg = new SolidBrush(selected ? Color.FromArgb(0, 120, 215) : _nav.BackColor))
                e.Graphics.FillRectangle(bg, e.Bounds);

            if (selected)
            {
                using var bar = new SolidBrush(Color.FromArgb(0, 90, 170));
                e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 4, e.Bounds.Height);
            }

            var text = _nav.Items[e.Index]?.ToString() ?? "";
            using var fg = new SolidBrush(selected ? Color.White : Color.FromArgb(48, 54, 64));
            using var font = selected
                ? new Font(_nav.Font, FontStyle.Bold)
                : new Font(_nav.Font, FontStyle.Regular);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            var r = new Rectangle(e.Bounds.X + 20, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height);
            e.Graphics.DrawString(text, font, fg, r, sf);
        };

        // 右侧内容区
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 12) };

        _pageApi = BuildApiPage();
        _pageTranslate = BuildTranslatePage();
        _pageContext = BuildContextPage();
        _pageAppearance = BuildAppearancePage();
        _pageMaintain = BuildMaintainPage();
        _pageAbout = BuildAboutPage();

        right.Controls.Add(_pageApi);
        right.Controls.Add(_pageTranslate);
        right.Controls.Add(_pageContext);
        right.Controls.Add(_pageAppearance);
        right.Controls.Add(_pageMaintain);
        right.Controls.Add(_pageAbout);

        _nav.SelectedIndexChanged += (_, _) => SwitchPage();
        SwitchPage();

        // Dock 顺序：最后添加的先布局
        Controls.Add(right);
        Controls.Add(_nav);
        Controls.Add(divider);
        Controls.Add(bottom);
    }

    private void SwitchPage()
    {
        var pages = new[] { _pageApi, _pageTranslate, _pageContext, _pageAppearance, _pageMaintain, _pageAbout };
        if (pages.Any(p => p == null)) return;

        for (int i = 0; i < pages.Length; i++)
            pages[i]!.Visible = i == _nav.SelectedIndex;

        int sel = Math.Clamp(_nav.SelectedIndex, 0, pages.Length - 1);
        pages[sel]!.BringToFront();
    }

    private static Panel BuildPage()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            AutoScroll = true,
            BackColor = Color.White
        };
    }

    private static TableLayoutPanel BuildGrid()
    {
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 4, 0, 0)
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return tlp;
    }

    private static void AddRow(TableLayoutPanel tlp, string label, Control ctrl, int height)
    {
        int row = tlp.RowCount;
        tlp.RowCount = row + 1;
        tlp.RowStyles.Add(height <= 0 ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, height));

        tlp.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Color.FromArgb(52, 58, 66)
        }, 0, row);

        ctrl.Dock = DockStyle.Fill;
        tlp.Controls.Add(ctrl, 1, row);
    }

    /// <summary>分组标题：灰字 + 细分隔线，横跨两列</summary>
    private static void AddSection(TableLayoutPanel tlp, string title)
    {
        int row = tlp.RowCount;
        tlp.RowCount = row + 1;
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 2) };
        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DividerColor };
        var lbl = new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = SectionColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Location = new Point(0, 0)
        };

        panel.Controls.Add(line);
        panel.Controls.Add(lbl);
        tlp.Controls.Add(panel, 0, row);
        tlp.SetColumnSpan(panel, 2);
    }

    private static Label Hint(string text)
    {
        // 小字灰色说明；不设 Dock，用 MaximumSize 限制宽度让它自己折行
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = HintColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Margin = new Padding(0, 6, 0, 0)
        };
    }

    private static Panel BuildTrackPanel(TrackBar track, Label value, int min, int max, int tick,
        Func<int, string> format)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        track.Minimum = min;
        track.Maximum = max;
        track.TickFrequency = tick;
        track.Dock = DockStyle.Fill;
        track.ValueChanged += (_, _) => value.Text = format(track.Value);

        value.AutoSize = false;
        value.Width = 52;
        value.Dock = DockStyle.Right;
        value.TextAlign = ContentAlignment.MiddleCenter;

        panel.Controls.Add(track);
        panel.Controls.Add(value);
        return panel;
    }

    // ---- 接口 ----
    private Panel BuildApiPage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "连接");

        _txtKey.UseSystemPasswordChar = true;
        AddRow(tlp, "API Key", _txtKey, 32);
        AddRow(tlp, "", Hint("只存在本机配置文件里，不会上传。"), 26);

        _txtBaseUrl.Text = "https://api.deepseek.com";
        AddRow(tlp, "接口地址", _txtBaseUrl, 32);
        AddRow(tlp, "", Hint("任何 OpenAI 兼容服务都可以，填到 /v1 之前。"), 26);

        AddSection(tlp, "模型");

        var models = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0)
        };

        _cmbOcr.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbOcr.Items.AddRange(new object[] { "deepseek-flash" });
        _cmbOcr.Width = 168;
        _cmbOcr.Margin = new Padding(0, 4, 0, 0);

        _cmbModel.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbModel.Items.AddRange(new object[] { "deepseek-flash", "deepseek-v4-pro" });
        _cmbModel.Width = 168;
        _cmbModel.Margin = new Padding(0, 4, 0, 0);

        models.Controls.Add(new Label { Text = "识别", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
        models.Controls.Add(_cmbOcr);
        models.Controls.Add(new Label { Text = "翻译", AutoSize = true, Margin = new Padding(20, 9, 8, 0) });
        models.Controls.Add(_cmbModel);

        AddRow(tlp, "模型", models, 34);
        AddRow(tlp, "", Hint("识别必须有图片能力（目前只有 flash）；翻译两个都能用，pro 更强更贵。"), 62);

        page.Controls.Add(tlp);
        return page;
    }

    // ---- 翻译 ----
    private Panel BuildTranslatePage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "引擎");

        _chkThinking.Text = "深度思考（thinking）";
        AddRow(tlp, "翻译", _chkThinking, 32);
        AddRow(tlp, "", Hint("质量提升不明显，但延迟约 3 倍、token 最多 27 倍。默认关闭。"), 44);

        _chkStream.Text = "流式输出（边生成边显示）";
        AddRow(tlp, "输出", _chkStream, 32);
        AddRow(tlp, "", Hint("分段不准时可能翻译不全，建议关闭。"), 26);

        AddSection(tlp, "操作");

        _cmbHotkey.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbHotkey.Items.AddRange(new object[] { "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Ctrl+F8", "Alt+F8", "Ctrl+Alt+F8" });
        AddRow(tlp, "翻译快捷键", _cmbHotkey, 32);
        AddRow(tlp, "", Hint("游戏在前台也能触发，冲突就换一个。"), 26);

        AddSection(tlp, "自动模式");

        _numAutoQuiet.Minimum = 300;
        _numAutoQuiet.Maximum = 5000;
        _numAutoQuiet.Increment = 100;
        AddRow(tlp, "安静多久才翻", _numAutoQuiet, 32);
        AddRow(tlp, "", Hint("毫秒。画面安静这么久才翻译；总提前翻就调大。"), 40);

        _numAutoCooldown.Minimum = 500;
        _numAutoCooldown.Maximum = 5000;
        _numAutoCooldown.Increment = 100;
        AddRow(tlp, "翻译后冷却", _numAutoCooldown, 32);
        AddRow(tlp, "", Hint("毫秒。两次自动翻译至少间隔这么久，防止连翻。"), 40);

        page.Controls.Add(tlp);
        return page;
    }

    // ---- 外观 ----
    // ---- 上下文 / 记忆 ----
    private Panel BuildContextPage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "默认值");

        var resetRow = new Panel { Dock = DockStyle.Fill };
        var btnReset = new Button { Text = "恢复默认", Width = 104, Dock = DockStyle.Right };
        btnReset.Click += (_, _) => ResetContextDefaults();
        resetRow.Controls.Add(new Label
        {
            Text = "999 / 200 / 60 / 1500",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            ForeColor = Color.FromArgb(120, 126, 138)
        });
        resetRow.Controls.Add(btnReset);
        AddRow(tlp, "默认组合", resetRow, 36);
        AddRow(tlp, "", Hint("依次是：内存保留 / 写入配置 / 总结几句 / 记忆上限。"), 40);

        AddSection(tlp, "上下文");

        _chkContext.Text = "把最近几句一起发给模型";
        AddRow(tlp, "启用", _chkContext, 32);

        _numContext.Minimum = 0;
        _numContext.Maximum = 999;
        AddRow(tlp, "发送条数", _numContext, 32);
        AddRow(tlp, "", Hint("每次翻译带上前几条对话；0 = 完全不带也不显示。"), 40);

        _chkCtxSource.Text = "译文窗的历史部分也显示日文原文";
        AddRow(tlp, "历史显示原文", _chkCtxSource, 32);

        _numHistMax.Minimum = 50;
        _numHistMax.Maximum = 5000;
        AddRow(tlp, "内存里保留", _numHistMax, 32);
        AddRow(tlp, "", Hint("超过就丢最早的；条数越多内存和小窗越吃紧。"), 40);

        _numHistSave.Minimum = 10;
        _numHistSave.Maximum = 2000;
        AddRow(tlp, "写入配置条数", _numHistSave, 32);
        AddRow(tlp, "", Hint("重启后能恢复的条数；写太多会让 config.json 变大。"), 40);

        _chkStrictCache.Text = "缓存严格匹配上下文";
        AddRow(tlp, "缓存匹配", _chkStrictCache, 32);
        AddRow(tlp, "", Hint("默认关：同一句不管上下文都复用缓存；开了命中率大降但译文一定对应当前语境。"), 56);

        AddSection(tlp, "记忆");

        _numSummaryCount.Minimum = 5;
        _numSummaryCount.Maximum = 500;
        AddRow(tlp, "总结取几句", _numSummaryCount, 32);
        AddRow(tlp, "", Hint("点「总结为记忆」时，拿最近多少条对话喂给模型。"), 40);

        _numMemoryMax.Minimum = 200;
        _numMemoryMax.Maximum = 20000;
        AddRow(tlp, "记忆字数上限", _numMemoryMax, 32);
        AddRow(tlp, "", Hint("超了丢最早的非星标记忆，★ 星标的永远不丢。"), 40);

        page.Controls.Add(tlp);
        return page;
    }

    private void ResetContextDefaults()
    {
        _numHistMax.Value = 999;
        _numHistSave.Value = 200;
        _numSummaryCount.Value = 60;
        _numMemoryMax.Value = 1500;
    }

    private Panel BuildAppearancePage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "界面");

        _chkDark.Text = "深色主题（黑底浅字）";
        AddRow(tlp, "主题", _chkDark, 32);
        AddRow(tlp, "", Hint("点确定后主界面立即变色。"), 26);

        AddSection(tlp, "译文窗");

        var outPanel = BuildTrackPanel(_trkOut, _lblOut, 0, 255, 25, v => v.ToString());
        AddRow(tlp, "背景不透明度", outPanel, 46);
        AddRow(tlp, "", Hint("0 = 全透明只剩文字，255 = 完全不透明。"), 26);

        AddSection(tlp, "捕获框");

        var capPanel = BuildTrackPanel(_trkCap, _lblCap, 0, 100, 10, v => v + "%");
        AddRow(tlp, "取景带浓度", capPanel, 46);
        AddRow(tlp, "", Hint("只调深浅，越低越不显眼。"), 26);

        _numBand.Minimum = 4; _numBand.Maximum = 30;
        AddRow(tlp, "边缘带宽度", _numBand, 32);

        _numLine.Minimum = 1; _numLine.Maximum = 6;
        AddRow(tlp, "边缘线粗细", _numLine, 32);

        _numPad.Minimum = 0; _numPad.Maximum = 60;
        AddRow(tlp, "靠近显示距离", _numPad, 32);
        AddRow(tlp, "", Hint("平时只留细线，光标靠近才展开取景带。"), 26);

        page.Controls.Add(tlp);
        return page;
    }

    // ---- 维护 ----
    // ---- 关于 ----
    private Panel BuildAboutPage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "关于本程序");

        var ver = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        AddRow(tlp, "版本", new Label
        {
            Text = "Kotoba  v" + ver + "    DPI = " + DeviceDpi,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 66, 76)
        }, 32);

        var repoLink = new LinkLabel
        {
            Text = "github.com/SakurabeEma-333/Kotoba",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        repoLink.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/SakurabeEma-333/Kotoba")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        };
        AddRow(tlp, "项目主页", repoLink, 32);


        AddSection(tlp, "帮助");

        var help = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };

        AddHelpItem(help, "捕获框", "拖顶栏移动，四角/左右边拖拽改大小；鼠标靠近才显示取景带。");
        AddHelpItem(help, "按钮", "翻译 / 重翻 / 清空 / 自动 / 缩 / 关闭 / 锁定。重翻会绕过缓存重来一次。");
        AddHelpItem(help, "译文窗", "可拖动、可拖角缩放，内容多了用滚轮或拖右侧滚动条。");
        AddHelpItem(help, "热键", "默认 Ctrl+F8 翻译，可在「翻译」页改。");
        AddHelpItem(help, "自动模式", "画面安静 0.8 秒后自动翻译，不用手点。");
        AddHelpItem(help, "小球", "点「缩」把两个窗收成一个圆球，点球还原，会话不断。");
        AddHelpItem(help, "上下文", "最近几句对话一起发给模型；右键配置→查看上下文可看/导出。");
        AddHelpItem(help, "记忆", "长期设定（人物、世界观）；带星标的不会被自动淘汰。");
        AddHelpItem(help, "术语表", "写成「原文=译文」，翻译时必须按此译法。");
        AddHelpItem(help, "预设与配置", "每个游戏一套配置（提示词/模型/区域/术语），可复制。");
        AddHelpItem(help, "缓存", "同一画面重复翻直接复用；可在「维护」里清空。");

        AddSpanRow(tlp, help);

        page.Controls.Add(tlp);
        return page;
    }

    /// <summary>跨两列的一行（用于放可折叠列表这类需要整行宽度的控件）</summary>
    private static void AddSpanRow(TableLayoutPanel tlp, Control c)
    {
        int row = tlp.RowCount;
        tlp.RowCount = row + 1;
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Top;
        tlp.Controls.Add(c, 0, row);
        tlp.SetColumnSpan(c, 2);
    }

    /// <summary>可折叠的帮助条目：点标题展开/收起说明</summary>
    private static void AddHelpItem(FlowLayoutPanel host, string title, string desc)
    {
        var head = new Label
        {
            Text = "▸  " + title,
            AutoSize = false,
            Width = 560,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 58, 68),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 0, 0)
        };

        var body = new Label
        {
            Text = desc,
            AutoSize = false,
            Width = 560,
            Height = 34,
            Visible = false,
            Padding = new Padding(18, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(96, 102, 112),
            Margin = new Padding(0, 0, 0, 4)
        };

        head.Click += (_, _) =>
        {
            body.Visible = !body.Visible;
            head.Text = (body.Visible ? "▾  " : "▸  ") + title;
        };

        host.Controls.Add(head);
        host.Controls.Add(body);
    }

    private Panel BuildMaintainPage()
    {
        var page = BuildPage();
        var tlp = BuildGrid();

        AddSection(tlp, "翻译缓存");

        var cacheRow = new Panel { Dock = DockStyle.Fill };
        var btnClear = new Button { Text = "清空缓存", Width = 104, Dock = DockStyle.Right };
        btnClear.Click += (_, _) =>
        {
            var r = MessageBox.Show(this,
                "清空后所有翻过的画面都要重新调用接口（会重新产生费用）。确定？",
                "清空翻译缓存", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
            _pipeline?.ClearCacheAndPersist();
            RefreshCacheLabel();
        };
        _lblCache.Dock = DockStyle.Fill;
        _lblCache.TextAlign = ContentAlignment.MiddleLeft;
        _lblCache.ForeColor = Color.FromArgb(52, 58, 66);
        cacheRow.Controls.Add(_lblCache);
        cacheRow.Controls.Add(btnClear);
        AddRow(tlp, "已缓存译文", cacheRow, 36);
        AddRow(tlp, "", Hint("同一画面再次翻译时直接读缓存，不调用接口也不花钱。"), 40);

        AddSection(tlp, "用量");

        AddRow(tlp, "统计", new Label
        {
            Text = _usage,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            ForeColor = Color.FromArgb(52, 58, 66)
        }, 32);

        AddSection(tlp, "窗口位置");

        var resetRow = new Panel { Dock = DockStyle.Fill };
        var btnReset = new Button { Text = "重置位置", Width = 104, Dock = DockStyle.Right };
        btnReset.Click += (_, _) =>
        {
            _resetWindows?.Invoke();
            MessageBox.Show(this, "捕获框和译文窗已回到默认位置。", "重置位置",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        resetRow.Controls.Add(new Label
        {
            Text = "回到屏幕中下部 / 中上部的默认比例",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            ForeColor = Color.FromArgb(52, 58, 66)
        });
        resetRow.Controls.Add(btnReset);
        AddRow(tlp, "布局", resetRow, 36);
        AddRow(tlp, "", Hint("立即生效，会话中也能用。"), 26);

        page.Controls.Add(tlp);
        return page;
    }

    // ---------------- 数据 ----------------

    private void RefreshCacheLabel()
    {
        int n = 0;
        try { n = _pipeline?.CacheCount ?? 0; } catch { }
        _lblCache.Text = n > 0 ? n + " 条" : "暂无缓存";
    }

    private void LoadValues()
    {
        _txtKey.Text = _cfg.ApiKey;
        _txtBaseUrl.Text = string.IsNullOrWhiteSpace(_cfg.BaseUrl) ? "https://api.deepseek.com" : _cfg.BaseUrl;
        _cmbOcr.Text = string.IsNullOrWhiteSpace(_cfg.OcrModel) ? "deepseek-flash" : _cfg.OcrModel;
        _cmbModel.Text = _profile.Model;

        _chkThinking.Checked = _cfg.ThinkingEnabled;
        _chkStream.Checked = _cfg.Streaming;
        _chkContext.Checked = _cfg.ContextEnabled;
        _numContext.Value = Math.Clamp(_cfg.ContextCount, 0, 999);
        _chkCtxSource.Checked = _cfg.ContextShowSource;
        _numHistMax.Value = Math.Clamp(_cfg.HistoryMaxEntries, 50, 5000);
        _numHistSave.Value = Math.Clamp(_cfg.HistorySaveCount, 10, 2000);
        _numSummaryCount.Value = Math.Clamp(_cfg.SummaryHistoryCount, 5, 500);
        _numMemoryMax.Value = Math.Clamp(_cfg.MemoryMaxChars, 200, 20000);
        _numAutoQuiet.Value = Math.Clamp(_profile.AutoQuietMs, 300, 5000);
        _numAutoCooldown.Value = Math.Clamp(_profile.AutoStableMs, 500, 5000);
        _chkStrictCache.Checked = _cfg.StrictContextCache;
        _cmbHotkey.SelectedItem = _cfg.Hotkey;
        if (_cmbHotkey.SelectedIndex < 0) _cmbHotkey.Text = _cfg.Hotkey;

        _chkDark.Checked = _cfg.DarkTheme;
        _trkOut.Value = Math.Clamp(_profile.BgOpacity, 0, 255);
        _lblOut.Text = _trkOut.Value.ToString();
        _trkCap.Value = Math.Clamp(_profile.CaptureOpacity, 0, 100);
        _lblCap.Text = _trkCap.Value + "%";
        _numBand.Value = Math.Clamp(_profile.CaptureBandWidth, 4, 30);
        _numLine.Value = Math.Clamp(_profile.CaptureLineWidth, 1, 6);
        _numPad.Value = Math.Clamp(_profile.CaptureRevealPad, 0, 60);

        RefreshCacheLabel();
    }

    private void Apply()
    {
        _cfg.ApiKey = _txtKey.Text.Trim();

        var url = _txtBaseUrl.Text.Trim();
        if (!string.IsNullOrWhiteSpace(url)) _cfg.BaseUrl = url.TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(_cmbOcr.Text)) _cfg.OcrModel = _cmbOcr.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_cmbModel.Text)) _profile.Model = _cmbModel.Text.Trim();

        _cfg.ThinkingEnabled = _chkThinking.Checked;
        _cfg.Streaming = _chkStream.Checked;
        _cfg.ContextEnabled = _chkContext.Checked;
        _cfg.ContextCount = (int)_numContext.Value;
        _cfg.ContextShowSource = _chkCtxSource.Checked;
        _cfg.HistoryMaxEntries = (int)_numHistMax.Value;
        _cfg.HistorySaveCount = (int)_numHistSave.Value;
        _cfg.SummaryHistoryCount = (int)_numSummaryCount.Value;
        _cfg.MemoryMaxChars = (int)_numMemoryMax.Value;
        _profile.AutoQuietMs = (int)_numAutoQuiet.Value;
        _profile.AutoStableMs = (int)_numAutoCooldown.Value;
        _cfg.StrictContextCache = _chkStrictCache.Checked;
        if (_cmbHotkey.SelectedItem != null) _cfg.Hotkey = _cmbHotkey.SelectedItem.ToString()!;

        _cfg.DarkTheme = _chkDark.Checked;
        Theme.Dark = _cfg.DarkTheme;

        _profile.BgOpacity = _trkOut.Value;
        _profile.CaptureOpacity = _trkCap.Value;
        _profile.CaptureBandWidth = (int)_numBand.Value;
        _profile.CaptureLineWidth = (int)_numLine.Value;
        _profile.CaptureRevealPad = (int)_numPad.Value;
    }
}