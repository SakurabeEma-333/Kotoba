using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kotoba.Models;

namespace Kotoba.Services;

public class SentencePair
{
    public string Src { get; set; } = "";
    public string Dst { get; set; } = "";
}

public record StepResult(
    string Source,
    string Target,
    bool FromCache,
    long ElapsedMs,
    List<(string Src, string Dst)> Pairs);

/// <summary>
/// 两步管线：截图 → 视觉转写(flash) → 按句切分 → 逐句翻译(用户选的模型)
/// 缓存 key 用转写出的整段日文，命中后直接还原句子对。
/// </summary>
public class TranslationPipeline
{
    public const string OcrPrompt =
        "逐字输出图片中的所有日文文字，保留原文标点与换行。只输出文字本身，不要翻译，不要任何说明。";

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private bool _cacheLoaded;
    private DateTime _lastCacheSave = DateTime.MinValue;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kotoba", "cache.json");

    /// <summary>首次使用时把上次的缓存读回来（重启后不用重新花钱）</summary>
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (dict != null)
                foreach (var kv in dict) _cache[kv.Key] = kv.Value;
        }
        catch { /* 缓存坏了就当没有 */ }
    }

    /// <summary>落地缓存（节流：最多 5 秒写一次）</summary>
    private void SaveCache()
    {
        var now = DateTime.Now;
        if ((now - _lastCacheSave).TotalSeconds < 5) return;
        _lastCacheSave = now;

        try
        {
            var dir = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache));
        }
        catch { }
    }

    public int CacheCount
    {
        get { EnsureCacheLoaded(); return _cache.Count; }
    }

    /// <summary>清空内存缓存并删掉盘上的 cache.json（设置页的「清空缓存」用）</summary>
    public void ClearCacheAndPersist()
    {
        _cache.Clear();
        _cacheLoaded = true;   // 别再回头把旧文件读进来
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }

    public void ClearCache() => _cache.Clear();

    public async Task<StepResult> RunAsync(
        Bitmap roi,
        Profile profile,
        AppConfig cfg,
        IReadOnlyList<(string Src, string Dst)>? history,
        CancellationToken ct,
        bool ignoreCache = false)
    {
        EnsureCacheLoaded();
        var sw = Stopwatch.StartNew();

        // ① 视觉转写（固定用支持图片的 flash）
        var imgB64 = ScreenCapturer.ToBase64Jpeg(roi);
        var rawSrc = await DeepSeekClient.ChatAsync(
            cfg.BaseUrl, cfg.ApiKey, cfg.OcrModel,
            systemPrompt: null, userText: OcrPrompt, imageBase64: imgB64,
            disableThinking: true, ct).ConfigureAwait(false);

        var source = CleanOcr(rawSrc);
        if (string.IsNullOrWhiteSpace(source) || IsNoTextResult(source))
            return new StepResult("", "", false, sw.ElapsedMilliseconds, new());

        // 缓存 key 默认不含历史（历史每次都变，带进去基本命中不了），
        // 设置里开了「严格匹配上下文」才会把历史指纹拼进 key。
        var key = profile.Model + "|" + Hash(profile.SystemPrompt + "\u0001" + profile.Glossary + "\u0001" + profile.Memory)
            + "|" + Hash(source) + ContextKeyPart(cfg, history);

        if (!ignoreCache && _cache.TryGetValue(key, out var cachedJson))
        {
            var cachedPairs = DeserializePairs(cachedJson);
            return new StepResult(source, JoinTargets(cachedPairs), true, sw.ElapsedMilliseconds, cachedPairs);
        }

        var sysPrompt = BuildSystemPrompt(profile);
        var historyList = history ?? Array.Empty<(string Src, string Dst)>();

        // ② 文本翻译：多句时逐句对齐
        List<(string Src, string Dst)> pairs;
        var sentences = SplitSentences(source);

        if (sentences.Count <= 1)
        {
            var dst = await TranslateWholeAsync(source, profile, cfg, sysPrompt, historyList, ct).ConfigureAwait(false);
            pairs = new List<(string, string)> { (source, dst) };
        }
        else
        {
            pairs = await TranslateSentencesAsync(sentences, profile, cfg, sysPrompt, historyList, ct).ConfigureAwait(false);
        }

        _cache[key] = SerializePairs(pairs);
        SaveCache();
        return new StepResult(source, JoinTargets(pairs), false, sw.ElapsedMilliseconds, pairs);
    }

    /// <summary>系统提示词 + 长期记忆 + 术语表</summary>
    private static string BuildSystemPrompt(Profile p)
    {
        var sb = new StringBuilder(p.SystemPrompt);

        if (!string.IsNullOrWhiteSpace(p.Memory))
        {
            sb.Append("\n\n【长期记忆（之前发生过的剧情、人物、设定，仅供参考，不要直接输出）】\n");
            sb.Append(StripMemoryStars(p.Memory));
        }

        if (!string.IsNullOrWhiteSpace(p.Glossary))
        {
            var lines = p.Glossary.Replace("\r", "")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Contains('='))
                .ToList();

            if (lines.Count > 0)
            {
                sb.Append("\n\n术语对照（必须严格按此译法，不得自行改写）：\n");
                foreach (var l in lines)
                {
                    var i = l.IndexOf('=');
                    sb.Append("- ").Append(l[..i].Trim()).Append(" = ").Append(l[(i + 1)..].Trim()).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>记忆里的 ★ 只是「星标、不许丢」的标记，拼提示词时得摘掉</summary>
    private static string StripMemoryStars(string memory)
    {
        var segs = memory.Replace("\r", "")
            .Split("\n---\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimStart('★').Trim())
            .Where(s => s.Length > 0);
        return string.Join("\n", segs);
    }

    /// <summary>把前文对话拼到待翻译内容前面</summary>
    private static string WithHistory(IReadOnlyList<(string Src, string Dst)> history, string body)
    {
        if (history == null || history.Count == 0) return body;

        var sb = new StringBuilder();
        sb.AppendLine("【前文对话，仅供理解上下文，不要翻译这部分】");
        foreach (var (hSrc, hDst) in history)
            sb.AppendLine($"{hSrc}  →  {hDst}");
        sb.AppendLine();
        sb.AppendLine("【现在翻译下面这些句子】");
        sb.Append(body);
        return sb.ToString();
    }

    // ---------------- 流式翻译 ----------------

    /// <summary>
    /// 流式翻译：仍然按句对齐显示（一行原文配一行译文），
    /// 多句时用编号协议增量解析，已完成的句子先显示。
    /// </summary>
    public async Task<StepResult> RunStreamAsync(
        Bitmap roi,
        Profile profile,
        AppConfig cfg,
        IReadOnlyList<(string Src, string Dst)>? history,
        Action<List<(string Src, string Dst)>> onUpdate,
        CancellationToken ct,
        bool ignoreCache = false)
    {
        EnsureCacheLoaded();
        var sw = Stopwatch.StartNew();

        var imgB64 = ScreenCapturer.ToBase64Jpeg(roi);
        var rawSrc = await DeepSeekClient.ChatAsync(
            cfg.BaseUrl, cfg.ApiKey, cfg.OcrModel,
            systemPrompt: null, userText: OcrPrompt, imageBase64: imgB64,
            disableThinking: true, ct).ConfigureAwait(false);

        var source = CleanOcr(rawSrc);
        if (string.IsNullOrWhiteSpace(source) || IsNoTextResult(source))
            return new StepResult("", "", false, sw.ElapsedMilliseconds, new());

        var key = profile.Model + "|" + Hash(profile.SystemPrompt + "\u0001" + profile.Glossary + "\u0001" + profile.Memory) + "|" + Hash(source) + "|s" + ContextKeyPart(cfg, history);

        if (!ignoreCache && _cache.TryGetValue(key, out var cachedJson))
        {
            var cachedPairs = DeserializePairs(cachedJson);
            onUpdate(cachedPairs);
            return new StepResult(source, JoinTargets(cachedPairs), true, sw.ElapsedMilliseconds, cachedPairs);
        }

        var sysPrompt = BuildSystemPrompt(profile);
        var historyList = history ?? Array.Empty<(string Src, string Dst)>();
        var sentences = SplitSentences(source);

        List<(string Src, string Dst)> finalPairs;

        if (sentences.Count <= 1)
        {
            var sb = new StringBuilder();
            await foreach (var chunk in DeepSeekClient.ChatStreamAsync(
                cfg.BaseUrl, cfg.ApiKey, profile.Model,
                sysPrompt, WithHistory(historyList, source), disableThinking: !cfg.ThinkingEnabled, ct).ConfigureAwait(false))
            {
                sb.Append(chunk);
                var partial = CleanTranslation(sb.ToString());
                onUpdate(new List<(string, string)> { (source, partial) });
            }

            var full = CleanTranslation(sb.ToString());
            finalPairs = new List<(string, string)> { (source, full) };
        }
        else
        {
            var numbered = string.Join("\n", sentences.Select((s, i) => $"{i + 1}. {s}"));
            var userText =
                $"把下面 {sentences.Count} 段日文分别翻译成简体中文。\n" +
                $"要求：严格输出 {sentences.Count} 行，每行格式为「序号. 译文」；" +
                "段落一一对应，不要合并、不要拆分。\n" +
                "不要添加「（续）」「（上接）」「（下略）」之类的标记，也不要自行补全未说完的内容；" +
                "原文若被折行，应视为一整句完整翻译。不要输出任何额外说明。\n\n" + numbered;

            var results = new string[sentences.Count];
            var buffer = new StringBuilder();

            await foreach (var chunk in DeepSeekClient.ChatStreamAsync(
                cfg.BaseUrl, cfg.ApiKey, profile.Model,
                sysPrompt, WithHistory(historyList, userText), disableThinking: !cfg.ThinkingEnabled, ct).ConfigureAwait(false))
            {
                buffer.Append(chunk);
                ParseNumberedPartial(buffer.ToString(), results);
                onUpdate(BuildPairs(sentences, results));
            }

            ParseNumberedPartial(buffer.ToString(), results);
            finalPairs = BuildPairs(sentences, results);
        }

        _cache[key] = SerializePairs(finalPairs);
        SaveCache();
        return new StepResult(source, JoinTargets(finalPairs), false, sw.ElapsedMilliseconds, finalPairs);
    }

    /// <summary>增量解析「序号. 译文」，已出现的行实时填入</summary>
    private static void ParseNumberedPartial(string text, string[] results)
    {
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;

            var m = Regex.Match(t, @"^[\s\-\*]*(\d{1,3})\s*[.．、:：]\s*(.*)$");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int idx)) continue;
            if (idx < 1 || idx > results.Length) continue;

            var body = CleanTranslation(m.Groups[2].Value.Trim());
            if (body.Length > 0) results[idx - 1] = body;
        }
    }

    private static List<(string Src, string Dst)> BuildPairs(List<string> sentences, string[] results)
    {
        var list = new List<(string, string)>();
        for (int i = 0; i < sentences.Count; i++)
            list.Add((sentences[i], results[i] ?? ""));
        return list;
    }

    // ---------------- 翻译 ----------------

    private async Task<string> TranslateWholeAsync(
        string source, Profile profile, AppConfig cfg,
        string sysPrompt, IReadOnlyList<(string Src, string Dst)> history,
        CancellationToken ct)
    {
        var raw = await DeepSeekClient.ChatAsync(
            cfg.BaseUrl, cfg.ApiKey, profile.Model,
            systemPrompt: sysPrompt, userText: WithHistory(history, source), imageBase64: null,
            disableThinking: !cfg.ThinkingEnabled, ct).ConfigureAwait(false);

        return CleanTranslation(raw);
    }

    private async Task<List<(string Src, string Dst)>> TranslateSentencesAsync(
        List<string> sentences, Profile profile, AppConfig cfg,
        string sysPrompt, IReadOnlyList<(string Src, string Dst)> history,
        CancellationToken ct)
    {
        var numbered = string.Join("\n", sentences.Select((s, i) => $"{i + 1}. {s}"));

        var userText =
            $"把下面 {sentences.Count} 段日文分别翻译成简体中文。\n" +
            $"要求：严格输出 {sentences.Count} 行，每行格式为「序号. 译文」；" +
            "段落一一对应，不要合并、不要拆分。\n" +
            "不要添加「（续）」「（上接）」「（下略）」之类的标记，也不要自行补全未说完的内容；" +
            "原文若被折行，应视为一整句完整翻译。不要输出任何额外说明。\n\n" + numbered;

        var reply = await DeepSeekClient.ChatAsync(
            cfg.BaseUrl, cfg.ApiKey, profile.Model,
            systemPrompt: sysPrompt, userText: WithHistory(history, userText), imageBase64: null,
            disableThinking: !cfg.ThinkingEnabled, ct).ConfigureAwait(false);

        var dict = new Dictionary<int, string>();
        foreach (var rawLine in reply.Split('\n'))
        {
            var t = rawLine.Trim();
            if (t.Length == 0) continue;

            var m = Regex.Match(t, @"^[\s\-\*]*(\d{1,3})\s*[.．、:：]\s*(.+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
            {
                var body = CleanTranslation(m.Groups[2].Value.Trim());
                if (body.Length > 0) dict[idx] = body;
            }
        }

        var pairs = new List<(string Src, string Dst)>();
        int missing = 0;
        for (int i = 0; i < sentences.Count; i++)
        {
            if (dict.TryGetValue(i + 1, out var d)) pairs.Add((sentences[i], d));
            else { pairs.Add((sentences[i], "")); missing++; }
        }

        // 大面积失败就别显示一堆空行，退回整段翻译
        if (missing > sentences.Count / 2)
        {
            var whole = await TranslateWholeAsync(string.Join("\n", sentences), profile, cfg, sysPrompt, history, ct).ConfigureAwait(false);
            return new List<(string, string)> { (string.Join("\n", sentences), whole) };
        }

        return pairs;
    }

    // ---------------- 按行切分 ----------------

    /// <summary>
    /// 按行切分，但会合并「括号未闭合」的折行。
    /// 游戏里的长句（尤其是（ト書き：…）这类舞台提示）常被自动折成多行，
    /// 如果直接按行切，每行都是残句，模型会自己加「（续）」并重复翻译。
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        var rawLines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();

        var result = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;

        for (int i = 0; i < rawLines.Count; i++)
        {
            var line = rawLines[i];

            if (line.Length == 0)
            {
                if (sb.Length == 0) continue;
                if (depth <= 0)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    depth = 0;
                }
                continue;
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
            depth += BracketDelta(line);

            // 判断这一段是否已经完整
            bool complete = depth <= 0;

            // 括号闭合了也可能是「话没说完就换行」，再看后面的行
            if (complete && i + 1 < rawLines.Count)
            {
                if (NeedsMergeWithNext(line, rawLines[i + 1])) complete = false;
            }

            if (complete)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
                depth = 0;
            }
        }

        if (sb.Length > 0) result.Add(sb.ToString().Trim());

        return result.Count > 0 ? result : new List<string> { text.Trim() };
    }

    /// <summary>
    /// 判断这一行是不是「话没说完就被折行」：
    /// - 下一行是新开引号/括号 → 那是新的一句，不合并
    /// - 当前行已以句末标点（或右引号/括号）结束 → 完整，不合并
    /// - 当前行较长但没标点结尾 → 大概率是折行，合并
    /// </summary>
    /// <summary>是否以列表/说明条目的符号开头（这类行一定是新的一条）</summary>
    /// <summary>是否以列表/说明条目的符号开头（这类行一定是新的一条）</summary>
    private static bool IsListItemStart(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var t = line.TrimStart();
        if (t.Length == 0) return false;
        char c = t[0];
        if (c == '※' || c == '・' || c == '·' || c == '＊' || c == '*' || c == '•' || c == '☆' || c == '★') return true;
        if (t.Length >= 2 && char.IsDigit(t[0]) && (t[1] == '.' || t[1] == '．' || t[1] == ')' || t[1] == '）')) return true;
        return false;
    }

    /// <summary>
    /// 当前行是否应该与下一行合并。
    /// 合并的典型场景：游戏把一句话折成两行显示（行末无标点、下一行不是新条目）。
    /// </summary>
    private static bool NeedsMergeWithNext(string line, string next)
    {
        if (string.IsNullOrEmpty(next)) return false;

        var t = line.TrimEnd();
        if (t.Length == 0) return false;

        // 下一行是列表/说明条目开头 → 新的一条
        if (IsListItemStart(next)) return false;

        var nx = next.TrimStart();
        if (nx.Length == 0) return false;

        // 下一行是新开的引号/括号 → 新的一句
        char first = nx[0];
        if (first == '「' || first == '『' || first == '”' || first == '"' || first == '（' || first == '(' || first == '【')
            return false;

        // 下一行是人名对话（如「ルゥ：「……」）
        if (next.Contains("：「") || next.Contains("『「")) return false;

        // 当前行已以句末标点（或右引号/括号）结束 → 完整，不合并
        char last = t[^1];
        if (last == '。' || last == '！' || last == '？' || last == '!' || last == '?'
            || last == '」' || last == '』' || last == '”' || last == '）' || last == ')' || last == '】')
            return false;

        if (last == '…' || last == 'ー') return false;

        // 行足够长但没标点 → 大概率是折行
        return t.Length >= 18;
    }
    // ---------------- 精确模式：模型自己分段（推荐） ----------------

    private const string SegmentPrompt =
        "把下面这段日文按语义分成若干句，只输出分句结果，不要翻译。" +
        "严格输出 json 数组，每个元素是一句日文原文，格式如 [\"句子1\",\"句子2\"]。" +
        "不得改写、不得合并、不得遗漏，所有句子拼接起来应等于原文。" +
        "只输出 json，不要任何说明文字。";

    /// <summary>
    /// 精确模式：一次请求完成「按语义分段 + 逐句翻译」。
    /// 分段由模型判断，因此不会出现"折行被吞"或"一句被劈开"。
    /// </summary>
    public async Task<StepResult> RunSegmentedAsync(
        Bitmap roi,
        Profile profile,
        AppConfig cfg,
        IReadOnlyList<(string Src, string Dst)>? history,
        CancellationToken ct,
        bool ignoreCache = false)
    {
        EnsureCacheLoaded();
        var sw = Stopwatch.StartNew();

        var imgB64 = ScreenCapturer.ToBase64Jpeg(roi);
        var rawSrc = await DeepSeekClient.ChatAsync(
            cfg.BaseUrl, cfg.ApiKey, cfg.OcrModel,
            null, OcrPrompt, imgB64, true, ct).ConfigureAwait(false);

        var source = CleanOcr(rawSrc);
        if (string.IsNullOrWhiteSpace(source) || IsNoTextResult(source))
            return new StepResult("", "", false, sw.ElapsedMilliseconds, new());

        // ② 分段：用 flash + 关思考（纯切分不需要推理，快）
        var segKey = Hash(source) + "|seg2";
        List<string> sentences = new();

        // 对话框通常就一两行：这种情况直接当一整句，省掉一次分段请求（约 1~1.5 秒）
        if (IsSingleSentenceScreen(source))
        {
            sentences.Add(source);
        }
        else if (!ignoreCache && _cache.TryGetValue(segKey, out var cachedSeg))
        {
            try { sentences = JsonSerializer.Deserialize<List<string>>(cachedSeg) ?? new(); }
            catch { sentences = new(); }
        }
        if (sentences.Count == 0)
        {
            var segReply = await DeepSeekClient.ChatAsync(
                cfg.BaseUrl, cfg.ApiKey, cfg.OcrModel,
                null, SegmentPrompt + Environment.NewLine + Environment.NewLine + source,
                null, true, ct, jsonMode: true).ConfigureAwait(false);

            sentences = ParseSentenceArray(segReply, source);
            _cache[segKey] = JsonSerializer.Serialize(sentences);
        }

        // ③ 翻译：用用户选的模型，深度思考跟随设置（质量优先）
        var transKey = profile.Model + "|" + Hash(profile.SystemPrompt + "\u0001" + profile.Glossary + "\u0001" + profile.Memory)
            + "|" + Hash(string.Join("\u0001", sentences)) + ContextKeyPart(cfg, history);

        if (!ignoreCache && _cache.TryGetValue(transKey, out var cachedTrans))
        {
            var cachedPairs = DeserializePairs(cachedTrans);
            if (cachedPairs.Count > 0)
                return new StepResult(source, JoinTargets(cachedPairs), true, sw.ElapsedMilliseconds, cachedPairs);
        }

        var sys = BuildSystemPrompt(profile);
        var historyList = history ?? Array.Empty<(string Src, string Dst)>();
        var pairs = await TranslateSentencesAsync(sentences, profile, cfg, sys, historyList, ct).ConfigureAwait(false);

        _cache[transKey] = SerializePairs(pairs);
        SaveCache();

        return new StepResult(source, JoinTargets(pairs), false, sw.ElapsedMilliseconds, pairs);
    }

    /// <summary>解析分段结果（纯句子数组，或 {"sentences":[...]}）</summary>
    private static List<string> ParseSentenceArray(string reply, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : (doc.RootElement.TryGetProperty("sentences", out var s) ? s : default);

            if (arr.ValueKind != JsonValueKind.Array) return new() { fallback };

            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
            {
                var t = e.GetString();
                if (!string.IsNullOrWhiteSpace(t)) list.Add(t.Trim());
            }
            return list.Count > 0 ? list : new() { fallback };
        }
        catch { return new() { fallback }; }
    }

    private List<(string Src, string Dst)> ParseSegmented(string reply, string fallbackSource)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arr = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("pairs", out var p) && p.ValueKind == JsonValueKind.Array)
                arr = p;
            else
                return new List<(string, string)> { (fallbackSource, CleanTranslation(reply)) };

            var list = new List<(string, string)>();
            foreach (var item in arr.EnumerateArray())
            {
                string ja = item.TryGetProperty("ja", out var j) ? (j.GetString() ?? "") : "";
                string zh = item.TryGetProperty("zh", out var z) ? (z.GetString() ?? "") : "";
                if (zh.Length > 0) list.Add((ja, zh));
            }
            return list.Count > 0
                ? list
                : new List<(string, string)> { (fallbackSource, CleanTranslation(reply)) };
        }
        catch
        {
            // JSON 崩了就退化成整段，至少不丢内容
            return new List<(string, string)> { (fallbackSource, CleanTranslation(reply)) };
        }
    }
    // ---------------- 辅助 ----------------

    /// <summary>
    /// <summary>一两行、字数不多的画面不必再调分段模型，直接当一整句翻译（省一次请求，约 1~1.5 秒）</summary>
    private static bool IsSingleSentenceScreen(string src)
    {
        var lines = src.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Count(l => l.Length > 0);

        return lines <= 2 && src.Length <= 80;
    }

    /// OCR 没认出文字时，模型会回一句说明（“图片中没有文字”之类）。
    /// 这种不能当台词：白花一次翻译，还会把垃圾写进上下文。
    /// </summary>
    private static bool IsNoTextResult(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;

        var t = s.Trim();
        if (t.Length > 60) return false;   // 真台词不会这么短还带这些词

        return (t.Contains("图片") && t.Contains("文字"))
            || t.Contains("没有可识别")
            || t.Contains("未识别到")
            || t.Contains("没有文字")
            || t.Contains("no text", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanOcr(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        // 去整体包裹的引号/反引号
        if (t.Length > 1 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '`' && t[^1] == '`')))
            t = t[1..^1].Trim();
        return t;
    }

    private static string CleanTranslation(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();

        // 去模型可能加的前缀
        foreach (var prefix in new[] { "译文：", "译文:", "翻译：", "翻译:", "中文：", "中文:", "Japanese:", "Translation:" })
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                t = t[prefix.Length..].Trim();
                break;
            }
        }
        return t;
    }

    /// <summary>统计一行里括号的开合净值（正数 = 还有未闭合的括号）</summary>
    private static int BracketDelta(string s)
    {
        int d = 0;
        foreach (char c in s)
        {
            if (c == '（' || c == '(' || c == '「' || c == '『' || c == '“' || c == '【') d++;
            else if (c == '）' || c == ')' || c == '」' || c == '』' || c == '”' || c == '】') d--;
        }
        return d;
    }

    /// <summary>严格匹配上下文时，把历史对话的指纹拼进缓存 key（默认不拼，否则几乎永远命中不了）</summary>
    private static string ContextKeyPart(AppConfig cfg, IReadOnlyList<(string Src, string Dst)>? history)
    {
        if (!cfg.StrictContextCache || history == null || history.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var (s, d) in history) sb.Append(s).Append('\u0001').Append(d).Append('\u0002');
        return "|h" + Hash(sb.ToString());
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private static string SerializePairs(List<(string Src, string Dst)> pairs)
        => JsonSerializer.Serialize(pairs.Select(p => new SentencePair { Src = p.Src, Dst = p.Dst }).ToList());

    private static List<(string Src, string Dst)> DeserializePairs(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<SentencePair>>(json);
            if (list == null) return new List<(string, string)>();
            return list.Select(p => (p.Src, p.Dst)).ToList();
        }
        catch { return new List<(string, string)>(); }
    }

    private static string JoinTargets(List<(string Src, string Dst)> pairs)
        => string.Join("\n", pairs.Select(p => p.Dst));
}
