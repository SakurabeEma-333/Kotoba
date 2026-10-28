using Kotoba.Models;

namespace Kotoba.Forms;

/// <summary>
/// 术语表：上方输入原文+译文并保存，下方列表展示全部术语，可选中修改或删除。
/// </summary>
public class GlossaryForm : Form
{
    private readonly Profile _profile;

    private readonly TextBox _txtSrc = new();
    private readonly TextBox _txtDst = new();
    private readonly Button _btnSaveTerm = new();
    private readonly ListView _list = new();
    private readonly Label _lblCount = new();

    public GlossaryForm(Profile profile)
    {
        _profile = profile;

        Text = "术语表 — " + profile.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 620);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.None;

        var tip = new Label
        {
            Text = "翻译时这些术语会强制作为对照表附在提示词后面，保证人名、技能名译法统一。",
            Bounds = new Rectangle(20, 12, 720, 24),
            AutoSize = false,
            ForeColor = Color.FromArgb(70, 75, 85)
        };

        // ---- 输入区 ----
        var lblSrc = new Label { Text = "原文", Bounds = new Rectangle(20, 48, 62, 26), AutoSize = false };
        _txtSrc.Bounds = new Rectangle(86, 46, 242, 30);
        _txtSrc.Font = new Font("Microsoft YaHei UI", 10F);

        var lblDst = new Label { Text = "译文", Bounds = new Rectangle(344, 48, 62, 26), AutoSize = false };
        _txtDst.Bounds = new Rectangle(410, 46, 224, 30);
        _txtDst.Font = new Font("Microsoft YaHei UI", 10F);

        _btnSaveTerm.Text = "保存";
        _btnSaveTerm.Bounds = new Rectangle(650, 45, 90, 32);
        _btnSaveTerm.Click += (_, _) => SaveTerm();

        // ---- 列表 ----
        _list.Bounds = new Rectangle(20, 92, 720, 440);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("原文", 360);
        _list.Columns.Add("译文", 330);
        _list.SelectedIndexChanged += (_, _) => LoadSelectedIntoInputs();
        _list.DoubleClick += (_, _) => LoadSelectedIntoInputs();

        _lblCount.Bounds = new Rectangle(20, 540, 400, 24);
        _lblCount.AutoSize = false;
        _lblCount.ForeColor = Color.FromArgb(120, 126, 138);

        // ---- 按钮 ----
        var btnDelete = new Button { Text = "删除选中", Bounds = new Rectangle(20, 566, 120, 36) };
        btnDelete.Click += (_, _) => DeleteSelected();

        var btnClear = new Button { Text = "清空全部", Bounds = new Rectangle(152, 566, 120, 36) };
        btnClear.Click += (_, _) =>
        {
            if (_list.Items.Count == 0) return;
            if (MessageBox.Show(this, "确定清空全部术语？", "术语表",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _list.Items.Clear();
            UpdateCount();
        };

        var btnOk = new Button { Text = "确定", Bounds = new Rectangle(530, 566, 100, 36), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Bounds = new Rectangle(642, 566, 100, 36), DialogResult = DialogResult.Cancel };

        Controls.Add(tip);
        Controls.Add(lblSrc);
        Controls.Add(_txtSrc);
        Controls.Add(lblDst);
        Controls.Add(_txtDst);
        Controls.Add(_btnSaveTerm);
        Controls.Add(_list);
        Controls.Add(_lblCount);
        Controls.Add(btnDelete);
        Controls.Add(btnClear);
        Controls.Add(btnOk);
        Controls.Add(btnCancel);

        AcceptButton = _btnSaveTerm;
        CancelButton = btnCancel;

        LoadFromProfile();
    }

    // ---------------- 数据 ----------------

    private void LoadFromProfile()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var line in (_profile.Glossary ?? "").Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;

            int i = t.IndexOf('=');
            if (i <= 0) continue;

            var src = t[..i].Trim();
            var dst = t[(i + 1)..].Trim();
            if (src.Length == 0) continue;

            _list.Items.Add(new ListViewItem(new[] { src, dst }));
        }

        _list.EndUpdate();
        UpdateCount();
    }

    private void SaveTerm()
    {
        var src = _txtSrc.Text.Trim();
        var dst = _txtDst.Text.Trim();

        if (src.Length == 0)
        {
            MessageBox.Show(this, "请填写原文。", "术语表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtSrc.Focus();
            return;
        }

        // 已存在同原文就更新，否则新增
        foreach (ListViewItem item in _list.Items)
        {
            if (string.Equals(item.Text, src, StringComparison.Ordinal))
            {
                item.SubItems[1].Text = dst;
                FinishSave();
                return;
            }
        }

        _list.Items.Add(new ListViewItem(new[] { src, dst }));
        FinishSave();
    }

    private void FinishSave()
    {
        _txtSrc.Clear();
        _txtDst.Clear();
        _txtSrc.Focus();
        UpdateCount();
    }

    private void LoadSelectedIntoInputs()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "请先在列表里选中一条术语。", "Kotoba"); return; }
        var item = _list.SelectedItems[0];
        _txtSrc.Text = item.Text;
        _txtDst.Text = item.SubItems[1].Text;
        _txtSrc.Focus();
        _txtSrc.SelectAll();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "请先在列表里选中一条术语。", "Kotoba"); return; }
        _list.Items.Remove(_list.SelectedItems[0]);
        UpdateCount();
    }

    private void UpdateCount()
    {
        _lblCount.Text = $"共 {_list.Items.Count} 条术语";
    }

    /// <summary>确定后调用：把列表序列化回「原文=译文」多行文本</summary>
    public string ResultGlossary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (ListViewItem item in _list.Items)
            {
                var src = item.Text.Trim();
                if (src.Length == 0) continue;
                sb.AppendLine(src + "=" + item.SubItems[1].Text.Trim());
            }
            return sb.ToString().TrimEnd();
        }
    }
}
