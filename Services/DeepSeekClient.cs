using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kotoba.Services;

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>OpenAI 兼容的 chat/completions 调用（DeepSeek）</summary>
public static class DeepSeekClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };

    public static async Task<string> ChatAsync(
        string baseUrl,
        string apiKey,
        string model,
        string? systemPrompt,
        string userText,
        string? imageBase64,
        bool disableThinking,
        CancellationToken ct,
        bool jsonMode = false)
    {
        object userContent;
        if (string.IsNullOrEmpty(imageBase64))
        {
            userContent = userText;
        }
        else
        {
            userContent = new object[]
            {
                new { type = "text", text = userText },
                new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/jpeg;base64," + imageBase64, detail = "original" }
                }
            };
        }

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userContent });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0,
            ["stream"] = false
        };

        // DeepSeek 默认开启 thinking（effort=high），翻译场景必须关掉，否则延迟和成本暴涨
        if (disableThinking)
            payload["thinking"] = new { type = "disabled" };

        if (jsonMode)
            payload["response_format"] = new { type = "json_object" };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // 截断过长的错误体
            if (body.Length > 600) body = body.Substring(0, 600) + "...";
            throw new ApiException($"HTTP {(int)resp.StatusCode} ({resp.StatusCode}): {body}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

        // 记录 token 用量（失败不影响主流程）
        try
        {
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                int pt = usage.TryGetProperty("prompt_tokens", out var pTok) ? pTok.GetInt32() : 0;
                int outTok = usage.TryGetProperty("completion_tokens", out var cTok) ? cTok.GetInt32() : 0;
                UsageTracker.Add(pt, outTok);
            }
        }
        catch { }
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                throw new ApiException("响应里没有 choices");

            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString() ?? "";

            return "";
        }
        catch (JsonException ex)
        {
            throw new ApiException("响应解析失败: " + ex.Message);
        }
    }

    /// <summary>流式调用：逐段返回 delta 文本</summary>
    public static async IAsyncEnumerable<string> ChatStreamAsync(
        string baseUrl,
        string apiKey,
        string model,
        string? systemPrompt,
        string userText,
        bool disableThinking,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userText });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0,
            ["stream"] = true,
            // 让流式最后多发一个带 usage 的块；不要求的话流式调用的 token 完全不计
            ["stream_options"] = new { include_usage = true }
        };
        if (disableThinking) payload["thinking"] = new { type = "disabled" };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (err.Length > 400) err = err[..400] + "...";
            throw new ApiException($"HTTP {(int)resp.StatusCode}: {err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);

                // 流式最后一块带 usage（choices 为空）；不解析的话这块 token 就漏统计了
                if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                    usage.TryGetProperty("prompt_tokens", out var ptUse) &&
                    ptUse.ValueKind == JsonValueKind.Number)
                {
                    int compUse = usage.TryGetProperty("completion_tokens", out var ctUse) && ctUse.ValueKind == JsonValueKind.Number
                        ? ctUse.GetInt32() : 0;
                    UsageTracker.Add(ptUse.GetInt32(), compUse);
                }

                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var ch = choices[0];
                    if (ch.TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                    {
                        delta = c.GetString();
                    }
                }
            }
            catch { /* 单行解析失败跳过 */ }

            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }
}
