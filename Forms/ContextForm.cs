using System.Text;
using Kotoba.Models;
using Kotoba.Services;

namespace Kotoba.Forms;

/// <summary>
/// 查看上下文：列出本次会话累积的对话，可导出为文本文件，
/// 也可以让模型把这些对话总结成「记忆」。
/// </summary>
public class ContextForm : Form
{
    private readonly Profile _profile;
    private readonly AppConfig _cfg;
    private readonly List<(string Src, string Dst)> _history;

    private readonly TextBox _txtContext = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnImport = new();
    private readonly Button _btnSummarize = new();
    private readonly Label _lblStatus = new();

    /// <summary>总结完成后由调用方写回配置</summary>
    public bool MemoryUpdated { get; private set; }
    public string NewMemory { get; private set; } = "";

    public ContextForm(Profile profile, AppConfig cfg, List<(string Src, string Dst)> history)
    {
        _profile = profile;
        _cfg = cfg;
        _history = history;

        Text = "查看上下文 — " + profile.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 620);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.None;

        var tip = new Label
        {
            Text = "本次会话累积的对话记录。可以导出成文本文件，或者让模型总结成「记忆」长期保留。",
            Bounds = new Rectangle(20, 12, 740, 24),
            AutoSize = false,
            ForeColor = Color.FromArgb(70, 75, 85)
        };

        _txtContext.Multiline = true;
        _txtContext.ScrollBars = ScrollBars.Vertical;
        _txtContext.ReadOnly = true;
        _txtContext.BackColor = Color.FromArgb(247, 248, 250);
        _txtContext.Bounds = new Rectangle(20, 42, 740, 470);
        _txtContext.Font = new Font("Microsoft YaHei UI", 9.5F);
        _txtContext.Text = BuildContextText();

        _btnExport.Text = "导出为文档…";
        _btnExport.Bounds = new Rectangle(20, 528, 130, 38);
        _btnExport.Click += (_, _) => ExportToFile();

        _btnImport.Text = "导入上下文…";
        _btnImport.Bounds = new Rectangle(158, 528, 130, 38);
        _btnImport.Click += (_, _) => ImportContext();

        _btnSummarize.Text = "总结为记忆";
        _btnSummarize.Bounds = new Rectangle(296, 528, 130, 38);
        _btnSummarize.Click += async (_, _) => await SummarizeAsync();

        _lblStatus.Bounds = new Rectangle(434, 534, 212, 26);
        _lblStatus.AutoSize = false;
        _lblStatus.ForeColor = Color.FromArgb(120, 126, 138);
        _lblStatus.Text = _history.Count == 0 ? "（本次会话还没有上下文）" : $"共 {_history.Count} 条上下文";

        var btnClose = new Button();
        btnClose.Text = "关闭";
        btnClose.Bounds = new Rectangle(660, 528, 100, 38);
        btnClose.ForeColor = Color.Black;
        btnClose.BackColor = Color.FromArgb(240, 242, 245);
        btnClose.UseVisualStyleBackColor = false;
        btnClose.DialogResult = DialogResult.OK;

        Controls.Add(tip);
        Controls.Add(_txtContext);
        Controls.Add(_btnExport);
        Controls.Add(_btnImport);
        Controls.Add(_btnSummarize);
        Controls.Add(_lblStatus);
        Controls.Add(btnClose);

        AcceptButton = btnClose;
    }

    private string BuildContextText()
    {
        if (_history.Count == 0) return "（本次会话还没有上下文）";

        var sb = new StringBuilder();
        for (int i = 0; i < _history.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {_history[i].Src}");
            // 这个箭头是导入端的格式约定，改掉会导致「导出的文件自己读不回来」
            sb.AppendLine($"     → {_history[i].Dst}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void ExportToFile()
    {
        if (_history.Count == 0)
        {
            MessageBox.Show(this, "当前没有上下文可导出。", "查看上下文", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出上下文",
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = $"{SanitizeFileName(_profile.Name)}_上下文_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, BuildContextText(), Encoding.UTF8);
            _lblStatus.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "查看上下文", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    /// <summary>
    /// 导入之前导出的上下文文本。
    /// 格式：[N] 原文
    ///       → 译文
    /// </summary>
    private void ImportContext()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "导入上下文",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int added = 0;
        try
        {
            var lines = File.ReadAllLines(dlg.FileName);
            string? pendingSrc = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\d+)\]\s*(.+)$");
                if (m.Success)
                {
                    pendingSrc = m.Groups[2].Value.Trim();
                    continue;
                }

                if (pendingSrc != null && line.StartsWith("→"))
                {
                    var dst = line.TrimStart('→').Trim();
                    if (dst.Length > 0)
                    {
                        _history.Add((pendingSrc, dst));
                        added++;
                    }
                    pendingSrc = null;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "Kotoba");
            return;
        }

        _txtContext.Text = BuildContextText();
        _txtContext.SelectionStart = _txtContext.TextLength;
        _txtContext.ScrollToCaret();
        _lblStatus.Text = $"已导入 {added} 条，共 {_history.Count} 条";
        HistoryImported?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>导入上下文后触发，让调用方立即落盘</summary>
    public event EventHandler? HistoryImported;

    /// <summary>
    /// 记忆超过上限时从最早的段开始丢；带 ★ 的段永远保留，
    /// 所以最早那段是星标时，会顺位去丢下一条没星标的。
    /// </summary>
    private static string TrimMemory(string memory, int maxChars)
    {
        var parts = memory.Split(new[] { "\n---\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (parts.Count <= 1) return parts.Count == 1 ? parts[0] : "";

        int total = parts.Sum(s => s.Length + 5);
        while (total > maxChars)
        {
            int idx = parts.FindIndex(s => !s.StartsWith("★"));
            if (idx < 0) break;                 // 全是星标 → 一条也不丢
            total -= parts[idx].Length + 5;
            parts.RemoveAt(idx);
        }
        return string.Join("\n---\n", parts);
    }

    private async Task SummarizeAsync()
    {
        if (_history.Count == 0)
        {
            MessageBox.Show(this, "当前没有上下文可总结。先翻译几句再试。", "总结为记忆",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
        {
            MessageBox.Show(this, "还没有填 API Key（设置里填）。", "总结为记忆",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnSummarize.Enabled = false;
        _btnExport.Enabled = false;
        _lblStatus.Text = "正在总结，请稍候…";

        try
        {
            // 只取最近 N 条（设置里可调）：历史上限上千条，全塞进去可能上万 token
            int recentCount = Math.Max(1, _cfg.SummaryHistoryCount);
            var recent = _history.Count > recentCount
                ? _history.GetRange(_history.Count - recentCount, recentCount)
                : _history;
            var dialogText = string.Join("\n", recent.Select((h, i) => $"{i + 1}. {h.Src}\n   → {h.Dst}"));

            var prompt =
                "下面是一段日式RPG的游戏对话记录（日文原文 → 中文译文）。\n" +
                "请把它总结成简短的「长期记忆」，供后续翻译时参考。\n" +
                "重点记录：世界观设定、登场人物及称呼、人物关系、已发生的关键事件、反复出现的专有名词。\n" +
                "要求：用简体中文分条列出，总长不超过 300 字，不要复述每一句对话，只提炼信息。\n\n" +
                dialogText;

            var result = await DeepSeekClient.ChatAsync(
                _cfg.BaseUrl, _cfg.ApiKey, _profile.Model,
                systemPrompt: null, userText: prompt, imageBase64: null,
                disableThinking: false,
                CancellationToken.None);

            var summary = result.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                _lblStatus.Text = "总结失败：模型返回为空";
                return;
            }

            // 已有记忆就追加为一条（用 --- 分隔，每条可在记忆页里单独看到/编辑），
            // 但总长要封顶：记忆会拼进每一句翻译的系统提示词，无限追加会让 token 和延迟一路涨
            var merged = string.IsNullOrWhiteSpace(_profile.Memory)
                ? summary
                : _profile.Memory.TrimEnd() + "\n---\n" + summary;

            NewMemory = TrimMemory(merged, Math.Max(200, _cfg.MemoryMaxChars));

            MemoryUpdated = true;
            _lblStatus.Text = NewMemory.Length < merged.Length
                ? $"已生成总结（{summary.Length} 字）；记忆超过上限，最早的段落已被裁掉"
                : $"已生成总结（{summary.Length} 字），关闭时保存到记忆";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "总结失败：" + ex.Message;
        }
        finally
        {
            _btnSummarize.Enabled = true;
            _btnExport.Enabled = true;
        }
    }
}
