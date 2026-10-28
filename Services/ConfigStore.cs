using System.Text.Encodings.Web;
using System.Text.Json;
using Kotoba.Models;

namespace Kotoba.Services;

public static class ConfigStore
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kotoba");

    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return CreateDefault();
            var json = File.ReadAllText(FilePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpt) ?? CreateDefault();
            if (cfg.Profiles.Count == 0) cfg.Profiles.Add(new Profile { Name = "默认" });

            // 迁移：旧版扁平 Presets → 分类结构（系统 + 我的）
            if (cfg.Categories.Count == 0)
            {
                cfg.Categories.Add(new PresetCategory { Name = "系统", BuiltIn = true });
                var mine = new PresetCategory { Name = "我的", BuiltIn = false };
                if (cfg.Presets.Count > 0) mine.Presets.AddRange(cfg.Presets);
                cfg.Categories.Add(mine);
            }

            // 自动迁移旧版默认提示词（旧版要求“不加引号”，会把「」和说话人标记吃掉）
            foreach (var p in cfg.Profiles)
            {
                if (p.SystemPrompt != null && p.SystemPrompt.Contains(Profile.LegacyPromptMarker, StringComparison.Ordinal))
                    p.SystemPrompt = Profile.DefaultPrompt;
            }

            return cfg;
        }
        catch
        {
            // 配置解析失败：先把坏文件改名备份，避免用户的术语表/记忆被默默覆盖
            try
            {
                if (File.Exists(FilePath))
                    File.Move(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            catch { }

            return CreateDefault();
        }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOpt));
        }
        catch
        {
            // 配置保存失败不影响运行
        }
    }

    private static AppConfig CreateDefault()
    {
        var cfg = new AppConfig
        {
            ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? ""
        };
        cfg.Profiles.Add(new Profile { Name = "默认" });
        cfg.LastProfileId = cfg.Profiles[0].Id;
        return cfg;
    }
}
