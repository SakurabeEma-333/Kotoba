using System.Text.Json;

namespace Kotoba.Services;

/// <summary>
/// API 用量统计。按日期重置「今日」，累计「总计」。
/// 价格按 deepseek-flash 非峰值估算（输入 $0.15/M、输出 $0.60/M，汇率按 7.1），
/// v4-pro 会贵不少，所以显示的是「估算值」。
/// </summary>
public static class UsageTracker
{
    public static long TodayIn { get; private set; }
    public static long TodayOut { get; private set; }
    public static long TotalIn { get; private set; }
    public static long TotalOut { get; private set; }
    public static string Today { get; private set; } = DateTime.Now.ToString("yyyy-MM-dd");

    private const decimal PriceInPerM = 0.15m;   // USD / 1M tokens
    private const decimal PriceOutPerM = 0.60m;
    private const decimal CnyRate = 7.1m;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kotoba");

    private static string FilePath => Path.Combine(Dir, "usage.json");

    private class Dto
    {
        public string Date { get; set; } = "";
        public long TodayIn { get; set; }
        public long TodayOut { get; set; }
        public long TotalIn { get; set; }
        public long TotalOut { get; set; }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto == null) return;

            TotalIn = dto.TotalIn;
            TotalOut = dto.TotalOut;

            if (dto.Date == DateTime.Now.ToString("yyyy-MM-dd"))
            {
                Today = dto.Date;
                TodayIn = dto.TodayIn;
                TodayOut = dto.TodayOut;
            }
            else
            {
                Today = DateTime.Now.ToString("yyyy-MM-dd");
                TodayIn = 0;
                TodayOut = 0;
            }
        }
        catch { /* 统计失败不影响使用 */ }
    }

    public static void Add(int promptTokens, int completionTokens)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd");
        if (now != Today)
        {
            Today = now;
            TodayIn = 0;
            TodayOut = 0;
        }

        TodayIn += promptTokens;
        TodayOut += completionTokens;
        TotalIn += promptTokens;
        TotalOut += completionTokens;

        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dto
            {
                Date = Today,
                TodayIn = TodayIn,
                TodayOut = TodayOut,
                TotalIn = TotalIn,
                TotalOut = TotalOut
            }));
        }
        catch { }
    }

    /// <summary>今日估算花费（人民币）</summary>
    public static decimal TodayCostCny =>
        ((decimal)TodayIn / 1_000_000m * PriceInPerM + (decimal)TodayOut / 1_000_000m * PriceOutPerM) * CnyRate;

    public static decimal TotalCostCny =>
        ((decimal)TotalIn / 1_000_000m * PriceInPerM + (decimal)TotalOut / 1_000_000m * PriceOutPerM) * CnyRate;

    /// <summary>给界面用的一行摘要</summary>
    public static string Summary()
    {
        long t = TodayIn + TodayOut;
        string tk = t >= 1000 ? (t / 1000.0).ToString("F1") + "K" : t.ToString();
        return $"今日 {tk} tokens";
    }
}
