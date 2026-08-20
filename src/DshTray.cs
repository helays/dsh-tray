// ============================================================================
// DshTray.cs — DeepSeek Harness 托盘宿主
//
// 用 NotifyIcon 在系统托盘显示鲸鱼图标。启动时拉起 harness(Web GUI)。
// 托盘菜单:
//   - 双击 / "打开浏览器" : 打开 Web GUI
//   - "重启 Harness"      : 停止旧实例并自启新实例
//   - "检测更新"          : 对比已装与 npm 最新 @deepseek-ai/dsh 版本
//   - "退出"               : 终止 harness 及其子进程, 然后退出自身
//
// 用法:
//   DshTray.exe [--port 3080] [--bin <bin>.js] [--url http://...]
//   DshTray.exe --help
//
// 注意: 使用了 WinForms。通过 Add-Type 编译为无窗口(system-tray only)程序。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static Process _harness;
    static NotifyIcon _tray;
    static ToolStripMenuItem _statusItem;
    static string _url = "http://127.0.0.1:3080";
    static string _iconPath;   // 图标文件路径(可空, 用系统默认)
    static int _port = 3080;
    static string _script;     // 解析出的 bin.js 路径
    static bool _ownsHarness;  // 本进程是否自启了 harness(退出时才杀掉)
    static SynchronizationContext _uiSync; // 主线程同步上下文, 用于跨线程弹窗
    static bool _noOpen;       // 为 true 时启动后不自动打开浏览器

    [STAThread]
    public static int Main(string[] args)
    {
        int port = 3080;
        string bin = null;
        string icon = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":   if (i + 1 < args.Length) { port = int.Parse(args[++i]); } break;
                case "--bin":    if (i + 1 < args.Length) { bin = args[++i]; } break;
                case "--icon":   if (i + 1 < args.Length) { icon = args[++i]; } break;
                case "--url":    if (i + 1 < args.Length) { _url = args[++i]; } break;
                case "--no-open": _noOpen = true; break;
                case "--help":
                case "-h":
                    Console.WriteLine("DshTray: DeepSeek Harness tray host");
                    Console.WriteLine("  --port <int>   Web port (default 3080)");
                    Console.WriteLine("  --bin <path>   dsh lib/bin.js launcher");
                    Console.WriteLine("  --icon <path>  tray icon .ico/.png");
                    Console.WriteLine("  --url <url>    browser URL");
                    Console.WriteLine("  --no-open      do not auto-open the browser on start");
                    return 0;
            }
        }

        try { SetProcessDPIAware(); } catch { }

        // 仅允许单实例
        bool ok;
        using (new Mutex(true, "DshTray.Singleton", out ok))
        {
            if (!ok)
            {
                MessageBox.Show("DshTray 已在运行。", "DshTray",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            _iconPath = icon;
            _port = port;

            // 若目标端口已有服务在监听, 则不再自启一个实例, 只挂托盘指向现有服务
            if (IsPortListening(port))
            {
                _ownsHarness = false;
            }
            else
            {
                _harness = StartHarness(bin, port);
                if (_harness == null)
                {
                    MessageBox.Show("无法启动 DeepSeek Harness。", "DshTray",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                _ownsHarness = true;
            }

            using (var form = new HiddenForm())
            {
                _uiSync = SynchronizationContext.Current;   // 捕获主线程同步上下文
                SetupTray(port);

                // 自启服务成功后, 等网页就绪再自动打开默认浏览器(可用 --no-open 关闭)
                if (_ownsHarness && !_noOpen)
                    AutoOpenBrowserAsync();

                Application.Run(form);   // forks off a message loop; _tray keeps it alive
            }
        }

        return 0;
    }

    // 后台等待 URL 连通后打开默认浏览器(避免 node 尚未 bind 就打开导致空白页)
    static void AutoOpenBrowserAsync()
    {
        var worker = new Thread(() =>
        {
            for (int i = 0; i < 20; i++)   // 最多约 10 秒
            {
                try
                {
                    using (var tcp = new System.Net.Sockets.TcpClient())
                    {
                        var ar = tcp.BeginConnect("127.0.0.1", _port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(500) && tcp.Connected)
                        {
                            OpenBrowser();
                            return;
                        }
                    }
                }
                catch { }
                Thread.Sleep(500);
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // 命令行参数引用: 用于构造 ProcessStartInfo.Arguments
    static string Q(string s) { return s.IndexOf(' ') < 0 ? s : "\"" + s + "\""; }

    // 从子进程的 PATH 中剔除 Python 的 nodejs_wheel(Scripts) 目录, 防止其 npm/node 抢占,
    // 确保 npm 脚本内部再调用 node 时也落到真实的系统 Node。
    static void ResetProcessEnv(ProcessStartInfo psi)
    {
        try
        {
            string path = psi.Environment["Path"];
            var parts = new List<string>();
            foreach (var dir in path.Split(';'))
            {
                if (!string.IsNullOrWhiteSpace(dir) &&
                    dir.IndexOf("Python", StringComparison.OrdinalIgnoreCase) < 0)
                    parts.Add(dir);
            }
            psi.Environment["Path"] = string.Join(";", parts);
        }
        catch { }
    }

    static Process StartHarness(string bin, int port)
    {
        // 定位 dsh 的 bin.js; --bin 优先, 否则在 npx 缓存里找
        string script = bin;
        if (string.IsNullOrEmpty(script))
        {
            script = ResolveDshBin();
            if (script == null)
            {
                MessageBox.Show("未找到 @deepseek-ai/dsh 的 bin.js。请通过 --bin 指定。", "DshTray",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }
        _script = script;

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(script))
        };
        psi.Arguments = Q(script) + " web --port " + port;

        var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Console.WriteLine("[DshTray] harness started pid=" + p.Id);
            return p;
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动 node 失败: " + ex.Message, "DshTray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    static string ResolveDshBin()
    {
        // 1) 优先用 npm 全局安装目录(npm install -g 的目标), 升级后能立即用新版
        string globalCandidate = Path.Combine(GetGlobalPrefix(), @"node_modules\@deepseek-ai\dsh\lib\bin.js");
        if (File.Exists(globalCandidate)) return globalCandidate;

        // 2) 其次遍历 npx 缓存目录
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] roots = {
            Path.Combine(local, "npm-cache", "_npx"),
            Path.Combine(home, "AppData", "Local", "npm-cache", "_npx")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var cand = Path.Combine(dir, @"node_modules\@deepseek-ai\dsh\lib\bin.js");
                if (File.Exists(cand)) return cand;
            }
        }
        return null;
    }

    // 获取 npm 全局前缀目录(如 C:\Users\<user>\AppData\Roaming\npm)。
    // 注意: 不能直接依赖 `npm prefix -g` —— PATH 可能被 Python 的 nodejs_wheel
    // (DeepSeek Harness 运行时自带) 接管, 导致返回 Harness 自身用的目录而非系统全局。
    // 因此在 Windows 上优先确定性地采用 `%APPDATA%\npm`(系统 npmrc 默认 prefix)。
    static string GetGlobalPrefix()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appdataNpm = Path.Combine(home, "AppData", "Roaming", "npm");

        // 1) 优先: 系统 npm 全局根若确实装有 dsh 包, 直接用它(不受 PATH 污染)
        if (File.Exists(Path.Combine(appdataNpm, "node_modules", "@deepseek-ai", "dsh", "package.json")))
            return appdataNpm;

        // 2) 其次: `%APPDATA%\npm` 目录存在也优先(标准全局根; 未装 dsh 会退回 npx 缓存)
        if (Directory.Exists(appdataNpm))
            return appdataNpm;

        // 3) 回退: 动态解析。用解析出的系统全局 npm.cmd 以减小 PATH 污染风险
        {
            string prefix = TryNpmPrefix(ResolveNpmCmd());
            if (prefix != null) return prefix;
        }

        // 4) 兜底: 常见默认路径
        return appdataNpm;
    }

    // 定位系统全局 npm CLI(cmd)。关键危险: 当前 PATH 可能被 Python 的 nodejs_wheel
    // (DeepSeek Harness 运行时自带) 提供的 npm.exe 接管, 其 global prefix 指向
    // site-packages\nodejs_wheel, 导致 `npm install -g` 装错位置。因此显式回退到
    // 真实 Node 所在目录的 npm.cmd, 并配合 --prefix 强制写往系统全局目录。
    static string ResolveNpmCmd()
    {
        // 1) 优先: 与真实 node.exe 相邻的系统 npm.cmd
        foreach (string root in new[] {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
            Environment.GetEnvironmentVariable("ProgramW6432") ?? ""
        })
        {
            string npm = Path.Combine(root, "nodejs", "npm.cmd");
            if (File.Exists(npm)) return npm;
        }

        // 2) 次选: 从 PATH 找首个非 Python 的 npm.cmd(避开 nodejs_wheel)
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (dir.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            string npm = Path.Combine(dir.Trim('"'), "npm.cmd");
            if (File.Exists(npm)) return npm;
        }

        // 3) 兜底: 裸 npm(由 PATH 决定)
        return "npm";
    }

    static string TryNpmPrefix(string file)
    {
        bool isBare = (file == "npm");
        if (!isBare && !File.Exists(file)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = "prefix -g",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (outp.Length > 0) return outp.Split('\n')[0].Trim();
            }
        }
        catch { }
        return null;
    }

    static void SetupTray(int port)
    {
        _tray = new NotifyIcon();
        _tray.Text = "DeepSeek Harness (Web)";
        _tray.Icon = LoadIcon();
        _tray.Visible = true;

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Harness: 正在运行");
        _statusItem.Enabled = false;

        var open = new ToolStripMenuItem("打开浏览器");
        open.Click += (s, e) => OpenBrowser();

        var start = new ToolStripMenuItem("重启 Harness");
        start.Click += (s, e) => Restart();

        var checkUpd = new ToolStripMenuItem("检测更新");
        checkUpd.Click += (s, e) => CheckUpdatesAsync();

        var sep = new ToolStripSeparator();

        var about = new ToolStripMenuItem("关于");
        about.Click += (s, e) => MessageBox.Show(
            "DeepSeek Harness 托盘宿主\n双击图标打开浏览器;\n\"退出\"将停止 Harness 服务。",
            "DshTray", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var quit = new ToolStripMenuItem("退出");
        quit.Click += (s, e) => Quit();

        menu.Items.Add(_statusItem);
        menu.Items.Add(open);
        menu.Items.Add(start);
        menu.Items.Add(checkUpd);
        menu.Items.Add(sep);
        menu.Items.Add(about);
        menu.Items.Add(quit);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => OpenBrowser();
        _tray.MouseUp += OnTrayMouseUp;
    }

    const string RES_ICON = "DeepSeekWhale.ico";   // 随 exe 嵌入的资源名

    static Icon LoadIcon()
    {
        // 1) 优先: exe 内嵌资源中的鲸鱼图标(自包含, 不依赖外部文件)
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(RES_ICON, StringComparison.OrdinalIgnoreCase)) continue;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    using (var ico = new Icon(s, 32, 32))
                        return (Icon)ico.Clone();
                }
            }
        }
        catch { }

        // 2) 回退: 外部 --icon 指定的图标文件
        try
        {
            if (!string.IsNullOrEmpty(_iconPath) && File.Exists(_iconPath))
            {
                using (var ico = new Icon(_iconPath, 32, 32))
                    return (Icon)ico.Clone();
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    static void OnTrayMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) OpenBrowser();
    }

    static void OpenBrowser()
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打不开浏览器: " + ex.Message, "DshTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // “检测更新”: 在后台对比已装的 @deepseek-ai/dsh 与 npm 上的最新版本。
    // 网络等待放后台线程; 结果通过主线程同步上下文回 UI 弹窗, 不阻塞托盘。
    static void CheckUpdatesAsync()
    {
        // 即时反馈: 先在托盘弹气泡表示“已开始”, 避免看起来没反应
        ShowBalloon("检测更新", "正在查询 @deepseek-ai/dsh 最新版本，请稍候…", 1500);

        string localDir = ResolveDshPackageDir();   // 已装 dsh 包目录(可空)
        var worker = new Thread(() =>
        {
            string latest = null;
            string err = null;
            try
            {
                string realNpm = ResolveNpmCmd();   // 避开 nodejs_wheel 的 npm
                var psi = new ProcessStartInfo
                {
                    FileName = realNpm,
                    Arguments = "view @deepseek-ai/dsh version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                ResetProcessEnv(psi);   // 剔除 nodejs_wheel 对 PATH 的污染
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd().Trim();
                    string stderr = p.StandardError.ReadToEnd().Trim();
                    p.WaitForExit(30000);
                    if (stdout.Length > 0) latest = stdout.Trim().Split('\n')[0].Trim();
                    else if (stderr.Length > 0) err = stderr.Trim();
                }
            }
            catch (Exception ex) { err = ex.Message; }

            string installed = null;
            if (localDir != null)
            {
                try
                {
                    var pj = Path.Combine(localDir, "package.json");
                    if (File.Exists(pj))
                    {
                        foreach (var line in File.ReadAllLines(pj))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("\"version\"")) { installed = t.Substring(t.IndexOf(':') + 1).Trim().Trim(',', '"', ' '); break; }
                        }
                    }
                }
                catch { }
            }

            var result = BuildUpdateMessage(latest, installed, err);
            // 通过主线程同步上下文回 UI 线程弹窗; 若不可用则退化为托盘气泡兜底
            if (_uiSync != null)
            {
                try
                {
                    _uiSync.Post(_ => ShowUpdateResult(result.Item1, result.Item2, result.Item3), null);
                    return;
                }
                catch { }
            }
            ShowBalloon(result.Item2, result.Item1, 5000);
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // 在 UI 线程展示检测结果; 若有新版可选, 弹“是/否”确认后触发自动升级
    static void ShowUpdateResult(string msg, string title, bool hasUpdate)
    {
        if (!hasUpdate)
        {
            MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var choice = MessageBox.Show(msg + "\n\n是否立即自动升级？",
            title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (choice == DialogResult.Yes) UpgradeAsync();
    }

    // 自动升级: 后台执行 npm install -g @deepseek-ai/dsh, 完成后重启 harness 使新版本生效
    // 升级前先停掉占用 _port 的 dsh web 服务(无论是否本托盘自启), 释放文件锁以便覆盖安装。
    static void UpgradeAsync()
    {
        ShowBalloon("检测更新", "正在升级 @deepseek-ai/dsh，请稍候…", 2000);
        string realNpm = ResolveNpmCmd();              // 避开 nodejs_wheel 的 npm
        string globalPrefix = GetGlobalPrefix();       // 强制安装到系统全局目录
        var psi = new ProcessStartInfo
        {
            FileName = realNpm,
            Arguments = Q("install") + " -g @deepseek-ai/dsh --prefix " + Q(globalPrefix),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ResetProcessEnv(psi);   // 清掉 nodejs_wheel 对 PATH 的污染, 让子进程仍可信解析 npm
        var worker = new Thread(() =>
        {
            string err = null;
            bool ok = false;

            // 1) 升级前停掉 dsh web 服务, 释放包目录文件锁
            int wasPort = _port;
            KillPortOwner(wasPort);

            try
            {
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd().Trim();
                    string stderr = p.StandardError.ReadToEnd().Trim();
                    p.WaitForExit(60000);
                    ok = p.ExitCode == 0;
                    if (!ok)
                        err = (stderr.Length > 0 ? stderr : stdout);
                    else if (stdout.Length > 0 && stdout.IndexOf("added", StringComparison.OrdinalIgnoreCase) >= 0)
                    { /* 正常安装 */ }
                }
            }
            catch (Exception ex) { err = ex.Message; }

            if (!ok)
            {
                // 升级失败: 若已停过服务则重启回老版本
                _uiSync.Post(_ =>
                {
                    try { StartHarness(null, wasPort); } catch { }
                    string em = (err != null ? err : "");
                    MessageBox.Show("升级失败。\n\nnpm install -g @deepseek-ai/dsh\n\n" +
                        (em.Length > 300 ? em.Substring(0, 300) : em), "检测更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }, null);
                return;
            }

            // 2) 升级成功, 重启 harness(总是自启, 接管模式也在升级后转为自启)
            if (_uiSync != null)
            {
                try
                {
                    _uiSync.Post(_ =>
                    {
                        try
                        {
                            _harness = StartHarness(null, wasPort);
                            _ownsHarness = _harness != null;
                        }
                        catch { }
                        MessageBox.Show("升级完成，Harness 已用新版本重启。", "检测更新",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }, null);
                    return;
                }
                catch { }
            }
            try { _harness = StartHarness(null, wasPort); _ownsHarness = _harness != null; } catch { }
            ShowBalloon("检测更新", "升级完成，Harness 已用新版本重启。", 5000);
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // 兜底/即时反馈: 托盘气泡(不依赖 UI 线程 Invoke, 托盘应用更合适)
    static void ShowBalloon(string title, string text, int timeout)
    {
        if (_tray == null) return;
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.ShowBalloonTip(timeout);
        }
        catch { }
    }

    // 在 UI 线程弹消息框(gui 线程由 Post 保证)
    static void ShowMessageBox(string msg, string title)
    {
        MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // 根据检测结果拼装提示内容。返回 (message, title, hasUpdate)。
    // latest = npm view 的原始输出(registry 最新版), installed = 本机已装版本。
    static Tuple<string, string, bool> BuildUpdateMessage(string latest, string installed, string err)
    {
        if (latest == null)
            return Tuple.Create("无法获取 registry 最新版本。\n\n命令: npm view @deepseek-ai/dsh version\n\n" +
                (err != null ? "错误输出:\n" + err : "请检查网络是否可用。"), "检测更新 - 失败", false);
        if (installed == null)
            return Tuple.Create("registry 最新版本: " + latest + "\n本机已装版本: 未知(未定位到本地 dsh 包)。",
                "检测更新", false);
        int cmp = CompareVersions(installed, latest);
        if (cmp < 0)
            return Tuple.Create("发现新版本!\n\n  registry 最新版本: " + latest +
                "   (npm view 输出)\n  本机已装版本:     " + installed +
                "\n\n点击“是”将自动执行升级并重启 Harness。",
                "检测更新 - 有新版本", true);
        if (cmp == 0)
            return Tuple.Create("registry 最新版本: " + latest +
                "   (npm view 输出)\n本机已装版本:     " + installed + "\n\n已是最新版本。",
                "检测更新", false);
        return Tuple.Create("registry 最新版本: " + latest +
            "\n本机已装版本:     " + installed + "\n\n本机版本高于 registry 最新(可能是预发布版)。", "检测更新", false);
    }

    // 完整 semver 比较。返回 <0 表示 a<b, ==0 表示相等, >0 表示 a>b。
    // 支持 pre-release: 0.1.0-rc.6 < 0.1.0-rc.7 < 0.1.0(< 正式版)。
    static int CompareVersions(string a, string b)
    {
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);

        // 1) 先比较主版本号(major.minor.patch...)
        int core = CompareCore(pa.Core, pb.Core);
        if (core != 0) return core;

        // 2) 主版本相等则比较 pre-release
        return ComparePrerelease(pa.Pre, pb.Pre);
    }

    struct VersionParts { public Version Core; public string[] Pre; }

    static VersionParts ParseVersionParts(string v)
    {
        var p = new VersionParts();
        string core = v;
        string pre = null;
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            core = v.Substring(0, dash);
            pre = v.Substring(dash + 1);
        }
        Version cv;
        if (!Version.TryParse(core.TrimEnd('.'), out cv)) cv = new Version(0, 0, 0);
        p.Core = cv;
        if (!string.IsNullOrEmpty(pre)) p.Pre = pre.Split('.');
        else p.Pre = null;   // null 表示无 pre-release(即正式版)
        return p;
    }

    static int CompareCore(Version a, Version b)
    {
        // 逐段比较 major.minor.build.revision, 缺失按 0 处理
        int[] aa = { Math.Max(0, a.Major), Math.Max(0, a.Minor), Math.Max(0, a.Build), Math.Max(0, a.Revision) };
        int[] bb = { Math.Max(0, b.Major), Math.Max(0, b.Minor), Math.Max(0, b.Build), Math.Max(0, b.Revision) };
        for (int i = 0; i < 4; i++)
        {
            if (aa[i] != bb[i]) return aa[i].CompareTo(bb[i]);
        }
        return 0;
    }

    // semver pre-release 比较: 无 pre > 有 pre; 逐段比较, 数字段按数值、字母段按字典序, 数字段 < 字母段。
    static int ComparePrerelease(string[] a, string[] b)
    {
        bool aPre = a != null;
        bool bPre = b != null;
        if (!aPre && !bPre) return 0;
        if (!aPre) return 1;   // a 是正式版 > b(pre)
        if (!bPre) return -1;  // a(pre) < b 正式版

        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int r = CompareIdentifier(a[i], b[i]);
            if (r != 0) return r;
        }
        // 前缀相同时, 更长的更大
        return a.Length.CompareTo(b.Length);
    }

    // semver 单个 pre-release 标识符比较
    static int CompareIdentifier(string x, string y)
    {
        int xi, yi;
        bool xnum = int.TryParse(x, out xi);
        bool ynum = int.TryParse(y, out yi);
        if (xnum && ynum) return xi.CompareTo(yi);
        if (xnum) return -1;   // 数字段 < 字母段
        if (ynum) return 1;
        return string.CompareOrdinal(x, y);
    }

    // 定位已装 @deepseek-ai/dsh 的包目录(含 package.json)。
    static string ResolveDshPackageDir()
    {
        // 1) 优先 npm 全局安装目录(与 ResolveDshBin 保持一致, 升级后读到此为最先)
        string global = Path.Combine(GetGlobalPrefix(), @"node_modules\@deepseek-ai\dsh");
        if (File.Exists(Path.Combine(global, "package.json"))) return global;

        // 2) 其次用已解析的 bin.js(它在 node_modules/@deepseek-ai/dsh/lib/bin.js)
        if (!string.IsNullOrEmpty(_script))
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(_script)); // .../dsh
            if (dir != null && File.Exists(Path.Combine(dir, "package.json"))) return dir;
        }
        // 3) 最后在 npx 缓存里找
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] roots = {
            Path.Combine(local, "npm-cache", "_npx"),
            Path.Combine(home, "AppData", "Local", "npm-cache", "_npx")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var cand = Path.Combine(dir, @"node_modules\@deepseek-ai\dsh");
                if (File.Exists(Path.Combine(cand, "package.json"))) return cand;
            }
        }
        return null;
    }

    static void Restart()
    {
        if (_ownsHarness) KillHarness();
        _harness = StartHarness(null, _port);
        _ownsHarness = true;
        if (_statusItem != null) _statusItem.Text = "Harness: 正在运行";
    }

    static void Quit()
    {
        if (_ownsHarness) KillHarness();   // 只杀掉本进程自启的实例
        _tray.Visible = false;
        Application.Exit();
    }

    static bool IsPortListening(int port)
    {
        try
        {
            // 顶多两次重试, 避免启动竞态
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                var ar = tcp.BeginConnect("127.0.0.1", port, null, null);
                return ar.AsyncWaitHandle.WaitOne(400) && tcp.Connected;
            }
        }
        catch { return false; }
    }

    static void KillHarness()
    {
        if (_harness == null || _harness.HasExited) return;
        try
        {
            // 杀进程树: node 可能 fork 子进程
            using (var killer = new Process())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "/PID " + _harness.Id + " /T /F"
                };
                killer.StartInfo = psi;
                killer.Start();
                killer.WaitForExit(3000);
            }
        }
        catch { }
        try { if (!_harness.HasExited) _harness.Kill(); } catch { }
    }

    // 按端口杀掉占用该端口的进程(含进程树)。升级前必须停掉 dsh web 服务,
    // 释放文件锁以便 npm 覆盖安装; 无论该服务是否由本托盘自启都要停。
    static void KillPortOwner(int port)
    {
        // 1) 优先杀掉本托盘自启的实例(直接、可靠)
        KillHarness();
        _harness = null;

        // 2) 兜底: 用 netstat 找到真正监听该端口的 PID 并整树杀掉
        //    (覆盖“接管外部实例”等 _ownsHarness 为 false 的情况)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string portTag = ":" + port;
                var pidSet = new HashSet<int>();
                foreach (var line in outp.Split('\n'))
                {
                    // 只认 LISTENING 的本地监听行
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (line.IndexOf(portTag, StringComparison.Ordinal) < 0) continue;
                    var tok = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length < 5) continue;
                    int pid;
                    if (int.TryParse(tok[tok.Length - 1], out pid) && pid > 0 && pid != Process.GetCurrentProcess().Id)
                        pidSet.Add(pid);
                }
                foreach (var pid in pidSet)
                {
                    try
                    {
                        using (var killer = new Process())
                        {
                            ProcessStartInfo ki = new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                Arguments = "/PID " + pid + " /T /F"
                            };
                            killer.StartInfo = ki;
                            killer.Start();
                            killer.WaitForExit(3000);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3) 等待端口真正释放, 避免 npm 安装仍撞上文件锁
        for (int i = 0; i < 20; i++)
        {
            if (!IsPortListening(port)) break;
            Thread.Sleep(200);
        }
    }

    // 隐藏窗体: 让消息循环存在且进程可见于任务管理器, 但不显示窗口
    sealed class HiddenForm : Form
    {
        public HiddenForm()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            FormBorderStyle = FormBorderStyle.None;
            // 放进托盘后最小化到托盘不带任务栏按钮
        }
        protected override void OnShown(EventArgs e) { Visible = false; base.OnShown(e); }
        protected override void SetVisibleCore(bool value) { if (value && WindowState != FormWindowState.Normal) value = false; base.SetVisibleCore(value); }
    }
}
