namespace Kotoba.Forms;

/// <summary>上下文（前文对话）的读写与持久化。</summary>
public partial class MainForm
{
    private void AddToHistory(string src, string dst)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst)) return;

        // 同一句重复翻（缓存命中、A→B→A 来回看）不重复记录：
        // 把旧的那条挪到末尾，顺便用最新译文覆盖
        int exist = _history.FindIndex(p => p.Src == src);
        if (exist >= 0) _history.RemoveAt(exist);

        _history.Add((src, dst));
        // 跟设置里的「内存保留条数」对齐
        if (_history.Count > Math.Max(50, _cfg.HistoryMaxEntries)) _history.RemoveAt(0);

        SaveHistoryToProfile();
    }

    /// <summary>把配置里存的上下文读回内存</summary>
    private void LoadHistoryFromProfile()
    {
        _history.Clear();
        if (_current == null || string.IsNullOrWhiteSpace(_current.SavedHistory)) return;

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Services.SentencePair>>(_current.SavedHistory);
            if (saved == null) return;
            foreach (var p in saved)
                if (!string.IsNullOrWhiteSpace(p.Src)) _history.Add((p.Src, p.Dst));
        }
        catch { /* 存坏了就忽略，不影响翻译 */ }
    }

    /// <summary>把上下文写回配置（只存最近若干条，避免 config.json 无限膨胀）</summary>
    private void SaveHistoryToProfile()
    {
        if (_current == null) return;

        int take = Math.Min(Math.Max(1, _cfg.HistorySaveCount), _history.Count);
        var tail = new List<Services.SentencePair>(take);
        for (int i = _history.Count - take; i < _history.Count; i++)
            tail.Add(new Services.SentencePair { Src = _history[i].Src, Dst = _history[i].Dst });

        _current.SavedHistory = System.Text.Json.JsonSerializer.Serialize(tail, HistoryJsonOptions);
        Persist();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _overlay?.ClearDisplay();   // 连输出框里的历史文本一起擦掉

        if (_current != null)
        {
            _current.SavedHistory = "";
            Persist();
        }
    }
}
