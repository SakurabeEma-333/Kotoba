namespace Kotoba.Models;

/// <summary>用户自定义的提示词预设</summary>
public class PromptPreset
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>预设分类（系统分类不可删除、不可改名）</summary>
public class PresetCategory
{
    public string Name { get; set; } = "";
    public bool BuiltIn { get; set; }
    public List<PromptPreset> Presets { get; set; } = new();

    // ComboBox 下拉靠 ToString 显示，不重写会显示成类名
    public override string ToString() => Name;
}

public class AppConfig
{
    /// <summary>DeepSeek API Key。留空时启动会尝试读环境变量 DEEPSEEK_API_KEY。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>接口地址，可换成任何 OpenAI 兼容服务</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>视觉转写固定用 flash（只有它支持图片输入）</summary>
    public string OcrModel { get; set; } = "deepseek-flash";

    /// <summary>是否流式输出（边生成边显示）</summary>
    public bool Streaming { get; set; } = false;

    /// <summary>翻译时是否开深度思考（thinking）。默认关：延迟约 3 倍、token 最多 27 倍</summary>
    public bool ThinkingEnabled { get; set; } = false;

    /// <summary>深色主题</summary>
    public bool DarkTheme { get; set; } = false;

    /// <summary>翻译快捷键，如 "F8" / "Ctrl+F8" / "Alt+F8"</summary>
    public string Hotkey { get; set; } = "F8";

    /// <summary>主窗口位置和大小（x,y,w,h）</summary>
    public string MainBounds { get; set; } = "";

    /// <summary>是否把最近几句作为上下文带给模型</summary>
    public bool ContextEnabled { get; set; } = true;

    /// <summary>上下文条数 1~8</summary>
    public int ContextCount { get; set; } = 3;

    /// <summary>译文窗里历史部分是否也显示原文</summary>
    public bool ContextShowSource { get; set; } = false;

    /// <summary>上下文：内存里最多保留多少条</summary>
    public int HistoryMaxEntries { get; set; } = 999;

    /// <summary>上下文：写进配置文件的条数</summary>
    public int HistorySaveCount { get; set; } = 200;

    /// <summary>记忆：点「总结为记忆」时取最近多少条对话</summary>
    public int SummaryHistoryCount { get; set; } = 60;

    /// <summary>记忆：总字数上限，超出后丢弃最早的非星标段</summary>
    public int MemoryMaxChars { get; set; } = 1500;

    /// <summary>缓存是否严格匹配上下文：开了命中率大降但译文一定对应当前语境，默认关</summary>
    public bool StrictContextCache { get; set; } = false;

    public string LastProfileId { get; set; } = "";

    /// <summary>用户自己保存的提示词预设（旧字段，仅用于迁移）</summary>
    public List<PromptPreset> Presets { get; set; } = new();

    /// <summary>预设分类（「系统」分类内置，其余用户自建）</summary>
    public List<PresetCategory> Categories { get; set; } = new();

    public List<Profile> Profiles { get; set; } = new();

    public Profile? FindProfile(string? id) => Profiles.FirstOrDefault(p => p.Id == id);

    public Profile EnsureProfile()
    {
        var p = FindProfile(LastProfileId);
        if (p != null) return p;
        if (Profiles.Count == 0) Profiles.Add(new Profile { Name = "默认" });
        LastProfileId = Profiles[0].Id;
        return Profiles[0];
    }
}