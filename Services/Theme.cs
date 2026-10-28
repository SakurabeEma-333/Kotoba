namespace Kotoba.Services;

/// <summary>
/// 全局主题。深色主题只作用于"标准窗口"（主界面、设置、术语表等），
/// 捕获框和译文窗本来就是深色，不参与。
/// </summary>
public static class Theme
{
    public static bool Dark { get; set; }

    private static readonly Color LForm = Color.FromArgb(240, 242, 245);
    private static readonly Color LPanel = Color.White;
    private static readonly Color LText = Color.FromArgb(32, 34, 38);
    private static readonly Color LInput = Color.White;
    private static readonly Color LButton = Color.FromArgb(250, 250, 252);
    private static readonly Color LBorder = Color.FromArgb(208, 213, 221);

    private static readonly Color DForm = Color.FromArgb(30, 32, 38);
    private static readonly Color DPanel = Color.FromArgb(38, 41, 49);
    private static readonly Color DText = Color.FromArgb(226, 229, 236);
    private static readonly Color DInput = Color.FromArgb(46, 50, 59);
    private static readonly Color DButton = Color.FromArgb(56, 61, 72);
    private static readonly Color DBorder = Color.FromArgb(62, 68, 80);

    public static Color FormBack => Dark ? DForm : LForm;
    public static Color PanelBack => Dark ? DPanel : LPanel;
    public static Color TextFore => Dark ? DText : LText;
    public static Color InputBack => Dark ? DInput : LInput;
    public static Color ButtonBack => Dark ? DButton : LButton;
    public static Color BorderColor => Dark ? DBorder : LBorder;
    public static Color SubText => Dark ? Color.FromArgb(146, 152, 165) : Color.FromArgb(110, 115, 125);
    public static Color Accent => Color.FromArgb(0, 120, 215);

    /// <summary>递归给一棵控件树套上主题</summary>
    public static void Apply(Control root)
    {
        ApplyOne(root);

        foreach (Control child in root.Controls)
            Apply(child);

        if (root is Form f && f.ContextMenuStrip != null)
            StyleMenu(f.ContextMenuStrip);
    }

    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.BackColor = PanelBack;
        menu.ForeColor = TextFore;
        menu.RenderMode = ToolStripRenderMode.System;
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = PanelBack;
            item.ForeColor = TextFore;
        }
    }

    private static void ApplyOne(Control c)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = FormBack;
                f.ForeColor = TextFore;
                break;

            case Button b:
                b.BackColor = ButtonBack;
                b.ForeColor = TextFore;
                if (b.FlatStyle == FlatStyle.Standard) b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = BorderColor;
                break;

            case TextBox t:
                t.BackColor = t.ReadOnly ? PanelBack : InputBack;
                t.ForeColor = TextFore;
                break;

            case ComboBox cb:
                cb.BackColor = InputBack;
                cb.ForeColor = TextFore;
                break;

            case NumericUpDown n:
                n.BackColor = InputBack;
                n.ForeColor = TextFore;
                break;

            case ListBox lb:
                lb.BackColor = InputBack;
                lb.ForeColor = TextFore;
                break;

            case ListView lv:
                lv.BackColor = InputBack;
                lv.ForeColor = TextFore;
                break;

            case CheckBox chk:
                chk.ForeColor = TextFore;
                break;

            case Label lbl:
                // 已经特意设成灰色（提示文字）的保留原色
                if (lbl.ForeColor != Color.FromArgb(110, 115, 125))
                    lbl.ForeColor = TextFore;
                break;

            case Panel p:
                // FlowLayoutPanel / TableLayoutPanel 都是 Panel 的子类，放这里统一处理
                if (p is FlowLayoutPanel || p is TableLayoutPanel)
                    p.BackColor = FormBack;
                else
                    p.BackColor = p.Parent is Form ? FormBack : PanelBack;
                break;

            case TrackBar tb:
                // TrackBar 不支持透明背景，设了会抛异常，所以不碰它
                break;

            case SplitContainer sc:
                sc.BackColor = FormBack;
                sc.Panel1.BackColor = FormBack;
                sc.Panel2.BackColor = PanelBack;
                break;
        }
    }
}
