using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Kotoba.Forms;

/// <summary>
/// 捕获框：完全自绘 + UpdateLayeredWindow 逐像素 alpha。
/// 为什么不用色键透明：色键是二值的，一个像素要么全透明（鼠标穿透到游戏）要么全不透明，
/// 于是"描边按钮"的洞里根本点不到，点下去会穿到游戏里。
/// 逐像素 alpha 下 alpha 只要 &gt; 0 就能接鼠标，所以按钮内部可以填 alpha=1：
/// 肉眼是透明的，鼠标仍然抓得住。
/// </summary>
public class CaptureForm : Form
{
    private const int WM_NCHITTEST = 0x84;
    private const int WM_PAINT = 0x000F;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12;
    private const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    public const int BarHeight = 30;

    private static readonly Color BarColor = Color.FromArgb(38, 40, 48);
    private static readonly Color BorderIdle = Color.FromArgb(79, 195, 247);
    private static readonly Color BorderLocked = Color.FromArgb(129, 199, 132);
    private static readonly Color BtnBorder = Color.FromArgb(210, 218, 228);
    private static readonly Color BtnBorderHot = Color.FromArgb(79, 195, 247);
    private static readonly Color BtnText = Color.FromArgb(238, 242, 248);

    /// <summary>边缘取景带宽度，同时是缩放命中区宽度</summary>
    private int _bandW = 14;
    private int _lineW = 2;
    private int _revealPad = 16;
    private bool _bandVisible;
    private bool _barArmed;

    /// <summary>按钮可见框与可点框的差：判定范围往里收这么多，避免擦边误触</summary>
    private const int HitInset = 4;

    /// <summary>四角握把边长（同时靠近两条边时算拖角）</summary>
    private const int CornerGrip = 12;

    private readonly System.Windows.Forms.Timer _hoverTimer = new();
    private const int DragThreshold = 5;

    // 按钮定义：文字 + 宽度（设备像素，与原子控件一致）
    private static readonly string[] BtnTexts = { "翻译", "重翻", "清空", "自动", "缩", "关闭", "锁定" };
    private static readonly int[] BtnW = { 52, 52, 56, 52, 52, 52, 64 };
    private const int BtnH = 24;
    private const int BtnTop = 3;
    private const int BtnGap = 4;
    private const int BtnRightMargin = 6;

    private readonly Rectangle[] _btnRects = new Rectangle[7];
    private int _hoverBtn = -1;
    private int _pressedBtn = -1;
    private bool _autoOn;

    private string _title = "";

    private bool _dragging;
    private bool _dragPending;
    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private int _captureOpacity = 45;

    public bool Locked { get; private set; }

    /// <summary>捕获区域（已向内让出边框宽度，避免把边框截进画面）</summary>
    public Rectangle RoiRect => new(
        Left + _bandW,
        Top + BarHeight + _bandW,
        Math.Max(1, Width - _bandW * 2),
        Math.Max(1, Height - BarHeight - _bandW * 2));

    public event EventHandler? TranslateRequested;
    public event EventHandler? RetranslateRequested;
    public event EventHandler? ClearContextRequested;
    public event EventHandler? AutoToggleRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? ShrinkRequested;
    public event EventHandler? LockChanged;
    public event EventHandler? RoiChanged;

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    /// <summary>不夺取焦点：点浮窗不会让游戏失焦暂停</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            return cp;
        }
    }

    public CaptureForm(string title)
    {
        _title = title;
        Text = "Kotoba-Capture";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(440, BarHeight + 60);
        DoubleBuffered = false;

        // 取景带/顶栏显隐：逐像素 alpha 下透明处收不到鼠标，只能轮询全局光标
        _hoverTimer.Interval = 60;
        _hoverTimer.Tick += (_, _) => { UpdateBandVisibility(); UpdateBarState(); };
        _hoverTimer.Start();

        MouseDown += OnMouseDownHandler;
        MouseMove += OnMouseMoveHandler;
        MouseUp += OnMouseUpHandler;
        MouseLeave += (_, _) => { if (_hoverBtn >= 0) { _hoverBtn = -1; Render(); } };

        ResizeEnd += (_, _) => RoiChanged?.Invoke(this, EventArgs.Empty);
        Resize += (_, _) => { if (!IsHandleCreated) return; LayoutBar(); Render(); };
    }

    // ---------------- 按钮 ----------------

    private void LayoutBar()
    {
        int right = Width - BtnRightMargin;
        for (int i = _btnRects.Length - 1; i >= 0; i--)
        {
            right -= BtnW[i];
            _btnRects[i] = new Rectangle(right, BtnTop, BtnW[i], BtnH);
            right -= BtnGap;
        }
    }

    /// <summary>可点区域：比可见框四周各内收 HitInset</summary>
    private Rectangle HitRect(int i)
    {
        var r = _btnRects[i];
        return new Rectangle(r.X + HitInset, r.Y + HitInset,
            Math.Max(1, r.Width - HitInset * 2), Math.Max(1, r.Height - HitInset * 2));
    }

    private int HitButton(Point p)
    {
        // 锁定后按钮仍要能点（否则解不开锁），所以不排除
        for (int i = 0; i < _btnRects.Length; i++)
            if (HitRect(i).Contains(p)) return i;
        return -1;
    }

    private void OnMouseDownHandler(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        int b = HitButton(e.Location);
        if (b >= 0)
        {
            _pressedBtn = b;
            Capture = true;
            Render();
            return;
        }

        BeginDrag(e);
    }

    private void OnMouseMoveHandler(object? s, MouseEventArgs e)
    {
        ContinueDrag();

        int b = HitButton(e.Location);
        if (b != _hoverBtn)
        {
            _hoverBtn = b;
            Cursor = b >= 0 ? Cursors.Hand : (_dragging ? Cursors.SizeAll : Cursors.Default);
            Render();
        }
    }

    private void OnMouseUpHandler(object? s, MouseEventArgs e)
    {
        if (_pressedBtn >= 0)
        {
            int b = _pressedBtn;
            _pressedBtn = -1;
            Capture = false;
            Render();
            if (HitButton(e.Location) == b) FireButton(b);
            return;
        }

        EndDrag();
    }

    private void FireButton(int index)
    {
        switch (index)
        {
            case 0: TranslateRequested?.Invoke(this, EventArgs.Empty); break;
            case 1: RetranslateRequested?.Invoke(this, EventArgs.Empty); break;
            case 2: ClearContextRequested?.Invoke(this, EventArgs.Empty); break;
            case 3: AutoToggleRequested?.Invoke(this, EventArgs.Empty); break;
            case 4: ShrinkRequested?.Invoke(this, EventArgs.Empty); break;
            case 5: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            case 6: SetLocked(!Locked); break;
        }
    }

    // ---------------- 拖动 ----------------

    private void BeginDrag(MouseEventArgs e)
    {
        if (Locked || e.Button != MouseButtons.Left) return;
        _dragPending = true;
        _dragging = false;
        _dragStartScreen = Cursor.Position;
        _dragStartWindow = Location;
        Capture = true;
    }

    private void ContinueDrag()
    {
        if (!_dragPending && !_dragging) return;

        var cur = Cursor.Position;

        if (_dragPending)
        {
            if (Math.Abs(cur.X - _dragStartScreen.X) < DragThreshold &&
                Math.Abs(cur.Y - _dragStartScreen.Y) < DragThreshold) return;

            _dragPending = false;
            _dragging = true;
            Cursor = Cursors.SizeAll;
        }

        if (!_dragging) return;

        Location = new Point(
            _dragStartWindow.X + (cur.X - _dragStartScreen.X),
            _dragStartWindow.Y + (cur.Y - _dragStartScreen.Y));
    }

    private void EndDrag()
    {
        _dragPending = false;
        if (!_dragging) { Capture = false; return; }
        _dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 状态 ----------------

    /// <summary>设置边缘外观：带宽（=命中区宽）、细线粗细、靠近多近才展开</summary>
    public void SetFrameStyle(int bandWidth, int lineWidth, int revealPad)
    {
        _bandW = Math.Clamp(bandWidth, 4, 30);
        _lineW = Math.Clamp(lineWidth, 1, 6);
        _revealPad = Math.Clamp(revealPad, 0, 60);
        UpdateBandVisibility();
        Render();
    }

    public void SetCaptureOpacity(int v)
    {
        _captureOpacity = Math.Clamp(v, 0, 100);
        Render();
    }

    public void SetAutoState(bool on)
    {
        _autoOn = on;
        Render();
    }

    public void ApplyBounds(Rectangle bounds, bool locked)
    {
        Bounds = bounds;
        Locked = locked;
        LayoutBar();
        UpdateBandVisibility();
        UpdateBarState();
        Render();
    }

    public void SetLocked(bool locked, bool raiseEvent = true)
    {
        Locked = locked;
        _hoverBtn = -1;
        _pressedBtn = -1;
        UpdateBandVisibility();
        UpdateBarState();
        Render();
        if (raiseEvent) LockChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>决定取景带该不该铺开。距离按矩形算，拖动时强制铺开</summary>
    private void UpdateBandVisibility()
    {
        if (!IsHandleCreated || IsDisposed) return;

        bool want;
        if (Locked) want = false;
        else if (_dragging) want = true;
        else
        {
            var c = Cursor.Position;
            var b = Bounds;
            int right = b.Right - 1, bottom = b.Bottom - 1;
            int dx = c.X < b.Left ? b.Left - c.X : (c.X > right ? c.X - right : 0);
            int dy = c.Y < b.Top ? b.Top - c.Y : (c.Y > bottom ? c.Y - bottom : 0);

            int dist = (dx > 0 || dy > 0)
                ? Math.Max(dx, dy)
                : Math.Min(Math.Min(c.X - b.Left, right - c.X),
                           Math.Min(c.Y - b.Top, bottom - c.Y));

            want = dist <= _bandW + _revealPad;
        }

        if (want == _bandVisible) return;
        _bandVisible = want;
        Render();
    }

    /// <summary>顶部工具条：平时透明（只剩按钮），光标靠近顶栏时才画出来并承担拖动</summary>
    private void UpdateBarState()
    {
        if (!IsHandleCreated || IsDisposed) return;

        bool want = false;
        if (!Locked)
        {
            var c = Cursor.Position;
            var b = Bounds;
            want = c.X >= b.Left - _revealPad && c.X <= b.Right + _revealPad
                && c.Y >= b.Top - _revealPad && c.Y <= b.Top + BarHeight + _revealPad;
        }

        if (want == _barArmed) return;
        _barArmed = want;
        Render();
    }

    // ---------------- 绘制 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutBar();
        Render();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 像素完全由 Render 提交，WinForms 不能插一脚，否则会用 GDI 盖掉 layered 位图
        ValidateRect(Handle, IntPtr.Zero);
    }

    private void Render()
    {
        if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;

        int w = Width, h = Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            float fontPx = 8f * DeviceDpi / 72f;
            using var font = new Font("Microsoft YaHei UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);

            // ① 顶栏底 + 标题（只在靠近时出现）
            if (!Locked && _barArmed)
            {
                using var barBrush = new SolidBrush(BarColor);
                g.FillRectangle(barBrush, 0, 0, w, BarHeight);

                using var titleBrush = new SolidBrush(Color.Gainsboro);
                using var titleFmt = new StringFormat { LineAlignment = StringAlignment.Center };
                int titleRight = _btnRects[0].X - 8;
                var titleRect = new Rectangle(10, 0, Math.Max(20, titleRight - 10), BarHeight);
                g.DrawString(_title, font, titleBrush, titleRect, titleFmt);
            }

            // ② 按钮：描边 + 文字，内部留 alpha=1 的不可见命中垫
            DrawButtons(g, font);

            // ③ 取景带
            if (!Locked && _bandVisible)
            {
                float k = Math.Clamp(_captureOpacity, 0, 100) / 100f;
                int rgb = (int)(34 + 190 * k);
                using var bandBrush = new SolidBrush(Color.FromArgb(150, rgb, rgb + 2, rgb + 8));
                int band = _bandW;
                g.FillRectangle(bandBrush, 0, BarHeight, w, band);
                g.FillRectangle(bandBrush, 0, h - band, w, band);
                g.FillRectangle(bandBrush, 0, BarHeight, band, h - BarHeight);
                g.FillRectangle(bandBrush, w - band, BarHeight, band, h - BarHeight);
            }

            // ④ 亮线：锁定前后同一个 inset，避免框"看起来变大一圈"
            // 未锁定时压到半透明，降低存在感；锁定后保持醒目，提示当前不可点
            var baseColor = Locked ? BorderLocked : BorderIdle;
            var color = Color.FromArgb(Locked ? 210 : 110, baseColor);
            int inset = _bandW;
            using (var pen = new Pen(color, Math.Max(1, _lineW)))
                g.DrawRectangle(pen, inset, BarHeight + inset,
                    Math.Max(1, w - inset * 2 - 1), Math.Max(1, h - BarHeight - inset * 2 - 1));

            // 四角握把：直接标出能拖角的位置，比盲抓 14px 的边可靠
            if (!Locked)
            {
                float lw = Math.Max(2f, DeviceDpi / 96f * 2f);
                using var gripPen = new Pen(color, lw);
                int o = 2, len = CornerGrip;

                g.DrawLine(gripPen, o, o, o + len, o);
                g.DrawLine(gripPen, o, o, o, o + len);

                g.DrawLine(gripPen, w - o - len, o, w - o, o);
                g.DrawLine(gripPen, w - o, o, w - o, o + len);

                g.DrawLine(gripPen, o, h - o, o + len, h - o);
                g.DrawLine(gripPen, o, h - o - len, o, h - o);

                g.DrawLine(gripPen, w - o - len, h - o, w - o, h - o);
                g.DrawLine(gripPen, w - o, h - o - len, w - o, h - o);
            }

            if (Locked)
            {
                using var tb = new SolidBrush(BorderLocked);
                g.DrawString("已穿透", font, tb, 12, BarHeight - fontPx - 4);
            }

            // ⑤ 命中垫：alpha=1 看不见，但鼠标不会穿到游戏里。
            // 只铺顶栏（拖动/按钮），其余地方保持穿透。
            using (var pad = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillRectangle(pad, 0, 0, w, BarHeight);
        }

        PushToLayeredWindow(bmp, w, h);
    }

    private void DrawButtons(Graphics g, Font font)
    {
        using var textFmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        float borderW = Math.Max(1f, DeviceDpi / 96f * 1.3f);

        for (int i = 0; i < _btnRects.Length; i++)
        {
            var r = _btnRects[i];
            bool hot = i == _hoverBtn;
            bool down = i == _pressedBtn;

            if (down)
            {
                using var fill = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                g.FillRectangle(fill, r);
            }

            using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), borderW + 2f))
                g.DrawRectangle(shadow, r.X + borderW / 2f, r.Y + borderW / 2f,
                    r.Width - borderW, r.Height - borderW);

            using (var pen = new Pen(hot ? BtnBorderHot : BtnBorder, borderW))
                g.DrawRectangle(pen, r.X + borderW / 2f, r.Y + borderW / 2f,
                    r.Width - borderW, r.Height - borderW);

            var text = i == 2 && _autoOn ? "自动中" : BtnTexts[i];

            // 1px 暗色投影：亮背景上白字也能认清（没有底色可用，只能靠描边）
            using (var shadowBrush = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                g.DrawString(text, font, shadowBrush,
                    new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), textFmt);

            using var brush = new SolidBrush(hot ? Color.White : BtnText);
            g.DrawString(text, font, brush, r, textFmt);
        }
    }

    private void PushToLayeredWindow(Bitmap bmp, int w, int h)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero;
        IntPtr oldBmp = IntPtr.Zero;

        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            oldBmp = SelectObject(memDc, hBmp);

            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { X = 0, Y = 0 };
            var origin = PointToScreen(Point.Empty);
            var dst = new POINT { X = origin.X, Y = origin.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------- 命中测试 ----------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PAINT)
        {
            ValidateRect(Handle, IntPtr.Zero);
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(Cursor.Position);

            int w = ClientSize.Width, h = ClientSize.Height;
            bool inBar = p.Y >= 0 && p.Y < BarHeight && p.X >= 0 && p.X < w;

            if (Locked)
            {
                m.Result = inBar ? (IntPtr)HTCLIENT : (IntPtr)HTTRANSPARENT;
                return;
            }

            // 角握把优先：同时靠近两条边时同时改宽高，比抓 14px 的边稳
            bool atLeft = p.X < _bandW;
            bool atRight = p.X >= w - _bandW;

            if (p.Y < CornerGrip && atLeft) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (p.Y < CornerGrip && atRight) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (p.Y >= h - CornerGrip && atLeft) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (p.Y >= h - CornerGrip && atRight) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }

            // 上下边不再单独提供缩放：高度只能从四角拉。
            // 上下边原本只8/20px，紧贴工具栏和底边，很容易误触，性价比低。
            if (atLeft && p.Y >= BarHeight) { m.Result = (IntPtr)HTLEFT; return; }
            if (atRight && p.Y >= BarHeight) { m.Result = (IntPtr)HTRIGHT; return; }

            // 顶栏空白：拖动手柄
            if (inBar) { m.Result = (IntPtr)HTCLIENT; return; }

            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        // 这里绝对不能 Render：UpdateLayeredWindow 的 dst 会移动窗口，会触发 LocationChanged，
        // 两边互相调用会让窗口无限漂移（OverlayForm 里踩过这个坑）
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hoverTimer.Dispose();
        base.Dispose(disposing);
    }
}
