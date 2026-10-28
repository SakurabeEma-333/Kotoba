using Kotoba.Models;
using Kotoba.Services;

namespace Kotoba.Forms;

/// <summary>新增或编辑一条提示词预设</summary>
public class PresetEditForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _txtName = new();
    private readonly ComboBox _cmbCategory = new();
    private readonly TextBox _txtContent = new();

    public string PresetName => _txtName.Text.Trim();
    public string PresetContent => _txtContent.Text.Trim();
    public PresetCategory? TargetCategory =>
        _cmbCategory.SelectedItem as PresetCategory;

    public PresetEditForm(AppConfig cfg, PresetCategory? defaultCategory = null,
                          string? initialName = null, string? initialContent = null)
    {
        _cfg = cfg;

        Text = string.IsNullOrEmpty(initialName) ? "新增预设" : "编辑预设";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 620);
        BackColor = Theme.FormBack;
        ForeColor = Theme.TextFore;

        // 名称
        Controls.Add(new Label
        {
            Text = "名称",
            Bounds = new Rectangle(24, 24, 80, 26),
            ForeColor = Theme.TextFore
        });
        _txtName.Bounds = new Rectangle(110, 22, 560, 30);
        _txtName.Font = new Font("Microsoft YaHei UI", 10F);
        _txtName.Text = initialName ?? "";
        Controls.Add(_txtName);

        // 分类
        Controls.Add(new Label
        {
            Text = "分类",
            Bounds = new Rectangle(24, 66, 80, 26),
            ForeColor = Theme.TextFore
        });
        _cmbCategory.Bounds = new Rectangle(110, 64, 300, 30);
        _cmbCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var c in _cfg.Categories.Where(c => !c.BuiltIn))
            _cmbCategory.Items.Add(c);
        if (defaultCategory != null)
            _cmbCategory.SelectedItem = defaultCategory;
        else if (_cmbCategory.Items.Count > 0)
            _cmbCategory.SelectedIndex = 0;
        Controls.Add(_cmbCategory);

        // 内容
        Controls.Add(new Label
        {
            Text = "提示词内容",
            Bounds = new Rectangle(24, 108, 100, 26),
            ForeColor = Theme.TextFore
        });
        _txtContent.Multiline = true;
        _txtContent.ScrollBars = ScrollBars.Vertical;
        _txtContent.AcceptsReturn = true;
        _txtContent.Bounds = new Rectangle(24, 138, 672, 400);
        _txtContent.Font = new Font("Microsoft YaHei UI", 10F);
        _txtContent.Text = initialContent ?? Profile.DefaultPrompt;
        Controls.Add(_txtContent);

        // 按钮
        var btnOk = new Button
        {
            Text = "确定",
            Bounds = new Rectangle(490, 556, 100, 38),
            DialogResult = DialogResult.OK
        };
        var btnCancel = new Button
        {
            Text = "取消",
            Bounds = new Rectangle(600, 556, 96, 38),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Theme.Apply(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                MessageBox.Show(this, "请填写预设名称。", "Kotoba");
                e.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(_txtContent.Text))
            {
                MessageBox.Show(this, "提示词内容不能为空。", "Kotoba");
                e.Cancel = true;
                return;
            }
            if (_cmbCategory.SelectedItem == null)
            {
                MessageBox.Show(this, "请选择一个分类（可在「管理提示词」里新建分类）。", "Kotoba");
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }
}
