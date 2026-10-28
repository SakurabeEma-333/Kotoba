namespace Kotoba.Forms;

/// <summary>通用单行输入对话框（用于重命名配置）</summary>
public class InputForm : Form
{
    private readonly TextBox _txt = new();

    public string Value => _txt.Text.Trim();

    public InputForm(string title, string label, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 170);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.None;

        var lbl = new Label
        {
            Text = label,
            Bounds = new Rectangle(20, 18, 480, 26),
            AutoSize = false
        };

        _txt.Bounds = new Rectangle(20, 50, 480, 32);
        _txt.Text = value;
        _txt.SelectAll();

        var ok = new Button { Text = "确定", Bounds = new Rectangle(300, 108, 92, 36), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Bounds = new Rectangle(404, 108, 92, 36), DialogResult = DialogResult.Cancel };

        Controls.Add(lbl);
        Controls.Add(_txt);
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>
/// 记忆：分条保存的长期记忆。每次总结会新增一条（记忆1、记忆2…），
/// 每条可单独编辑/删除。翻译时全部拼接到系统提示词后面。
/// </summary>
public class MemoryForm : Form
{
    private const string Separator = "\n---\n";

    private readonly TextBox _txtInput = new();
    private readonly Button _btnSave = new();
    private readonly ListView _list = new();
    private readonly Label _lblCount = new();
    private List<string> _items = new();

    public MemoryForm(string profileName, string memory)
    {
        Text = "记忆 — " + profileName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(800, 700);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.None;

        var tip = new Label
        {
            Text = "每条记忆是独立的一块（世界观、人物、事件）。可以在上框写新的记忆点「保存」，\n" +
                   "也可以在列表里选中某条修改；选中后点「★ 星标」，这条就永远不会被自动丢弃。",
            Bounds = new Rectangle(20, 12, 760, 48),
            AutoSize = false,
            ForeColor = Color.FromArgb(70, 75, 85)
        };

        // ---- 输入 ----
        _txtInput.Multiline = true;
        _txtInput.ScrollBars = ScrollBars.Vertical;
        _txtInput.AcceptsReturn = true;
        _txtInput.Bounds = new Rectangle(20, 66, 610, 150);
        _txtInput.Font = new Font("Microsoft YaHei UI", 10F);

        _btnSave.Text = "保存为记忆";
        _btnSave.Bounds = new Rectangle(650, 66, 130, 150);
        _btnSave.Click += (_, _) => SaveEntry();

        // ---- 列表 ----
        _list.Bounds = new Rectangle(20, 230, 760, 360);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("序号", 70);
        _list.Columns.Add("星标", 50);
        _list.Columns.Add("内容", 620);
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        _list.DoubleClick += (_, _) => LoadSelected();

        _lblCount.Bounds = new Rectangle(20, 598, 400, 24);
        _lblCount.AutoSize = false;
        _lblCount.ForeColor = Color.FromArgb(120, 126, 138);

        // ---- 按钮 ----
        var btnDelete = new Button { Text = "删除选中", Bounds = new Rectangle(20, 626, 120, 38) };
        btnDelete.Click += (_, _) => DeleteSelected();

        var btnStar = new Button { Text = "★ 星标", Bounds = new Rectangle(152, 626, 90, 38) };
        btnStar.Click += (_, _) => ToggleStar();

        var btnClear = new Button { Text = "清空全部", Bounds = new Rectangle(250, 626, 120, 38) };
        btnClear.Click += (_, _) =>
        {
            if (_items.Count == 0) return;
            if (MessageBox.Show(this, "确定清空全部记忆？", "记忆",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _items.Clear();
            RefreshList();
            // 立即通知外面保存：否则用户清完直接点 × 关窗，清空就白做了
            Cleared?.Invoke(this, EventArgs.Empty);
        };

        var btnOk = new Button { Text = "确定", Bounds = new Rectangle(570, 626, 100, 38), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Bounds = new Rectangle(682, 626, 98, 38), DialogResult = DialogResult.Cancel };

        Controls.Add(tip);
        Controls.Add(_txtInput);
        Controls.Add(_btnSave);
        Controls.Add(_list);
        Controls.Add(_lblCount);
        Controls.Add(btnDelete);
        Controls.Add(btnStar);
        Controls.Add(btnClear);
        Controls.Add(btnOk);
        Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        // 解析已有记忆
        _items = (memory ?? "")
            .Replace("\r\n", "\n")
            .Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        for (int i = 0; i < _items.Count; i++)
        {
            bool starred = _items[i].StartsWith("★");
            var body = starred ? _items[i].TrimStart('★').Trim() : _items[i];
            var preview = body.Replace("\r", "").Replace("\n", " ");
            if (preview.Length > 90) preview = preview[..90] + "…";

            var item = new ListViewItem(new[] { $"记忆{i + 1}", starred ? "★" : "", preview });
            item.Tag = i;
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _lblCount.Text = $"共 {_items.Count} 条记忆";
    }

    private void SaveEntry()
    {
        var text = _txtInput.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "请先写一点内容。", "记忆", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 有选中项 → 覆盖该条；否则新增
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is int idx && idx >= 0 && idx < _items.Count)
        {
            // 编辑正文时保留原来的星标状态
            _items[idx] = _items[idx].StartsWith("★") ? "★ " + text : text;
        }
        else
        {
            _items.Add(text);
        }

        _txtInput.Clear();
        _list.SelectedItems.Clear();
        RefreshList();
        _txtInput.Focus();
    }

    private void LoadSelected()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "请先在列表里选中一条记忆。", "Kotoba"); return; }
        if (_list.SelectedItems[0].Tag is not int idx) return;
        if (idx < 0 || idx >= _items.Count) return;

        _txtInput.Text = _items[idx].TrimStart('★').Trim();
        _txtInput.Focus();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "请先在列表里选中一条记忆。", "Kotoba"); return; }
        if (_list.SelectedItems[0].Tag is not int idx) return;
        if (idx < 0 || idx >= _items.Count) return;

        _items.RemoveAt(idx);
        _txtInput.Clear();
        RefreshList();
        _lblCount.Text = $"已删除，共 {_items.Count} 条记忆";
    }

    /// <summary>星标段永远不会被自动丢弃（早期星标会自动顺位到下一条非星标）</summary>
    private void ToggleStar()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "请先在列表里选中一条记忆。", "Kotoba"); return; }
        if (_list.SelectedItems[0].Tag is not int idx) return;
        if (idx < 0 || idx >= _items.Count) return;

        bool nowStarred = !_items[idx].StartsWith("★");
        _items[idx] = nowStarred ? "★ " + _items[idx].TrimStart('★').Trim() : _items[idx].TrimStart('★').Trim();

        RefreshList();
        _lblCount.Text = nowStarred ? "已加星标：这条不会被自动丢弃" : "已取消星标";
    }

    /// <summary>确定后调用：序列化回带分隔符的字符串</summary>
    public string Memory => string.Join(Separator, _items);

    /// <summary>点「清空全部」时触发，让调用方立即落盘</summary>
    public event EventHandler? Cleared;
}
