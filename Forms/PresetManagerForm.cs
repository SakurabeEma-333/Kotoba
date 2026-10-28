using Kotoba.Models;
using Kotoba.Services;

namespace Kotoba.Forms;

/// <summary>管理提示词：左侧分类，右侧该分类下的预设</summary>
public class PresetManagerForm : Form
{
    private readonly AppConfig _cfg;

    private readonly ListBox _lstCategories = new();
    private readonly ListView _lstPresets = new();
    private readonly Label _lblInfo = new();

    private const string SystemCategoryName = "系统（内置）";

    /// <summary>内置分类名（来自 PromptPresets；「特殊」由设置里的开关控制是否显示）</summary>
    private List<string> BuiltInCats =>
        PromptPresets.Visible().Select(x => x.Category).Distinct().ToList();

    public PresetManagerForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "管理提示词";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(960, 690);
        BackColor = Theme.FormBack;
        ForeColor = Theme.TextFore;

        BuildUi();
        RefreshCategories();

        Theme.Apply(this);
    }

    private void BuildUi()
    {
        // 左侧：分类
        _lstCategories.Bounds = new Rectangle(16, 16, 240, 500);
        _lstCategories.DrawMode = DrawMode.OwnerDrawFixed;
        _lstCategories.ItemHeight = 36;
        _lstCategories.BorderStyle = BorderStyle.FixedSingle;
        _lstCategories.SelectedIndexChanged += (_, _) => RefreshPresets();
        _lstCategories.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(0, 120, 215) : _lstCategories.BackColor))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using var fg = new SolidBrush(sel ? Color.White : Theme.TextFore);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            var r = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            e.Graphics.DrawString(_lstCategories.Items[e.Index]?.ToString() ?? "", _lstCategories.Font, fg, r, sf);
        };
        Controls.Add(_lstCategories);

        var catBtns = new FlowLayoutPanel
        {
            Bounds = new Rectangle(16, 524, 240, 116),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var btnNewCat = new Button { Text = "新建分类", Width = 240, Height = 28 };
        btnNewCat.Click += (_, _) => NewCategory();
        var btnRenameCat = new Button { Text = "重命名分类", Width = 240, Height = 28 };
        btnRenameCat.Click += (_, _) => RenameCategory();
        var btnDelCat = new Button { Text = "删除分类", Width = 240, Height = 28 };
        btnDelCat.Click += (_, _) => DeleteCategory();
        catBtns.Controls.Add(btnNewCat);
        catBtns.Controls.Add(btnRenameCat);
        catBtns.Controls.Add(btnDelCat);
        Controls.Add(catBtns);

        // 右侧：预设列表
        _lstPresets.Bounds = new Rectangle(272, 16, 672, 500);
        _lstPresets.View = View.Details;
        _lstPresets.FullRowSelect = true;
        _lstPresets.GridLines = true;
        _lstPresets.HideSelection = false;
        _lstPresets.Columns.Add("名称", 220);
        _lstPresets.Columns.Add("内容摘要", 430);
        _lstPresets.DoubleClick += (_, _) => EditSelected();
        Controls.Add(_lstPresets);

        var preBtns = new FlowLayoutPanel
        {
            Bounds = new Rectangle(272, 526, 672, 40),
            FlowDirection = FlowDirection.LeftToRight
        };
        var btnAdd = new Button { Text = "新增", Width = 100, Height = 32 };
        btnAdd.Click += (_, _) => AddPreset();
        var btnEdit = new Button { Text = "编辑", Width = 100, Height = 32 };
        btnEdit.Click += (_, _) => EditSelected();
        var btnMove = new Button { Text = "移动到…", Width = 110, Height = 32 };
        btnMove.Click += (_, _) => MoveSelected();
        var btnDel = new Button { Text = "删除", Width = 100, Height = 32 };
        btnDel.Click += (_, _) => DeleteSelected();
        preBtns.Controls.Add(btnAdd);
        preBtns.Controls.Add(btnEdit);
        preBtns.Controls.Add(btnMove);
        preBtns.Controls.Add(btnDel);
        Controls.Add(preBtns);

        _lblInfo.Bounds = new Rectangle(272, 636, 500, 26);
        _lblInfo.ForeColor = Color.FromArgb(120, 126, 138);
        _lblInfo.AutoSize = false;
        Controls.Add(_lblInfo);

        var btnClose = new Button
        {
            Text = "关闭",
            Bounds = new Rectangle(848, 630, 96, 38),
            DialogResult = DialogResult.OK
        };
        Controls.Add(btnClose);
        AcceptButton = btnClose;
    }

    // ---------------- 分类 ----------------

    private void RefreshCategories()
    {
        int keep = _lstCategories.SelectedIndex;
        _lstCategories.Items.Clear();
        foreach (var name in BuiltInCats) _lstCategories.Items.Add(name);
        foreach (var c in _cfg.Categories.Where(c => !c.BuiltIn))
            _lstCategories.Items.Add(c.Name);

        if (keep >= 0 && keep < _lstCategories.Items.Count) _lstCategories.SelectedIndex = keep;
        else if (_lstCategories.Items.Count > 0) _lstCategories.SelectedIndex = 0;
    }

    private PresetCategory? SelectedCategory
    {
        get
        {
            int i = _lstCategories.SelectedIndex;
            if (i < BuiltInCats.Count) return null;                 // 内置分类
            var user = _cfg.Categories.Where(c => !c.BuiltIn).ToList();
            int ui = i - BuiltInCats.Count;
            return ui >= 0 && ui < user.Count ? user[ui] : null;
        }
    }

    private void NewCategory()
    {
        using var dlg = new InputForm("新建分类", "分类名称：", "");
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.Value)) return;

        if (_cfg.Categories.Any(c => c.Name == dlg.Value) || BuiltInCats.Contains(dlg.Value))
        {
            MessageBox.Show(this, "分类名已存在。", "Kotoba");
            return;
        }

        _cfg.Categories.Add(new PresetCategory { Name = dlg.Value });
        SaveAndRefresh();
    }

    private void RenameCategory()
    {
        var cat = SelectedCategory;
        if (cat == null) { MessageBox.Show(this, "系统分类不能重命名。", "Kotoba"); return; }

        using var dlg = new InputForm("重命名分类", "新名称：", cat.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.Value)) return;

        cat.Name = dlg.Value;
        SaveAndRefresh();
    }

    private void DeleteCategory()
    {
        var cat = SelectedCategory;
        if (cat == null) { MessageBox.Show(this, "系统分类不能删除。", "Kotoba"); return; }

        var ans = MessageBox.Show(this,
            $"确定删除分类「{cat.Name}」吗？里面的 {cat.Presets.Count} 个预设也会一起删除。",
            "Kotoba", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ans != DialogResult.Yes) return;

        _cfg.Categories.Remove(cat);
        SaveAndRefresh();
    }

    // ---------------- 预设 ----------------

    private void RefreshPresets()
    {
        _lstPresets.Items.Clear();

        var cat = SelectedCategory;
        if (cat == null)
        {
            // 系统分类：只读显示内置
            int bi = _lstCategories.SelectedIndex;
            var biName = bi >= 0 && bi < BuiltInCats.Count ? BuiltInCats[bi] : null;
            foreach (var (_, name, content) in PromptPresets.Visible().Where(x => x.Category == biName))
            {
                var item = new ListViewItem(name);
                item.SubItems.Add(Brief(content));
                item.Tag = "system";
                item.ForeColor = Color.FromArgb(130, 136, 148);
                _lstPresets.Items.Add(item);
            }
            _lblInfo.Text = "内置提示词，不可修改或删除。";
            return;
        }

        foreach (var p in cat.Presets)
        {
            var item = new ListViewItem(p.Name);
            item.SubItems.Add(Brief(p.Content));
            item.Tag = p;
            _lstPresets.Items.Add(item);
        }
        _lblInfo.Text = $"「{cat.Name}」共 {cat.Presets.Count} 条预设。双击可编辑。";
    }

    private static string Brief(string s)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 60 ? one : one[..60] + "…";
    }

    private void AddPreset()
    {
        var cat = SelectedCategory;
        if (cat == null) { MessageBox.Show(this, "系统分类下不能新增。", "Kotoba"); return; }

        using var dlg = new PresetEditForm(_cfg, cat);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var target = dlg.TargetCategory ?? cat;
        var exist = target.Presets.FirstOrDefault(p => p.Name == dlg.PresetName);
        if (exist != null) exist.Content = dlg.PresetContent;
        else target.Presets.Add(new PromptPreset { Name = dlg.PresetName, Content = dlg.PresetContent });

        SaveAndRefresh();
    }

    private void EditSelected()
    {
        if (_lstPresets.SelectedItems.Count == 0) return;
        if (_lstPresets.SelectedItems[0].Tag is not PromptPreset p) return;

        using var dlg = new PresetEditForm(_cfg, SelectedCategory, p.Name, p.Content);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var target = dlg.TargetCategory ?? SelectedCategory;
        if (target == null) return;

        // 从原分类移除
        SelectedCategory?.Presets.Remove(p);
        // 同名覆盖或新增到目标分类
        var exist = target.Presets.FirstOrDefault(x => x.Name == dlg.PresetName);
        if (exist != null) exist.Content = dlg.PresetContent;
        else target.Presets.Add(new PromptPreset { Name = dlg.PresetName, Content = dlg.PresetContent });

        SaveAndRefresh();
    }

    private void MoveSelected()
    {
        if (_lstPresets.SelectedItems.Count == 0) return;
        if (_lstPresets.SelectedItems[0].Tag is not PromptPreset p) return;

        var targets = _cfg.Categories.Where(c => !c.BuiltIn && c != SelectedCategory).ToList();
        if (targets.Count == 0) { MessageBox.Show(this, "没有其他分类可移动。", "Kotoba"); return; }

        using var dlg = new CategoryPickForm(targets);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected == null) return;

        SelectedCategory?.Presets.Remove(p);
        dlg.Selected.Presets.Add(p);
        SaveAndRefresh();
    }

    private void DeleteSelected()
    {
        if (_lstPresets.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "请先在右侧选中一条预设。", "Kotoba");
            return;
        }
        if (_lstPresets.SelectedItems[0].Tag is not PromptPreset p)
        {
            MessageBox.Show(this, "系统内置提示词不能删除。", "Kotoba");
            return;
        }

        var ans = MessageBox.Show(this, $"确定删除「{p.Name}」吗？", "Kotoba",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ans != DialogResult.Yes) return;

        SelectedCategory?.Presets.Remove(p);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        ConfigStore.Save(_cfg);
        RefreshCategories();
        RefreshPresets();
    }
}

/// <summary>选择要移动到的分类</summary>
public class CategoryPickForm : Form
{
    private readonly ListBox _list = new();
    public PresetCategory? Selected { get; private set; }

    public CategoryPickForm(List<PresetCategory> categories)
    {
        Text = "移动到哪个分类";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 320);
        BackColor = Theme.FormBack;

        _list.Bounds = new Rectangle(20, 20, 320, 210);
        foreach (var c in categories) _list.Items.Add(c.Name);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        Controls.Add(_list);

        var btnOk = new Button
        {
            Text = "确定",
            Bounds = new Rectangle(120, 250, 100, 38),
            DialogResult = DialogResult.OK
        };
        btnOk.Click += (_, _) =>
        {
            if (_list.SelectedIndex >= 0) Selected = categories[_list.SelectedIndex];
        };
        var btnCancel = new Button
        {
            Text = "取消",
            Bounds = new Rectangle(232, 250, 96, 38),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Theme.Apply(this);
    }
}
