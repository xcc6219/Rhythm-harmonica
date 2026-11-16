using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonicaStudio;

public static class PackagingTests
{
    private static int checks;
    private delegate bool WindowEnumerator(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumerator callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);

    [STAThread]
    public static int Main() { Run(); return 0; }
    public static int Run()
    {
        checks = 0;
        string project = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string executable = Environment.GetEnvironmentVariable("HARMONICA_TEST_APPLICATION");
        if (String.IsNullOrWhiteSpace(executable)) executable = Path.Combine(project, "节奏口琴.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("请先构建用户启动程序。", executable);
        Assembly application = Assembly.LoadFile(executable);
        string[] resources = application.GetManifestResourceNames();
        Assert(new FileInfo(executable).Length < 2 * 1024 * 1024, "启动程序应为轻量交付。");
        Assert(application.GetName().Version == Assembly.GetExecutingAssembly().GetName().Version, "启动程序与本次受检源码为同一版本。");
        Assert(resources.Contains("HarmonicaContent/data/catalog/pop909.json"), "中文索引内置。");
        Assert(resources.Contains("HarmonicaContent/scripts/setup-audio-runtime.ps1") && resources.Contains("HarmonicaContent/scripts/compact-audio-runtime.py"), "无需外部CMD即可准备组件。");
        string temporary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"package-validation");
        string content = Path.Combine(temporary,"程序文件");
        AppPaths.ExtractResources(application,"HarmonicaContent/",content);
        string index = Path.Combine(content,"data","catalog","pop909.json");
        Assert(ChineseScoreCatalog.Search("",index).Count == 909,"单独EXE可释放909条曲库索引。");
        string license = Path.Combine(content,"data","catalog","POP909-LICENSE");
        Assert(File.ReadAllText(license).Contains("MIT License"),"内置许可完整保留。");
        Assert(File.Exists(Path.Combine(content,"scripts","audio-to-midi.py")) && File.Exists(Path.Combine(content,"scripts","audio-requirements.txt")),"内置转换脚本与固定依赖可释放。");
        string webView = Path.Combine(content, "runtime", "webview2");
        Assert(File.Exists(Path.Combine(webView, "Microsoft.Web.WebView2.Core.dll")) &&
            File.Exists(Path.Combine(webView, "Microsoft.Web.WebView2.WinForms.dll")), "软件内官方登录页所需浏览器控件随EXE提供。");
        Assert(File.Exists(Path.Combine(webView, "runtimes", "win-x64", "native", "WebView2Loader.dll")) &&
            File.Exists(Path.Combine(webView, "runtimes", "win-x86", "native", "WebView2Loader.dll")), "官方浏览器加载器包含32位和64位版本。");
        Assert(File.ReadAllText(Path.Combine(webView, "LICENSE.txt")).Contains("Microsoft Corporation"), "浏览器控件原始许可随程序保留。");
        string userIndex = Path.Combine(content,"data","library.json"); File.WriteAllText(userIndex,"[{\"Title\":\"我的歌曲\"}]");
        string settings = Path.Combine(content,"data","settings.json"); File.WriteAllText(settings,"{\"Opacity\":0.75}");
        File.WriteAllText(index,"{\"songs\":[{\"id\":\"001\",\"title\":\"用户索引\",\"artist\":\"本人\"}]}");
        string installerScript = Path.Combine(content, "scripts", "setup-audio-runtime.ps1");
        File.WriteAllText(installerScript, "old internal installation script");
        string browserCore = Path.Combine(webView, "Microsoft.Web.WebView2.Core.dll");
        File.WriteAllText(browserCore, "damaged browser component");
        string ownProfile = Path.Combine(content, "data", "qq-webview", "profile-marker");
        Directory.CreateDirectory(Path.GetDirectoryName(ownProfile)); File.WriteAllText(ownProfile, "personal login profile");
        AppPaths.ExtractResources(application,"HarmonicaContent/",content);
        Assert(File.ReadAllText(userIndex).Contains("我的歌曲") && File.ReadAllText(settings).Contains("0.75"),"再启动保留已有曲库和设置。");
        Assert(ChineseScoreCatalog.Search("",index).Count == 1,"内置释放不得覆盖用户已有索引。");
        using (Stream embeddedScript = application.GetManifestResourceStream("HarmonicaContent/scripts/setup-audio-runtime.ps1"))
        using (var scriptBuffer = new MemoryStream())
        {
            embeddedScript.CopyTo(scriptBuffer);
            Assert(File.ReadAllBytes(installerScript).SequenceEqual(scriptBuffer.ToArray()), "升级时旧安装脚本替换为当前内嵌版本，用户曲库与配置保留。");
        }
        Assert(Directory.GetFiles(Path.Combine(content, "scripts"), "*.update-*").Length == 0, "原子更新安装脚本不遗留中间文件。");
        using (Stream embeddedCore = application.GetManifestResourceStream("HarmonicaContent/runtime/webview2/Microsoft.Web.WebView2.Core.dll"))
        using (var browserBuffer = new MemoryStream())
        {
            embeddedCore.CopyTo(browserBuffer);
            Assert(File.ReadAllBytes(browserCore).SequenceEqual(browserBuffer.ToArray()), "升级或修复时登录控件替换为内嵌版本。");
        }
        Assert(File.ReadAllText(ownProfile) == "personal login profile", "修复控件保留软件专属网页登录资料。");
        string docs = Path.Combine(temporary,"文档中心"); AppPaths.ExtractResources(application,"HarmonicaDocs/",docs);
        Assert(File.Exists(Path.Combine(docs,"main.html")) && Directory.GetFiles(docs,"*.html").Length>=4,"离线说明也随EXE提供。");
        Assert(Directory.GetFiles(Path.Combine(docs,"assets"),"*.png").Length==9,"预览图内置可离线打开。");
        CheckResourceRefresh(application, docs);
        CheckSingleInstance(executable, temporary);
        Console.WriteLine("一键启动打包测试通过，共 "+checks+" 项断言。"); return checks;
    }

    private static void CheckResourceRefresh(Assembly application, string docs)
    {
        string guide = Path.Combine(docs, "main.html");
        string personal = Path.Combine(docs, "我的练习记录.html");
        byte[] expected;
        using (Stream embedded = application.GetManifestResourceStream("HarmonicaDocs/main.html"))
        using (var memory = new MemoryStream()) { embedded.CopyTo(memory); expected = memory.ToArray(); }
        File.WriteAllText(personal, "保留用户另外创建的内容");
        File.WriteAllText(guide, "旧版内置说明");
        AppPaths.ExtractResources(application, "HarmonicaDocs/", docs);
        Assert(File.ReadAllBytes(guide).SequenceEqual(expected), "只替换新版EXE后，帮助首页也更新为当前内嵌说明。");
        Assert(File.ReadAllText(personal) == "保留用户另外创建的内容", "文档更新不删除或修改用户另外创建的页面。");
        DateTime stable = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(guide, stable);
        AppPaths.ExtractResources(application, "HarmonicaDocs/", docs);
        Assert(File.GetLastWriteTimeUtc(guide) == stable, "相同内嵌文件不反复写入，不改变时间戳。");

        File.WriteAllText(guide, "尚未替换的完整说明");
        bool blocked = false;
        using (var held = new FileStream(guide, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { AppPaths.ExtractResources(application, "HarmonicaDocs/", docs); }
            catch (IOException) { blocked = true; }
        }
        Assert(blocked && File.ReadAllText(guide) == "尚未替换的完整说明", "文档被占用导致替换失败时保留原文件，不留下截断正文。");
        Assert(Directory.GetFiles(docs, "*.update-*", SearchOption.AllDirectories).Length == 0, "成功或失败的资源提取均清理本次中间文件。");
        AppPaths.ExtractResources(application, "HarmonicaDocs/", docs);
        Assert(File.ReadAllBytes(guide).SequenceEqual(expected), "占用释放后可以再次更新成功。");
    }

    private static void CheckSingleInstance(string executable, string temporary)
    {
        string isolatedExecutable = Path.Combine(temporary, "节奏口琴.exe");
        File.Copy(executable, isolatedExecutable, true);
        Process first = null, second = null;
        try
        {
            first = Process.Start(StartInfo(isolatedExecutable, temporary));
            Assert(first.WaitForInputIdle(7000), "隔离程序完成首轮初始化后进入消息循环。");
            IntPtr firstWindow = IntPtr.Zero;
            int windowCount = 0;
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 7000 && !first.HasExited)
            {
                firstWindow = FindMainWindow(first.Id, out windowCount);
                if (firstWindow != IntPtr.Zero) break;
                Thread.Sleep(40);
            }
            Assert(firstWindow != IntPtr.Zero && !first.HasExited, "隔离单EXE首次启动应创建可用主窗。");
            firstWindow = FindMainWindow(first.Id, out windowCount);
            Assert(windowCount == 1, "首次实例仅有一个节奏口琴主窗。");
            ShowWindow(firstWindow, 6);
            wait.Restart();
            while (wait.ElapsedMilliseconds < 2000 && !IsIconic(firstWindow)) Thread.Sleep(20);
            Assert(IsIconic(firstWindow), "隔离主窗已最小化，可检验重复启动的恢复行为。");
            second = Process.Start(StartInfo(isolatedExecutable, temporary));
            Assert(second.WaitForExit(4000) && second.ExitCode == 0, "同目录第二实例应在四秒内正常退出。");
            Assert(!first.HasExited, "重复启动不会结束原实例。");
            wait.Restart();
            while (wait.ElapsedMilliseconds < 2500 && IsIconic(firstWindow)) Thread.Sleep(20);
            IntPtr restored = FindMainWindow(first.Id, out windowCount);
            IntPtr result;
            Assert(restored == firstWindow && windowCount == 1 && !IsIconic(restored), "第二次启动恢复原主窗，保持唯一窗口与原句柄。");
            Assert(SendMessageTimeout(restored, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out result) != IntPtr.Zero, "重复启动后原窗口继续响应消息。");
            PostMessage(restored, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Assert(first.WaitForExit(5000) && first.ExitCode == 0, "隔离实例通过普通关闭退出并保存自身设置。");
        }
        finally
        {
            CloseTestProcess(second);
            CloseTestProcess(first);
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string directory)
    { return new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; }
    private static IntPtr FindMainWindow(int processId, out int count)
    {
        int matching = 0; IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != (uint)processId) return true;
            System.Text.StringBuilder text = new System.Text.StringBuilder(256); GetWindowText(window, text, text.Capacity);
            if (text.ToString() == "节奏口琴") { matching++; found = window; }
            return true;
        }, IntPtr.Zero);
        count = matching; return found;
    }
    private static void CloseTestProcess(Process process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                int count; IntPtr window = FindMainWindow(process.Id, out count);
                if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                else process.CloseMainWindow();
                if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(2000); }
            }
        }
        finally { process.Dispose(); }
    }
    private static void Assert(bool condition,string message) { if(!condition) throw new Exception(message); checks++; }
}
