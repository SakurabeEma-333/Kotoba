using Kotoba.Models;

namespace Kotoba.Forms;

/// <summary>预设菜单构建器：分类 → 子菜单 → 预设项</summary>
public static class PresetMenuBuilder
{
    public static ContextMenuStrip Build(
        AppConfig cfg,
        Action<string> applyPreset,
        Action onNewPreset,
        Action onManage,
        Action onSaveCurrent)
    {
        var menu = new ContextMenuStrip();

        // ① 内置分类（代码内置，不可删）
        var sysMenu = new ToolStripMenuItem("内置提示词");
        foreach (var g in PromptPresets.Visible().GroupBy(x => x.Category))
        {
            var sub = new ToolStripMenuItem(g.Key);
            foreach (var (_, name, content) in g)
            {
                var item = new ToolStripMenuItem(name);
                var captured = content;
                item.Click += (_, _) => applyPreset(captured);
                sub.DropDownItems.Add(item);
            }
            sysMenu.DropDownItems.Add(sub);
        }
        menu.Items.Add(sysMenu);

        // ② 用户分类
        foreach (var cat in cfg.Categories.Where(c => !c.BuiltIn))
        {
            var catMenu = new ToolStripMenuItem(cat.Name);

            if (cat.Presets.Count == 0)
            {
                var empty = new ToolStripMenuItem("（空）") { Enabled = false };
                catMenu.DropDownItems.Add(empty);
            }
            else
            {
                foreach (var p in cat.Presets)
                {
                    var item = new ToolStripMenuItem(p.Name);
                    var captured = p.Content;
                    item.Click += (_, _) => applyPreset(captured);
                    catMenu.DropDownItems.Add(item);
                }
            }

            menu.Items.Add(catMenu);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("保存当前提示词为预设…", null, (_, _) => onSaveCurrent());
        menu.Items.Add("新增预设…", null, (_, _) => onNewPreset());
        menu.Items.Add("管理提示词…", null, (_, _) => onManage());

        return menu;
    }
}
