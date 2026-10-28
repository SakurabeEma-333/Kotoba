namespace Kotoba.Models;

/// <summary>
/// 一套"游戏配置"：一块屏幕区域 + 一套提示词 + 一个模型 + 窗口外观。
/// ROI 与输出窗坐标都存 0~1 的比例，换分辨率不会错位。
/// </summary>
public class Profile
{
    public const string DefaultPrompt = """
        你是一名资深日式RPG译者，负责把游戏里的日文对话翻译成简体中文。
        要求：
        - 保持角色语气：敬语、粗鲁口吻、女性语尾要译出对应的中文语感
        - 「わ」「わよ」「ですわ」等女性语尾用「呢/呀/哦」，不要译成标准语
        - 第一人称按角色性格处理：俺→我、わし→老夫、あたし→人家、僕→我
        - 拟声词意译成中文习惯表达，不要硬凑象声词
        - **完整保留原文的说话人标记（如「主人公：」）、引号「」、括号（）、省略号……以及所有标点**
        - **不得省略、删减或概括任何原文内容，必须逐句对应翻译**
        - 口语化、简洁，符合对话体
        - 只输出译文，不添加解释或说明
        """;

    /// <summary>旧版默认提示词的特征片段（用于自动迁移）</summary>
    public const string LegacyPromptMarker = "不加解释、不加引号";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "新配置";

    public string SystemPrompt { get; set; } = DefaultPrompt;

    /// <summary>翻译用模型：deepseek-flash / deepseek-v4-pro</summary>
    public string Model { get; set; } = "deepseek-flash";

    // ---- 捕获框（相对虚拟桌面的比例） ----
    public double RoiX { get; set; } = 0.10;
    public double RoiY { get; set; } = 0.62;
    public double RoiW { get; set; } = 0.80;
    public double RoiH { get; set; } = 0.26;

    // ---- 译文窗（相对虚拟桌面的比例） ----
    public double OutX { get; set; } = 0.10;
    public double OutY { get; set; } = 0.12;
    public double OutW { get; set; } = 0.62;
    public double OutH { get; set; } = 0.20;

    /// <summary>译文字号（像素）</summary>
    public int FontSize { get; set; } = 16;

    /// <summary>译文窗背景不透明度 0~255</summary>
    public int BgOpacity { get; set; } = 200;

    /// <summary>识别框取景带浓度 0~100（越高越明显）</summary>
    public int CaptureOpacity { get; set; } = 45;

    /// <summary>边缘带宽度（逻辑像素）。同时是缩放命中区宽度，改小会让抓取变难</summary>
    public int CaptureBandWidth { get; set; } = 14;

    /// <summary>平时显示的边缘线粗细（逻辑像素）</summary>
    public int CaptureLineWidth { get; set; } = 2;

    /// <summary>光标离边缘多近才铺开取景带（逻辑像素），0 = 贴近才展开</summary>
    public int CaptureRevealPad { get; set; } = 16;

    /// <summary>术语表：一行一条「原文=译文」，翻译时自动拼到提示词后面</summary>
    public string Glossary { get; set; } = "";

    /// <summary>
    /// 保存下来的上下文记忆（JSON 数组 [{Src,Dst}]）。
    /// 不存的话上下文只在内存，重启/切配置就没了。
    /// </summary>
    public string SavedHistory { get; set; } = "";

    /// <summary>
    /// 模型总结出的长期记忆（世界观、人物关系等）。
    /// 每次翻译时会拼到系统提示词后面，让模型“记得”之前发生过什么。
    /// </summary>
    public string Memory { get; set; } = "";

    /// <summary>是否在译文窗里同时显示识别到的日文原文</summary>
    public bool ShowSource { get; set; } = true;

    /// <summary>捕获框是否锁定（锁定后鼠标穿透，不影响点游戏）</summary>
    public bool Locked { get; set; }

    /// <summary>是否自动翻译</summary>
    public bool AutoMode { get; set; }

    /// <summary>自动模式的轮询间隔（毫秒）</summary>
    public int AutoIntervalMs { get; set; } = 250;

    /// <summary>自动模式下画面稳定多久才触发（毫秒）</summary>
    public int AutoStableMs { get; set; } = 1200;

    /// <summary>画面要安静多久才触发自动翻译（毫秒）。小了容易提前翻，大了反应慢</summary>
    public int AutoQuietMs { get; set; } = 800;

    public Profile Clone() => (Profile)MemberwiseClone();
}
