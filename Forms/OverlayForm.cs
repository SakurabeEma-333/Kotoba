using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Kotoba.Forms;

/// <summary>
/// 译文窗：完全自绘 + UpdateLayeredWindow 管理透明度。
///
/// 为什么不用 Form.Opacity：
/// 那是整窗统一 alpha 的分层窗口，改变大小时 DWM 不会重建整个 surface，
/// 会出现背景残缺、边框消失、内容错位。UpdateLayeredWindow 每帧由我们自己提交
/// 完整位图，resize 时整块重画，且能做到「背景半透明、文字不透明」。
/// </summary>
public class OverlayForm : Form
{
    private const int WM_NCHITTEST = 0x84;
    private const int HTTRANSPARENT = -1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12;
    private const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int GripMax = 28;
    private const int DragThreshold = 5;

    /// <summary>边缘可抓住区宽度：固定 28 会让小窗口几乎全被吃掉，所以按尺寸自适应</summary>
    private int Grip => Math.Min(GripMax, Math.Max(12, Math.Min(ClientSize.Width, ClientSize.Height) / 6));
    private const int PadX = 16;
    private const int PadY = 12;
    private const int GapSentence = 14;

    private const int WM_MOUSEWHEEL = 0x020A;

    private static readonly Color BgColor = Color.FromArgb(12, 13, 18);
    private static readonly Color BorderColor = Color.FromArgb(120, 145, 170);
    private static readonly Color TargetColor = Color.White;
    private static readonly Color SourceColor = Color.FromArgb(140, 165, 185);
    private static readonly Color StateColor = Color.FromArgb(140, 160, 175);
    private static readonly Color ErrorColor = Color.FromArgb(255, 140, 120);

    // ---- Win32 ----
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
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    private const int WM_PAINT = 0x000F;
    public static string LastRenderDiagnostic = "";

    // ---- 状态 ----
    private List<(string Src, string Dst)> _pairs = new();
    private List<(string Src, string Dst)> _history = new();
    private bool _showHistorySource = false;
    private bool _showSource = true;
    private string _stateText = "等待翻译…";
    private Color _stateColor = StateColor;
    private bool _showState = true;

    private Font? _fontTarget;
    private Font? _fontSource;
    private Font? _fontState;
    private int _fontSizePx = 16;
    private int _dpi = 96;
    private int _bgAlpha = 220;

    private bool _dragging;
    private bool _dragPending;
    private Point _dragStartScreen;
    private Point _dragStartWindow;

    private DateTime _lastRender = DateTime.MinValue;


    public event EventHandler? BoundsChanged;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "Kotoba-Overlay";
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgColor;
        MinimumSize = new Size(280, 110);
        DoubleBuffered = true;
        Cursor = Cursors.SizeAll;
        SetStyle(ControlStyles.Opaque, true);

        BuildFonts();

        MouseDown += OnDragDown;
        MouseMove += OnDragMove;
        MouseUp += OnDragUp;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RenderLayer();
    }



    private void ScrollByWheel(int delta)
    {
        // 向上滚（delta>0）看历史，向下滚回底
        _stickToBottom = false;
        float step = 60f * (delta / 120f);
        _scrollOffset = Math.Clamp(_scrollOffset - step, 0, _maxOffset);

        if (_scrollOffset >= _maxOffset - 1) _stickToBottom = true;

        RenderLayer();
    }

    // ---------------- 字体 ----------------

    private float DpiScale => Math.Max(1f, _dpi / 96f);

    private void BuildFonts()
    {
        _fontTarget?.Dispose();
        _fontSource?.Dispose();
        _fontState?.Dispose();

        float px = Math.Max(10f, _fontSizePx * DpiScale);
        _fontTarget = new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontSource = new Font("Microsoft YaHei UI", px * 0.7f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontState = new Font("Microsoft YaHei UI", px * 0.85f, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public void ApplyStyle(int fontSizePx, int bgOpacity, int dpi)
    {
        if (dpi > 0) _dpi = dpi;
        _fontSizePx = fontSizePx;
        _bgAlpha = Math.Clamp(bgOpacity, 0, 255);
        BuildFonts();
        RenderLayer();
    }

    // ---------------- 内容 ----------------

    public void ShowWaiting(string text)
    {
        _showState = true;
        _stateColor = StateColor;
        _stateText = text;
        _pairs = new();
        RenderLayer();
    }

    public void ShowError(string message)
    {
        _showState = true;
        _stateColor = ErrorColor;
        _stateText = "⚠ " + message;
        _pairs = new();
        RenderLayer();
    }

    private DateTime _lastStreamRender = DateTime.MinValue;

    private float _scrollOffset;
    private float _contentHeight;
    private bool _scrolling;
    private int _scrollAnchorY;
    private float _scrollAnchorOffset;
    private bool _stickToBottom = true;

    private const int ScrollBarWidth = 7;
    private const int ScrollBarRightGap = 40;
    private float _maxOffset;
    private bool _needsRepaint;
    private float _thumbH = 30f;

    private string _status = "待机";
    private System.Windows.Forms.Timer? _statusTimer;

    /// <summary>右下角状态：待机 / 识别中 / 翻译中 / 完成 / 出错</summary>
    public void SetStatus(string text, int keepMs = 0)
    {
        _status = text;

        _statusTimer?.Stop();
        _statusTimer?.Dispose();
        _statusTimer = null;

        if (keepMs > 0)
        {
            _statusTimer = new System.Windows.Forms.Timer { Interval = keepMs };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer?.Stop();
                _status = "待机";
                RenderLayer();
            };
            _statusTimer.Start();
        }

        RenderLayer();
    }


    /// <summary>流式按句更新（保留逐句排版）</summary>
    public void StreamPairs(
        IReadOnlyList<(string Src, string Dst)> pairs,
        IReadOnlyList<(string Src, string Dst)>? history,
        bool showHistorySource,
        bool showSource,
        bool force = false)
    {
        _history = history?.ToList() ?? new List<(string, string)>();
        _showHistorySource = showHistorySource;
        _showSource = showSource;
        _pairs = pairs.ToList();
        _showState = false;

        var now = DateTime.Now;
        if (!force && (now - _lastStreamRender).TotalMilliseconds < 40) return;
        _lastStreamRender = now;

        if (_stickToBottom) { /* 滚动位置由 DrawContent 末尾校正 */ }
        RenderLayer();
    }

    /// <summary>流式开始：先把原文占上位置</summary>
    /// <summary>调试用：强制滚到指定位置并重画</summary>
    public void DebugScrollTo(float offset)
    {
        _scrollOffset = Math.Clamp(offset, 0, Math.Max(0, _maxOffset));
        _stickToBottom = false;
        RenderLayer();
    }

    /// <summary>清空显示：连历史一起擦掉，回到「待机」空状态</summary>
    public void ClearDisplay()
    {
        _pairs.Clear();
        _history.Clear();
        _scrollOffset = 0;
        _stickToBottom = true;
        _showState = true;
        _stateColor = StateColor;
        _stateText = "（已清空，按 F8 或点翻译）";
        _statusTimer?.Stop();
        _status = "待机";
        RenderLayer();
    }

    public void ShowPairs(
        IReadOnlyList<(string Src, string Dst)> pairs,
        IReadOnlyList<(string Src, string Dst)>? history,
        bool showHistorySource,
        bool showSource,
        int fontSizePx,
        int bgOpacity,
        int dpi)
    {
        if (dpi > 0) _dpi = dpi;
        _fontSizePx = fontSizePx;
        _bgAlpha = Math.Clamp(bgOpacity, 0, 255);
        BuildFonts();

        _showSource = showSource;
        _history = history?.ToList() ?? new List<(string, string)>();
        _showHistorySource = showHistorySource;
        _pairs = pairs.ToList();
        _showState = _pairs.Count == 0;
        if (_showState)
        {
            _stateColor = StateColor;
            _stateText = "（没有识别到文字）";
        }

        _stickToBottom = true;
        _scrollOffset = 0;
        RenderLayer();
    }

    // ---------------- 拖动 ----------------

    private void OnDragDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var p = PointToClient(Cursor.Position);
        int sbLeft = ClientSize.Width - ScrollBarRightGap;

        // 右侧滚动条：拖着滚，不移动窗口
        if (_maxOffset > 1 && p.X >= sbLeft - 6 && p.X <= sbLeft + ScrollBarWidth + 6)
        {
            _scrolling = true;
            _stickToBottom = false;
            _scrollAnchorY = Cursor.Position.Y;
            _scrollAnchorOffset = _scrollOffset;
            Capture = true;
            return;
        }

        _dragPending = true;
        _dragging = false;
        _dragStartScreen = Cursor.Position;
        _dragStartWindow = Location;
        Capture = true;
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        var cur = Cursor.Position;

        if (_scrolling)
        {
            float trackH = Math.Max(1f, ClientSize.Height - PadY * 2 - 22);
            float scrollable = Math.Max(1f, trackH - _thumbH);
            float ratio = _maxOffset / scrollable;
            _scrollOffset = Math.Clamp(_scrollAnchorOffset + (cur.Y - _scrollAnchorY) * ratio, 0, _maxOffset);
            RenderLayer();
            return;
        }

        // 死区：按住后必须先移动超过阈值才开始拖，避免手抖一下窗户就跳走
        if (_dragPending)
        {
            if (Math.Abs(cur.X - _dragStartScreen.X) < DragThreshold &&
                Math.Abs(cur.Y - _dragStartScreen.Y) < DragThreshold) return;

            _dragPending = false;
            _dragging = true;
        }

        if (!_dragging) return;

        Location = new Point(
            _dragStartWindow.X + (cur.X - _dragStartScreen.X),
            _dragStartWindow.Y + (cur.Y - _dragStartScreen.Y));
    }

    private void OnDragUp(object? sender, MouseEventArgs e)
    {
        if (_scrolling)
        {
            _scrolling = false;
            Capture = false;
            return;
        }

        _dragPending = false;
        if (!_dragging) { Capture = false; return; }
        _dragging = false;
        Capture = false;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 渲染 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        // 全部走 RenderLayer，这里不做绘制
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 禁用默认背景绘制，避免和 layered 位图冲突
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;

        // 拖大小期间限频重绘：位图 + 文字排版若每像素都重做会很卡，
        // 表现为「拖边缘不跟手」。停手后（ResizeEnd）再完整重绘一次。
        var now = DateTime.Now;
        if ((now - _lastRender).TotalMilliseconds >= 45)
        {
            _lastRender = now;
            RenderLayer();
        }
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!IsHandleCreated) return;
        _lastRender = DateTime.Now;
        RenderLayer();
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        // 注意：这里绝对不能调 RenderLayer！
        // UpdateLayeredWindow 的 dst 参数会移动窗口，会触发 LocationChanged，
        // 两边互相调用会导致窗口无限漂移、屏幕上留一堆残影。
    }

    /// <summary>四角握把：很淡的 L 形角标，不开背板时也能看见窗口在哪、能拖角改大小</summary>
    private static void DrawCornerGrips(Graphics g, int w, int h)
    {
        int len = 10, o = 2;
        using var pen = new Pen(Color.FromArgb(90, 235, 240, 248), 2f);

        g.DrawLine(pen, o, o, o + len, o);
        g.DrawLine(pen, o, o, o, o + len);

        g.DrawLine(pen, w - o - len, o, w - o, o);
        g.DrawLine(pen, w - o, o, w - o, o + len);

        g.DrawLine(pen, o, h - o, o + len, h - o);
        g.DrawLine(pen, o, h - o - len, o, h - o);

        g.DrawLine(pen, w - o - len, h - o, w - o, h - o);
        g.DrawLine(pen, w - o, h - o - len, w - o, h - o);
    }

    private void RenderLayer()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        if (_fontTarget == null || _fontSource == null || _fontState == null) return;

        int w = Width;
        int h = Height;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.None;

            // 背景（半透明）
            using (var bg = new SolidBrush(Color.FromArgb(_bgAlpha, BgColor)))
                g.FillRectangle(bg, 0, 0, w, h);

            // 边框（不透明）
            using (var pen = new Pen(BorderColor, 2f))
                g.DrawRectangle(pen, 1, 1, w - 3, h - 3);

            DrawContent(g, w, h);

            // 背景几乎全透明（0~7）时铺一层极淡的命中垫：
            // 肉眼看不出来，但鼠标不会穿过去，窗口还能拖、能改大小
            if (_bgAlpha < 8)
            {
                using var pad = new SolidBrush(Color.FromArgb(4, 0, 0, 0));
                g.FillRectangle(pad, 0, 0, w, h);
            }

            DrawCornerGrips(g, w, h);
        }

        PushToLayeredWindow(bmp, w, h);

        // 贴底模式下若在绘制过程中发现要滚动到底，提交完这一帧后再画一次
        if (_needsRepaint)
        {
            _needsRepaint = false;
            RenderLayer();
        }
    }

    private void DrawContent(Graphics g, int w, int h)
    {
        int contentW = Math.Max(40, w - PadX * 2 - ScrollBarWidth - 10);
        float y = PadY - _scrollOffset;
        float top = y;

        // 注意：不能用 TextRenderer（GDI）—— 它在带 alpha 的 layered 位图上
        // 会出现定位错位、相互重叠。必须用 Graphics.DrawString（GDI+）。
        using var sf = new StringFormat(StringFormatFlags.LineLimit)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None
        };

        if (_showState || _pairs.Count == 0)
        {
            using var sbState = new SolidBrush(_stateColor);
            var sz = g.MeasureString(_stateText, _fontState!, new SizeF(contentW, float.MaxValue), sf);
            g.DrawString(_stateText, _fontState!, sbState,
                new RectangleF(PadX, y, contentW, sz.Height), sf);
            return;
        }

        // 前文上下文（暗灰小字，和当前句区分开）
        if (_history.Count > 0)
        {
            using var histBrush = new SolidBrush(Color.FromArgb(108, 118, 132));

            foreach (var (hs, hd) in _history)
            {

                if (!string.IsNullOrWhiteSpace(hd))
                {
                    var hsz = g.MeasureString(hd, _fontSource!, new SizeF(contentW, float.MaxValue), sf);
                    g.DrawString(hd, _fontSource!, histBrush,
                        new RectangleF(PadX, y, contentW, hsz.Height), sf);
                    y += hsz.Height;
                }

                if (_showHistorySource && !string.IsNullOrWhiteSpace(hs) && y < h - PadY)
                {
                    var hssz = g.MeasureString(hs, _fontSource!, new SizeF(contentW, float.MaxValue), sf);
                    g.DrawString(hs, _fontSource!, histBrush,
                        new RectangleF(PadX, y, contentW, hssz.Height), sf);
                    y += hssz.Height;
                }

                y += 6;
            }

            if (y < h - PadY)
            {
                using var sep = new Pen(Color.FromArgb(60, 70, 84), 1f);
                g.DrawLine(sep, PadX, y + 4, PadX + contentW, y + 4);
                y += 12;
            }
        }

        for (int i = 0; i < _pairs.Count; i++)
        {
            var (src, dst) = _pairs[i];

            string t = string.IsNullOrWhiteSpace(dst) ? "（本句未翻译）" : dst;
            var tSize = g.MeasureString(t, _fontTarget!, new SizeF(contentW, float.MaxValue), sf);

            using (var tb = new SolidBrush(TargetColor))
            {
                g.DrawString(t, _fontTarget!, tb,
                    new RectangleF(PadX, y, contentW, tSize.Height), sf);
            }
            y += tSize.Height + 3;

            if (_showSource && !string.IsNullOrWhiteSpace(src))
            {
                var sSize = g.MeasureString(src, _fontSource!, new SizeF(contentW, float.MaxValue), sf);
                if (y < h - PadY)
                {
                    using var sbSrc = new SolidBrush(SourceColor);
                    g.DrawString(src, _fontSource!, sbSrc,
                        new RectangleF(PadX, y, contentW, sSize.Height), sf);
                }
                y += sSize.Height;
            }

            y += GapSentence;
        }

        // 记录总内容高 + 限制滚动范围
        float contentH = y - top;
        _contentHeight = contentH;
        float maxOffset = Math.Max(0, contentH - (h - PadY * 2));
        _maxOffset = maxOffset;

        // 贴底模式：先判断位置对不对，再赋值并同步重画一次
        // （顺序不能反，否则会先赋值导致判断永远为假、不再重画）
        if (_stickToBottom && Math.Abs(_scrollOffset - maxOffset) > 0.5f)
        {
            _scrollOffset = maxOffset;
            _needsRepaint = true;   // 提交这一帧后再画一次（不能在这里递归，会覆盖结果）
            return;
        }

        DrawScrollBar(g, w, h, maxOffset);
        DrawStatus(g, w, h);
    }

    private void DrawStatus(Graphics g, int w, int h)
    {
        // “完成”只显示几秒，超时回“待机”

        float px = Math.Max(10f, _fontSizePx * 0.58f * DpiScale);
        using var f = new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        using var br = new SolidBrush(Color.FromArgb(115, 126, 142));

        var sz = g.MeasureString(_status, f);
        g.DrawString(_status, f, br, w - sz.Width - 14, h - sz.Height - 6);
    }

    private void DrawScrollBar(Graphics g, int w, int h, float maxOffset)
    {
        if (maxOffset <= 1) return;

        float trackTop = PadY;
        // 底部留出 22px 给状态文字
        float trackH = h - PadY * 2 - 22;
        if (trackH <= 0) return;

        float thumbH = Math.Max(28f, trackH * trackH / Math.Max(1f, _contentHeight));
        _thumbH = thumbH;
        thumbH = Math.Min(thumbH, trackH);
        float thumbTop = trackTop + (trackH - thumbH) * (_scrollOffset / maxOffset);

        int x = w - ScrollBarRightGap;

        using (var track = new SolidBrush(Color.FromArgb(130, 30, 34, 44)))
            g.FillRectangle(track, x, trackTop, ScrollBarWidth, trackH);

        using var thumb = new SolidBrush(Color.FromArgb(240, 160, 175, 196));
        g.FillRectangle(thumb, x, thumbTop, ScrollBarWidth, thumbH);
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

            bool ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            LastRenderDiagnostic = $"ok={ok} err={Marshal.GetLastWin32Error()} size={w}x{h} pos={Left},{Top} alpha={_bgAlpha}";
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
        // 完全自己管理像素：不能让 WinForms 处理 WM_PAINT，
        // 否则它会用 GDI 画一遍背景，把 layered 位图覆盖掉
        if (m.Msg == WM_PAINT)
        {
            ValidateRect(Handle, IntPtr.Zero);
            return;
        }

        if (m.Msg == 0x00A3)   // WM_NCLBUTTONDBLCLK：正文是 HTCAPTION，双击会触发最大化
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_MOUSEWHEEL)
        {
            // Win10/11 默认会把滚轮发给光标下的窗口（“悬停时滚动非活动窗口”），
            // 不依赖全局钩子，直接在这里处理最可靠。
            if (_maxOffset > 1)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                ScrollByWheel(delta);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        if (m.Msg == WM_NCHITTEST)
        {

            var p = PointToClient(Cursor.Position);
            int g = Grip;
            bool left = p.X < g;
            bool right = p.X >= ClientSize.Width - g;
            bool top = p.Y < g;
            bool bottom = p.Y >= ClientSize.Height - g;

            if (left && top) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (right && top) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (left && bottom) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (right && bottom) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
            if (left) { m.Result = (IntPtr)HTLEFT; return; }
            if (right) { m.Result = (IntPtr)HTRIGHT; return; }
            if (top) { m.Result = (IntPtr)HTTOP; return; }
            if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

            // 正文区交给系统拖（HTCAPTION）。
            // 不能自己设 Location：窗口一动会回灌 WM_MOUSEMOVE，位置越跑越远（实测能飞出去 1400px）。
            // 右侧滚动条得保留给程序自己处理，不然拖不动内容。
            int sbLeft = ClientSize.Width - ScrollBarRightGap;
            if (_maxOffset > 1 && p.X >= sbLeft - 14) { base.WndProc(ref m); return; }

            m.Result = (IntPtr)HTCAPTION;
            return;
        }

        base.WndProc(ref m);
    }
}