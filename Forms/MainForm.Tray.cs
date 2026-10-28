using System.Drawing;

namespace Kotoba.Forms;

/// <summary>托盘图标与全局热键。</summary>
public partial class MainForm
{
    /// <summary>按配置重新注册全局热键（改完设置后调用）</summary>
    public void RegisterHotkey()
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        HotkeyRegistered = RegisterSpec(_cfg.Hotkey, HOTKEY_ID);

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "kotoba-hotkey.txt"),
                $"translate={_cfg.Hotkey} ok={HotkeyRegistered}");
        }
        catch { }
    }

    /// <summary>把 "Ctrl+F8" 这种写法注册成全局热键，返回是否成功</summary>
    private bool RegisterSpec(string? spec, int id)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;

        uint mod = 0;
        var parts = spec.Split('+');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var t = parts[i].Trim().ToUpperInvariant();
            if (t == "CTRL" || t == "CONTROL") mod |= 0x0002;
            else if (t == "ALT") mod |= 0x0001;
            else if (t == "SHIFT") mod |= 0x0004;
        }

        var keyName = parts[^1].Trim();
        if (!Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key)) return false;

        return RegisterHotKey(Handle, id, mod | MOD_NOREPEAT, (uint)key);
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add("清空翻译显示", null, (_, _) => _overlay?.ClearDisplay());

        // 悬浮窗开关：菜单打开时根据当前状态切换文字
        var miToggleOverlay = new ToolStripMenuItem("关闭悬浮窗");
        miToggleOverlay.Click += (_, _) =>
        {
            if (_sessionRunning) StopSession(restoreWindow: false);
            else OnStartClicked();
        };
        menu.Items.Add(miToggleOverlay);

        menu.Opening += (_, _) =>
        {
            miToggleOverlay.Text = _sessionRunning ? "关闭悬浮窗" : "打开悬浮窗";
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { _reallyExit = true; Close(); });

        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Text = "Kotoba — 日文游戏翻译",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }
}
