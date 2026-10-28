using System.Drawing.Imaging;

namespace Kotoba.Services;

public static class ScreenCapturer
{
    /// <summary>抓取屏幕矩形区域（物理像素坐标）</summary>
    public static Bitmap Capture(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("区域无效");
        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>转 base64 JPEG，减小上传体积</summary>
    /// <summary>
    /// 转 base64 JPEG。超过 maxWidth 先等比缩小：
    /// 捕获框经常是 1900+ 宽，原尺寸上传既慢、vision 模型内部也要再缩一次，白白多花时间。
    /// </summary>
    public static string ToBase64Jpeg(Bitmap bmp, long quality = 88, int maxWidth = 1600)
    {
        Bitmap src = bmp;
        Bitmap? scaled = null;

        if (bmp.Width > maxWidth)
        {
            int h = Math.Max(1, (int)Math.Round(bmp.Height * (maxWidth / (double)bmp.Width)));
            scaled = new Bitmap(maxWidth, h);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, maxWidth, h);
            src = scaled;
        }

        try
        {
            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            src.Save(ms, codec, ps);
            return Convert.ToBase64String(ms.ToArray());
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>粗略的像素指纹，用于判断捕获框内容是否变化</summary>
    /// <summary>
    /// 把画面采样成 24x12 个量化点。自动模式用它比较“变了多少”，
    /// 比单个 hash 好用：hash 只能回答“变没变”，这里能回答“变了一点点还是整屏换页”。
    /// </summary>
    public static int[] Samples(Bitmap bmp)
    {
        const int cols = 24, rows = 12;
        var s = new int[cols * rows];
        int stepX = Math.Max(1, bmp.Width / cols);
        int stepY = Math.Max(1, bmp.Height / rows);

        int i = 0;
        for (int r = 0; r < rows; r++)
        {
            int y = Math.Min(bmp.Height - 1, r * stepY + stepY / 2);
            for (int c = 0; c < cols; c++)
            {
                int x = Math.Min(bmp.Width - 1, c * stepX + stepX / 2);
                var px = bmp.GetPixel(x, y);
                s[i++] = ((px.R >> 3) << 10) | ((px.G >> 3) << 5) | (px.B >> 3);
            }
        }
        return s;
    }

    /// <summary>两组采样里“明显不同”的点数（容忍轻微抗锯齿/压缩噪声）</summary>
    public static int DiffCount(int[]? a, int[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;

        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int dr = Math.Abs(((a[i] >> 10) & 31) - ((b[i] >> 10) & 31));
            int dg = Math.Abs(((a[i] >> 5) & 31) - ((b[i] >> 5) & 31));
            int db = Math.Abs((a[i] & 31) - (b[i] & 31));
            if (dr + dg + db > 12) diff++;
        }
        return diff;
    }
}
