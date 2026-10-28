using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Kotoba.Models;
using Kotoba.Services;

namespace Kotoba;

internal static class Program
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDC, uint nFlags);

    private static Mutex? _singleton;

    /// <summary>已有实例在跑：把它的窗口叫出来，叫不出来（最小化到托盘）就提示</summary>
    private static void ActivateRunningInstance()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id) continue;

            var h = FindVisibleWindowOf(p.Id);
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9);            // SW_RESTORE
                SetForegroundWindow(h);
                return;
            }
        }

        MessageBox.Show(
            "Kotoba 已经在运行了。\n\n请从右下角托盘图标打开，或右键托盘图标选「显示主界面」。",
            "Kotoba", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static IntPtr FindVisibleWindowOf(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    [STAThread]
    /// <summary>崩溃兜底：日志写到 %TEMP%\kotoba-crash-*.log，并弹窗告诉用户文件在哪</summary>
    private static void ReportCrash(Exception? ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"kotoba-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("异常: " + (ex?.ToString() ?? "(未知)"));
            File.WriteAllText(path, sb.ToString());

            MessageBox.Show("Kotoba 遇到异常，已记录日志：\n" + path + "\n\n" + ex?.Message,
                "Kotoba 出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    private static void Main(string[] args)
    {
        // 必须在创建任何窗口前声明 DPI 感知，否则 150% 缩放下窗口会缩成 2/3
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* 老系统忽略 */ }

        ApplicationConfiguration.Initialize();
        UsageTracker.Load();

        // 全局异常兜底：任何未捕获异常都写一份日志，别让进程无声消失
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // 单实例：调试参数（--shot / --selftest / --demo）不参与，方便反复跑测试
        bool debugMode = args.Any(a => a.StartsWith("--shot", StringComparison.Ordinal))
                      || args.Contains("--selftest") || args.Contains("--demo");

        if (!debugMode)
        {
            _singleton = new Mutex(true, "Kotoba_SingleInstance_v1", out bool isNew);
            if (!isNew)
            {
                ActivateRunningInstance();
                return;
            }
        }

        Theme.Dark = args.Contains("--dark");
        string? shotPath = null;
        string? selfTest = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--shot") shotPath = args[i + 1];
            if (args[i] == "--selftest") selfTest = args[i + 1];
        }

        if (selfTest != null)
        {
            RunSelfTest(selfTest);
            return;
        }

        string? demo = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--demo") demo = args[i + 1];

        if (demo != null)
        {
            RunDemo(demo);
            return;
        }

        string? shotSettings = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot-settings") shotSettings = args[i + 1];

        if (shotSettings != null)
        {
            RunShotSettings(shotSettings);
            return;
        }

        string? shotContext = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot-context") shotContext = args[i + 1];

        if (shotContext != null)
        {
            var cfgC = ConfigStore.Load();
            var profC = cfgC.EnsureProfile();
            var histC = new List<(string Src, string Dst)>
            {
                ("「勇者よ、よくぞ参った。」", "「勇者啊，你来得正好。」"),
                ("「この塔の頂には魔王が待っておる。」", "「这座塔的顶端，魔王正等着你呢。」"),
                ("ルゥ：「わたしはルゥ。あなたの案内役の妖精だよ。」", "露：「我是露。是你的向导妖精哦。」")
            };
            var cf = new Forms.ContextForm(profC, cfgC, histC);
            var hostC = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
                Size = new Size(1, 1),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000)
            };
            var tc = new System.Windows.Forms.Timer { Interval = 1200 };
            tc.Tick += (_, _) =>
            {
                tc.Stop();
                try
                {
                    using var bmp = new Bitmap(cf.Width, cf.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var pos = cf.PointToScreen(Point.Empty);
                        g.CopyFromScreen(pos, Point.Empty, cf.Size);
                    }
                    bmp.Save(shotContext);
                }
                catch (Exception ex) { File.WriteAllText(shotContext + ".err.txt", ex.ToString()); }
                Application.Exit();
            };
            cf.Show();
            tc.Start();
            Application.Run(hostC);
            return;
        }

        string? shotManage = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot-manage") shotManage = args[i + 1];

        if (shotManage != null)
        {
            var cfgM = ConfigStore.Load();
            var mf = new Forms.PresetManagerForm(cfgM);
            var hostM = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Opacity = 0, Size = new Size(1, 1), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
            var tm = new System.Windows.Forms.Timer { Interval = 1200 };
            tm.Tick += (_, _) =>
            {
                tm.Stop();
                try
                {
                    using var bmp = new Bitmap(mf.Width, mf.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var pos = mf.PointToScreen(Point.Empty);
                        g.CopyFromScreen(pos, Point.Empty, mf.Size);
                    }
                    bmp.Save(shotManage);
                }
                catch (Exception ex) { File.WriteAllText(shotManage + ".err.txt", ex.ToString()); }
                Application.Exit();
            };
            mf.Show();
            tm.Start();
            Application.Run(hostM);
            return;
        }

        string? shotMemory = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot-memory") shotMemory = args[i + 1];

        if (shotMemory != null)
        {
            var cfg0 = ConfigStore.Load();
            var prof0 = cfg0.EnsureProfile();
            var hist = new List<(string Src, string Dst)>
            {
                ("「勇者よ、よくそぞ参った。」", "「勇者啊，你来得正好。」"),
                ("「この塔の頂には魔王が待っておる。」", "「这座塔的顶端，魔王正等着你呢。」")
            };
            var mf = new Forms.MemoryForm(prof0.Name, "「勇者」是主人公，来自现代日本的少年`n---`n妖精「露」是向导，自称わたし，语气轻快`n---`n舞台设定在名为艾尔塞利亚的异世界草原");
            var hostM = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
                Size = new Size(1, 1),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000)
            };
            var tm = new System.Windows.Forms.Timer { Interval = 1200 };
            tm.Tick += (_, _) =>
            {
                tm.Stop();
                try
                {
                    using var bmp = new Bitmap(mf.Width, mf.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var pos = mf.PointToScreen(Point.Empty);
                        g.CopyFromScreen(pos, Point.Empty, mf.Size);
                    }
                    bmp.Save(shotMemory);
                }
                catch (Exception ex) { File.WriteAllText(shotMemory + ".err.txt", ex.ToString()); }
                Application.Exit();
            };
            mf.Show();
            tm.Start();
            Application.Run(hostM);
            return;
        }

        Application.Run(new Forms.MainForm(shotPath));
    }

    /// <summary>调试用：只显示两个浮窗并截图，不调 API</summary>
    private static void RunDemo(string outPrefix)
    {
        var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000)
        };

        var cap = new Forms.CaptureForm("Demo 配置");
        cap.ApplyBounds(new Rectangle(100, 100, 1000, 340), false);
        cap.Show();

        var ov = new Forms.OverlayForm();
        ov.Bounds = new Rectangle(100, 480, 1000, 150);
        ov.Show();
        try
        {
            ov.ShowPairs(new List<(string Src, string Dst)>
            {
                ("「この塔の頂には魔王が待っておる。」", "「这座塔的顶端，魔王正等着你呢。」"),
                ("スペシャルウィーク「今日のレースも全力ですわ！」", "特别周：「今天的比赛也要全力以赴哦！」")
            },
            new List<(string Src, string Dst)>
            {
                ("「勇者よ、よくぞ参った。」", "「勇者啊，你来得正好。」")
            },
            false, true, 16, 220, 144);
            ov.SetStatus("翻译中…");
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPrefix + "-showpairs-error.txt", ex.ToString());
        }

        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        int step = 0;
        t.Tick += (_, _) =>
        {
            step++;
            try
            {
                if (step == 1)
                {
                    // 顶部
                    ov.DebugScrollTo(0);
                    SnapWindow(cap, outPrefix + "-capture.png");
                    SnapWindow(ov, outPrefix + "-overlay-top.png");

                    // 底部
                    ov.DebugScrollTo(99999);
                    SnapWindow(ov, outPrefix + "-overlay-bottom.png");

                    cap.Bounds = new Rectangle(100, 100, 760, 280);
                    ov.Bounds = new Rectangle(100, 460, 700, 420);
                }
                else
                {
                    t.Stop();
                    SnapWindow(cap, outPrefix + "-capture-resized.png");

                    // 全屏截图：确认窗口在屏幕上的真实位置和渲染结果
                    try
                    {
                        var vs = SystemInformation.VirtualScreen;
                        using var full = new Bitmap(Math.Min(vs.Width, 2560), Math.Min(vs.Height, 1600));
                        using (var fg = Graphics.FromImage(full))
                            fg.CopyFromScreen(vs.X, vs.Y, 0, 0, full.Size);
                        full.Save(outPrefix + "-fullscreen.png");
                    }
                    catch (Exception ex) { File.WriteAllText(outPrefix + "-fullscreen-error.txt", ex.ToString()); }

                    SnapWindow(ov, outPrefix + "-overlay-resized.png");
                    File.WriteAllText(outPrefix + "-diag.txt",
                        "cap client=" + cap.ClientSize + "  bounds=" + cap.Bounds + "\n" +
                        "ov  client=" + ov.ClientSize + "  bounds=" + ov.Bounds + "\n" +
                        "layered: " + Forms.OverlayForm.LastRenderDiagnostic + "\nwheelHook: 已移除\n");
                    Application.Exit();
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(outPrefix + "-demo-error.txt", ex.ToString());
                Application.Exit();
            }
        };
        t.Start();

        Application.Run(host);
    }

    private static void RunShotSettings(string path)
    {
        var cfg = ConfigStore.Load();
        var prof = cfg.EnsureProfile();
        var f = new Forms.SettingsForm(cfg, prof);
        var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000)
        };

        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                using var bmp = new Bitmap(f.Width, f.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    var pos = f.PointToScreen(Point.Empty);
                    g.CopyFromScreen(pos, Point.Empty, f.Size);
                }
                bmp.Save(path);
            }
            catch (Exception ex) { File.WriteAllText(path + ".err.txt", ex.ToString()); }
            Application.Exit();
        };

        f.Show();
        t.Start();
        Application.Run(host);
    }

    private static void SnapWindow(Form f, string path)
    {
        // 必须用 PointToScreen 取屏幕物理坐标：
        // 直接用 Form.Location 在 DPI 缩放下会取错位置，截到一张完全无关的画面
        using var bmp = new Bitmap(f.Width, f.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var pos = f.PointToScreen(Point.Empty);
            g.CopyFromScreen(pos, Point.Empty, f.Size);
        }
        bmp.Save(path);
    }

    /// <summary>端到端自测：一张图 → 转写 → 翻译，结果写入同名 .txt</summary>
    private static void RunSelfTest(string imgPath)
    {
        var outPath = Path.ChangeExtension(imgPath, ".result.txt");
        try
        {
            var cfg = ConfigStore.Load();
            var prof = cfg.EnsureProfile();

            if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                File.WriteAllText(outPath, "缺少 API Key");
                return;
            }

            var pipeline = new TranslationPipeline();
            using var bmp = new Bitmap(imgPath);

            // 临时注入术语表（不写回配置），验证是否真的拼进提示词
            cfg.ThinkingEnabled = true;    // 模拟实际：翻译阶段开深度思考
            prof.Glossary = "スペシャルウィーク=特别周（Special Week）\nやくそう=回复药草";

            var testHist = new List<(string Src, string Dst)> { ("「おはよう、旅人さん！」", "「早上好，旅行者先生！」"), ("「ここはエルセリアだ。」", "「这里是艾尔塞利亚。」") };
            var r1 = pipeline.RunSegmentedAsync(bmp, prof, cfg, testHist, CancellationToken.None).GetAwaiter().GetResult();
            var r2 = pipeline.RunSegmentedAsync(bmp, prof, cfg, null, CancellationToken.None).GetAwaiter().GetResult();

            // 流式测试：统计回调次数，>1 说明真的是逐段到达
            int chunks = 0;
            var swStream = System.Diagnostics.Stopwatch.StartNew();
            string firstChunkAt = "-";
            var sr = pipeline.RunStreamAsync(bmp, prof, cfg, null,
                _ =>
                {
                    if (Interlocked.Increment(ref chunks) == 1)
                        firstChunkAt = swStream.ElapsedMilliseconds + "ms";
                },
                CancellationToken.None).GetAwaiter().GetResult();
            swStream.Stop();

            File.WriteAllText(outPath,
                $"model={prof.Model}\n" +
                $"img={bmp.Width}x{bmp.Height}\n" +
                $"pairs: first cache={r1.FromCache} ms={r1.ElapsedMs} n={r1.Pairs.Count} / second cache={r2.FromCache} ms={r2.ElapsedMs}\n" +
                $"stream: cache={sr.FromCache} ms={sr.ElapsedMs} chunks={chunks} firstChunk={firstChunkAt}\n" +
                $"\n--- 逐句结果 ---\n" +
                string.Join("\n", r1.Pairs.Select((p, i) => $"[{i + 1}] {p.Dst}\n    {p.Src}")) +
                $"\n\n--- 流式译文 ---\n{sr.Target}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, "ERROR: " + ex.Message + "\n" + ex);
        }
    }
}
