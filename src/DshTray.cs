// ============================================================================
// DshTray.cs — DeepSeek Harness 托盘宿主
//
// 用 NotifyIcon 在系统托盘显示鲸鱼图标。启动时拉起 harness(Web GUI)。
// 托盘菜单:
//   - 双击 / "打开浏览器" : 打开 Web GUI
//   - "退出"               : 终止 harness 及其子进程, 然后退出自身
//
// 用法:
//   DshTray.exe [--port 3080] [--bin <bin>.js] [--url http://...]
//   DshTray.exe --help
//
// 注意: 使用了 WinForms。通过 Add-Type 编译为无窗口(system-tray only)程序。
// ============================================================================
using System;
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
                case "--help":
                case "-h":
                    Console.WriteLine("DshTray: DeepSeek Harness tray host");
                    Console.WriteLine("  --port <int>   Web port (default 3080)");
                    Console.WriteLine("  --bin <path>   dsh lib/bin.js launcher");
                    Console.WriteLine("  --icon <path>  tray icon .ico/.png");
                    Console.WriteLine("  --url <url>    browser URL");
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
                SetupTray(port);
                Application.Run(form);   // forks off a message loop; _tray keeps it alive
            }
        }
        return 0;
    }

    // 命令行参数引用: 用于构造 ProcessStartInfo.Arguments
    static string Q(string s) { return s.IndexOf(' ') < 0 ? s : "\"" + s + "\""; }

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
        // 遍历 npx 缓存目录
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
