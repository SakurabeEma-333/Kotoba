using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Kotoba.Forms;

/// <summary>
/// 缩小后的小球：透明底、只留一圈淡蓝细框，点一下还原，拖动可以挪位置。
/// 用逐像素 alpha：中间填 alpha=1，肉眼看是透明的，但鼠标抓得住（色键做不到这点）。
/// </summary>
public class MiniBallForm : Form
{
    public event EventHandler? Restored;

    private const int WS_EX_LAYERED = 0x80000;
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private bool _dragging;
    private bool _moved;
    private Point _dragStart;
    private Point _windowStart;

    public MiniBallForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(34, 34);
        BackColor = Color.Black;
        Text = "Kotoba-Ball";
        Cursor = Cursors.Hand;

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _moved = false;
            _dragStart = Cursor.Position;
            _windowStart = Location;
        };

        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            var p = Cursor.Position;
            if (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3) _moved = true;
            Location = new Point(_windowStart.X + p.X - _dragStart.X, _windowStart.Y + p.Y - _dragStart.Y);
        };

        MouseUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            if (!_moved) Restored?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 强制成正方形：高 DPI 下 WinForms 可能把窗口拉成非正方形，这里锁死
        Size = new Size(34, 34);
        Render();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Render();
    }

    private void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        int w = Width, h = Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 用短边当直径并在窗口里居中：
            // 即使窗口被 DPI 缩放成非正方形，画出来也一定是正圆
            int d = Math.Min(w, h) - 3;
            if (d < 8) d = 8;
            int ox = (w - d) / 2;
            int oy = (h - d) / 2;

            // 内部 alpha=1：肉眼不可见，但整块都能接鼠标
            using (var fill = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillEllipse(fill, ox, oy, d, d);

            using var pen = new Pen(Color.FromArgb(210, 79, 195, 247), 2f);
            g.DrawEllipse(pen, ox + 1f, oy + 1f, d - 2f, d - 2f);
        }

        PushToScreen(bmp);
    }

    private void PushToScreen(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBits = SelectObject(memDc, hBitmap);

        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { X = 0, Y = 0 };
            var top = PointToScreen(Point.Empty);
            var dst = new POINT { X = top.X, Y = top.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBits);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
