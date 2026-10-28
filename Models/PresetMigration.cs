namespace Kotoba.Models;

/// <summary>
/// 旧版把内置提示词存在配置里（系统 / 全年龄 两个分类）。
/// 现在内置提示词随程序发布（PromptPresets），启动时把旧分类清掉，
/// 其中用户自己加的预设（比如「特定游戏提示词」）搬到「我的」里保留下来。
/// </summary>
public static class PresetMigration
{
    private static readonly string[] LegacyCategories = { "系统", "全年龄" };

    /// <summary>旧版内置过的预设名：直接丢弃，不再搬进「我的」</summary>
    private static readonly HashSet<string> LegacyPresets = new(StringComparer.Ordinal)
    {
        "RPG（优化版）", "视觉小说（优化版）", "通用（优化版）",
        "通用提示词", "视觉小说", "RPG游戏", "技术文档"
    };

    public static bool Run(AppConfig cfg)
    {
        var builtInNames = PromptPresets.BuiltIn.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        bool changed = false;

        foreach (var name in LegacyCategories)
        {
            var cat = cfg.Categories.FirstOrDefault(c => !c.BuiltIn && c.Name == name);
            if (cat == null) continue;

            var mine = cfg.Categories.FirstOrDefault(c => !c.BuiltIn && c.Name == "我的");
            if (mine == null)
            {
                mine = new PresetCategory { Name = "我的" };
                cfg.Categories.Add(mine);
            }

            foreach (var p in cat.Presets.Where(p => !builtInNames.Contains(p.Name) && !LegacyPresets.Contains(p.Name)).ToList())
            {
                if (!mine.Presets.Any(x => x.Name == p.Name)) mine.Presets.Add(p);
            }

            cfg.Categories.Remove(cat);
            changed = true;
        }

        // 清掉旧模型遗留的空内置分类（新内置只存在代码里，不会进配置）
        changed |= cfg.Categories.RemoveAll(c => c.BuiltIn && c.Presets.Count == 0) > 0;

        return changed;
    }
}
