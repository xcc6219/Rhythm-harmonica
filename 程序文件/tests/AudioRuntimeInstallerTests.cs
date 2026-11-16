using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonicaStudio;

public static class AudioRuntimeInstallerTests
{
    private static int checks;
    private static string root;

    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); if (error.Data.Contains("InstallerDiagnostic")) Console.Error.WriteLine(error.Data["InstallerDiagnostic"]); if (error.Data.Contains("InstallerExitCode")) Console.Error.WriteLine("后台程序退出码：" + error.Data["InstallerExitCode"]); return 1; }
    }
    public static int Run()
    {
        checks = 0;
        root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestInstallAndProgress(); TestReadyCompleteness(); TestFailureAndRetry(); TestCancelBeforeStart(); TestCancelTree();
            TestMissingFiles(); TestIncompleteSetup(); TestDiagnosticRedaction(); TestFailureClassification(); TestConcurrentRequests();
            TestLegacyStorageUpgrade(); TestOptionalStorageOptimization();
            TestBootstrapRecovery();
            TestOwnedTreeShutdown(); TestConcurrentOutputIsolation(); TestTranscriptionCancellation(); TestStartShutdownRace();
            Console.WriteLine("隐藏组件安装测试通过，共 " + checks + " 项断言（离线脚本，无模型下载、无游戏输入）。");
            return checks;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void TestInstallAndProgress()
    {
        string folder = CaseFolder("空 格's成功");
        Script(folder, "Write-Output 'pip details must remain hidden'\nWrite-Output '正在安装离线测试组件…'\n" + PrepareRuntime +
            "[IO.File]::WriteAllText((Join-Path $PSScriptRoot '../count.txt'), 'once')\nWrite-Output '音频组件就绪。'");
        var progress = new List<string>();
        AudioRuntimeInstaller.EnsureInstalledAsync(folder, delegate(string text) { lock (progress) progress.Add(text); }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.Exists(Path.Combine(folder, "runtime", "python", "ready.txt")), "成功安装必须产生就绪标记。");
        Assert(AudioRuntimeInstaller.Ready(folder), "成功安装同时具备Python、路径文件、模型和转换脚本，不能只写就绪标记。");
        Assert(progress.Exists(delegate(string text) { return text == "正在安装离线测试组件…"; }), "中文进度须以UTF8完整传回。");
        Assert(!progress.Exists(delegate(string text) { return text.Contains("pip details"); }), "包管理输出不显示在用户进度中。");
        Assert(progress[progress.Count - 1].Contains("继续转换"), "安装完成须引导继续转换。");
        Script(folder, "throw '已安装组件不应再次运行脚本'");
        AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(folder, "count.txt")) == "once", "已有环境直接复用，不重复安装。");
    }
    private static void TestReadyCompleteness()
    {
        string[] required = {
            Path.Combine("runtime", "python", "ready.txt"),
            Path.Combine("runtime", "python", "python.exe"),
            Path.Combine("runtime", "python", "python310.dll"),
            Path.Combine("runtime", "python", "python3.dll"),
            Path.Combine("runtime", "python", "python310.zip"),
            Path.Combine("runtime", "python", "python310._pth"),
            Path.Combine("runtime", "python", "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx"),
            Path.Combine("scripts", "audio-to-midi.py")
        };
        for (int i = 0; i < required.Length; i++)
        {
            string folder = CaseFolder("缺少必需组件-" + i);
            RuntimeFixture(folder, ReadyFixture);
            Assert(AudioRuntimeInstaller.Ready(folder), "完整离线文件夹具满足核心文件与识别组件就绪条件。");
            File.Delete(Path.Combine(folder, required[i]));
            Assert(!AudioRuntimeInstaller.Ready(folder), "缺少任一必需文件不误判就绪：" + required[i]);
            Script(folder, "[IO.File]::AppendAllText((Join-Path $PSScriptRoot '../repairs.txt'), 'once;')\n" + PrepareRuntime);
            AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(AudioRuntimeInstaller.Ready(folder), "旧标记缺组件时实际重新准备，并补齐所有就绪条件。");
            Assert(File.ReadAllText(Path.Combine(folder, "repairs.txt")) == "once;", "缺组件不直接复用旧ready，实际执行一次修复。");
        }
    }
    private static void TestFailureAndRetry()
    {
        string folder = CaseFolder("失败重试");
        Script(folder, "New-Item -ItemType Directory -Path $env:HARMONICA_SETUP_TEMP -Force | Out-Null\n[IO.File]::WriteAllText((Join-Path $env:HARMONICA_SETUP_TEMP 'partial.tmp'), 'partial')\nthrow '音频离线安装测试失败。'");
        Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None));
        Assert(failure is InvalidOperationException && failure.Message.Contains("音频离线安装测试失败"), "失败须显示中文原因。");
        Assert(!File.Exists(Path.Combine(folder, "runtime", "python", "ready.txt")), "失败不得设置就绪标记。");
        Assert(Directory.GetDirectories(folder, ".audio-setup-*").Length == 0, "失败须清理本次暂存目录。");
        AssertFailureMetadata(failure, folder, "Unknown", 1);
        Script(folder, PrepareRuntime);
        AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.Exists(Path.Combine(folder, "runtime", "python", "ready.txt")), "失败后在软件中再次操作可以重试。");
    }
    private static void TestCancelBeforeStart()
    {
        string folder = CaseFolder("预先取消"); Script(folder, PrepareRuntime);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, cancellation.Token));
            Assert(failure is OperationCanceledException, "预先取消应立即结束。");
            Assert(!Directory.Exists(Path.Combine(folder, "runtime")), "预先取消不启动安装脚本。");
            Assert(!failure.Data.Contains("AudioWorkflowStage") && !File.Exists(Path.Combine(folder, "data", "logs", "audio-runtime.log")),
                "用户预先取消不标安装失败，也不产生错误日志。");
        }
    }
    private static void TestCancelTree()
    {
        string folder = CaseFolder("取消父子进程");
        string childCode = "Start-Sleep -Seconds 60";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(childCode));
        Script(folder,
            "New-Item -ItemType Directory -Path $env:HARMONICA_SETUP_TEMP -Force | Out-Null\n" +
            "[IO.File]::WriteAllText((Join-Path $env:HARMONICA_SETUP_TEMP 'partial.tmp'), 'partial')\n" +
            "[IO.File]::WriteAllText((Join-Path $PSScriptRoot '../parent.txt'), [string]$PID)\n" +
            "$child=Start-Process -WindowStyle Hidden -PassThru -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','" + encoded + "'\n" +
            "[IO.File]::WriteAllText((Join-Path $PSScriptRoot '../child.txt'), [string]$child.Id)\nWrite-Output '正在等待取消测试…'\nStart-Sleep -Seconds 60");
        using (var cancellation = new CancellationTokenSource())
        {
            Task task = AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, cancellation.Token);
            int parent = 0, child = 0;
            try
            {
                Wait(delegate { return File.Exists(Path.Combine(folder, "child.txt")); }, "测试子进程未启动。");
                parent = Int32.Parse(File.ReadAllText(Path.Combine(folder, "parent.txt")));
                child = Int32.Parse(File.ReadAllText(Path.Combine(folder, "child.txt")));
                Assert(Alive(parent) && Alive(child), "取消前父进程和子进程必须确实存在。");
                cancellation.Cancel();
                Exception failure = Failure(task);
                Assert(failure is OperationCanceledException, "取消安装须返回取消状态。");
                Assert(!failure.Data.Contains("AudioRuntimeFailureKind") && !File.Exists(Path.Combine(folder, "data", "logs", "audio-runtime.log")),
                    "主动取消进程树不错误记录为组件安装失败。");
                Wait(delegate { return !Alive(parent) && !Alive(child); }, "安装父子进程未终止。");
                Assert(!Alive(parent) && !Alive(child), "取消结束本次父子树，无后台进程残留。");
                Assert(Directory.GetDirectories(folder, ".audio-setup-*").Length == 0, "取消清理本次下载暂存。");
                Assert(!File.Exists(Path.Combine(folder, "runtime", "python", "ready.txt")), "取消不产生错误就绪标记。");
            }
            finally
            {
                cancellation.Cancel();
                if (!task.IsCompleted) task.Wait(5000);
                // Only IDs created by this isolated test are considered for fallback cleanup.
                if (parent != 0 && Alive(parent)) Kill(parent);
                if (child != 0 && Alive(child)) Kill(child);
            }
        }
    }
    private static void TestMissingFiles()
    {
        string folder = CaseFolder("缺失安装文件");
        Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None));
        Assert(failure is FileNotFoundException && failure.Message.Contains("安装文件缺失"), "缺失组件须给出中文错误。");
        AssertFailureMetadata(failure, folder, "Runtime", null);
        string incomplete = CaseFolder("孤立标记");
        Directory.CreateDirectory(Path.Combine(incomplete, "runtime", "python"));
        File.WriteAllText(Path.Combine(incomplete, "runtime", "python", "ready.txt"), "old");
        Assert(Failure(AudioRuntimeInstaller.EnsureInstalledAsync(incomplete, null, CancellationToken.None)) is FileNotFoundException,
            "没有Python的孤立标记不能被当作已安装。");
    }
    private static void TestIncompleteSetup()
    {
        string folder = CaseFolder("未完成初始化");
        Script(folder, "New-Item -ItemType Directory -Path $env:HARMONICA_SETUP_TEMP -Force | Out-Null\nexit 0");
        Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None));
        Assert(failure is InvalidOperationException && failure.Message.Contains("初始化"), "零退出码仍须确认环境初始化成功。");
        Assert(Directory.GetDirectories(folder, ".audio-setup-*").Length == 0, "未完成初始化仍清理本次暂存。");
        AssertFailureMetadata(failure, folder, "Runtime", null);
    }
    private static void TestDiagnosticRedaction()
    {
        string folder = CaseFolder("诊断脱敏");
        Script(folder,
            "[Console]::Error.WriteLine('音频离线诊断测试失败。')\n" +
            "[Console]::Error.WriteLine('https://audio.example.invalid/song.mp3?token=MOCK_URL_SECRET&password=MOCK_URL_PASSWORD')\n" +
            "[Console]::Error.WriteLine('token=MOCK_TOKEN_SECRET password=MOCK_PASSWORD_SECRET secret=MOCK_OTHER_SECRET')\n" +
            "[Console]::Error.WriteLine('Cookie: MOCK_COOKIE_SECRET')\n" +
            "[Console]::Error.WriteLine('Authorization: Bearer MOCK_AUTH_SECRET')\nexit 7");
        Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None));
        Assert(failure.Data.Contains("InstallerDiagnostic") && Convert.ToString(failure.Data["InstallerDiagnostic"]).Contains("MOCK_URL_SECRET"),
            "离线测试确实产生带测试凭据的底层诊断，而非空输入的脱敏假阳性。");
        string log = AssertFailureMetadata(failure, folder, null, 7);
        foreach (string value in new[] { "audio.example.invalid", "MOCK_URL_SECRET", "MOCK_URL_PASSWORD", "MOCK_TOKEN_SECRET",
            "MOCK_PASSWORD_SECRET", "MOCK_OTHER_SECRET", "MOCK_COOKIE_SECRET", "MOCK_AUTH_SECRET" })
            Assert(!log.Contains(value), "持久化日志不保留敏感下载地址或测试凭据：" + value);
        Assert(log.Contains("[下载地址已隐去]") && log.Contains("[已隐去]"), "脱敏日志保留明确提示和错误上下文。");
    }
    private static void TestFailureClassification()
    {
        Assert(AudioRuntimeInstaller.FailureKind(new FileNotFoundException("安装文件缺失"), "安装文件缺失") == "Runtime", "缺安装文件归组件问题。");
        Assert(AudioRuntimeInstaller.FailureKind(new TimeoutException("准备组件超时"), "准备组件超时") == "Network", "安装超时有明确Network分类。");
        Assert(AudioRuntimeInstaller.FailureKind(new UnauthorizedAccessException("无法写入文件夹"), "无法写入文件夹") == "Permission", "目录权限异常归Permission。");
        Assert(AudioRuntimeInstaller.FailureKind(new InvalidOperationException("脚本被禁止"), "running scripts is disabled on this system") == "Policy", "脚本执行限制归Policy。");
        Assert(AudioRuntimeInstaller.FailureKind(new InvalidOperationException("文件未签名"), "file is not digitally signed") == "Policy", "签名策略限制归Policy。");
        Assert(AudioRuntimeInstaller.FailureKind(new InvalidOperationException("音频组件未能完成初始化，请检查网络后重试。"),
            "音频组件未能完成初始化，请检查网络后重试。") == "Runtime", "初始化失败不被通用网络建议误分类。");
        Assert(AudioRuntimeInstaller.FailureKind(new IOException("disk full"), "disk full") == "Disk", "磁盘空间不足与网络错误区分。");
        Assert(AudioRuntimeInstaller.FailureKind(new InvalidOperationException("后台程序无诊断"), "后台程序无诊断") == "Unknown", "未知启动失败诚实保留Unknown。");
        string[] messages = { "Connection timed out", "Access is denied", "ExecutionPolicy: running scripts is disabled" };
        string[] kinds = { "Network", "Permission", "Policy" };
        for (int i = 0; i < messages.Length; i++)
        {
            string folder = CaseFolder("分类-" + kinds[i]);
            Script(folder, "[Console]::Error.WriteLine('" + messages[i] + "')\nexit 9");
            Exception failure = Failure(AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None));
            string log = AssertFailureMetadata(failure, folder, kinds[i], 9);
            Assert(log.Contains("分类：" + kinds[i]), "安装异常的实际持久化日志保留具体原因分类。");
        }
    }
    private static void TestConcurrentRequests()
    {
        string folder = CaseFolder("重复点击");
        Script(folder, "[IO.File]::AppendAllText((Join-Path $PSScriptRoot '../starts.txt'), 'once;')\nStart-Sleep -Milliseconds 300\n" + PrepareRuntime);
        Task first = AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None);
        Task second = AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None);
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(folder, "starts.txt")) == "once;", "重复请求只启动一个安装进程。");
        Assert(first.Status == TaskStatus.RanToCompletion && second.Status == TaskStatus.RanToCompletion, "重复请求共同复用成功就绪结果。");
        Assert(AudioRuntimeInstaller.Ready(folder), "并发成功后环境仍满足五项就绪条件。");
    }
    private static void TestLegacyStorageUpgrade()
    {
        string folder = CaseFolder("已有模型空间优化升级");
        RuntimeFixture(folder, "Basic Pitch 0.4.0 / ONNX Runtime 1.19.2 / Python 3.10.11");
        Assert(!AudioRuntimeInstaller.Ready(folder), "旧就绪标记实际触发一次空间优化，不能被五个文件的存在直接跳过。");
        string[] unchanged = {
            Path.Combine("runtime", "python", "python.exe"), Path.Combine("runtime", "python", "python310._pth"),
            Path.Combine("runtime", "python", "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx"),
            Path.Combine("scripts", "audio-to-midi.py")
        };
        var originals = new Dictionary<string, string>();
        foreach (string file in unchanged) originals[file] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, file)));
        Script(folder, "Write-Output '正在检查已有识别组件，可用时直接继续，不重复下载...'\n" +
            "[IO.File]::AppendAllText((Join-Path $PSScriptRoot '../optimizations.txt'), 'once;')\n" + UpdateOptimizationMarker);
        AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(AudioRuntimeInstaller.Ready(folder), "优化尝试后旧模型成为当前版本就绪环境。");
        Assert(File.ReadAllText(Path.Combine(folder, "runtime", "python", "ready.txt")).EndsWith(" / " + AudioRuntimeInstaller.StorageOptimizationMarker),
            "空间优化版本保存在原ready.txt，不另增标记文件。");
        foreach (string file in unchanged)
            Assert(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, file))) == originals[file], "旧环境迁移保留模型和转换所需文件内容：" + file);
        Script(folder, "throw '优化过的组件不应再次运行准备脚本'");
        AudioRuntimeInstaller.EnsureInstalledAsync(folder, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(folder, "optimizations.txt")) == "once;", "第二次转换不重复优化或下载。");

        File.WriteAllText(Path.Combine(folder, "runtime", "python", "ready.txt"), "offline ready / Storage optimization v10");
        Assert(!AudioRuntimeInstaller.Ready(folder), "优化版本必须完整匹配，v10不能误匹配v1。");
    }
    private static void TestOptionalStorageOptimization()
    {
        foreach (string reason in new[] { "当前文件系统不支持普通NTFS压缩", "系统暂未完成文件压缩" })
        {
            string folder = CaseFolder("可选压缩-" + (reason.Contains("不支持") ? "不支持" : "失败"));
            RuntimeFixture(folder, "legacy ready");
            string model = Path.Combine(folder, "runtime", "python", "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx");
            string originalModel = Convert.ToBase64String(File.ReadAllBytes(model));
            Script(folder, "Write-Output '音频组件空间优化已跳过：" + reason + "；识别模型仍可使用。'\n" + UpdateOptimizationMarker);
            var progress = new List<string>();
            AudioRuntimeInstaller.EnsureInstalledAsync(folder, progress.Add, CancellationToken.None).GetAwaiter().GetResult();
            Assert(AudioRuntimeInstaller.Ready(folder), "可选压缩不支持或失败仍允许进入转换。");
            Assert(progress.Exists(delegate(string text) { return text.Contains("空间优化已跳过") && text.Contains("仍可使用"); }),
                "可选压缩结果以明确非阻断提示传回同页进度。");
            Assert(!File.Exists(Path.Combine(folder, "data", "logs", "audio-runtime.log")), "可选压缩警告不伪报识别组件安装失败。");
            Assert(Convert.ToBase64String(File.ReadAllBytes(model)) == originalModel, "可选压缩警告不删除或替换已装模型。");
        }
    }
    private static void RuntimeFixture(string folder, string marker)
    {
        string[] files = {
            Path.Combine("runtime", "python", "ready.txt"), Path.Combine("runtime", "python", "python.exe"),
            Path.Combine("runtime", "python", "python310.dll"), Path.Combine("runtime", "python", "python3.dll"),
            Path.Combine("runtime", "python", "python310.zip"),
            Path.Combine("runtime", "python", "python310._pth"),
            Path.Combine("runtime", "python", "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx"),
            Path.Combine("scripts", "audio-to-midi.py")
        };
        foreach (string relative in files)
        {
            string file = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, relative.EndsWith("ready.txt") ? marker : "offline fixture; no executable Python or model", new UTF8Encoding(false));
        }
    }
    private static void TestBootstrapRecovery()
    {
        string application = Environment.GetEnvironmentVariable("HARMONICA_TEST_APPLICATION");
        if (String.IsNullOrWhiteSpace(application))
            application = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)), "节奏口琴.exe");
        string source;
        using (Stream input = System.Reflection.Assembly.LoadFile(application).GetManifestResourceStream("HarmonicaContent/scripts/setup-audio-runtime.ps1"))
        using (var reader = new StreamReader(input, Encoding.UTF8)) source = reader.ReadToEnd();
        int main = source.IndexOf("$projectRoot =", StringComparison.Ordinal);
        Assert(main > 0 && source.Substring(0, main).Contains("function Initialize-PythonBootstrap"), "回归实际内嵌的安装准备函数。");
        string bootstrap = source.Substring(0, main);
        foreach (string scenario in new[] { "完整", "中断解压", "无效压缩包" })
        {
            string folder = CaseFolder("Python核心修复-" + scenario);
            RuntimeFixture(folder, ReadyFixture);
            string runtime = Path.Combine(folder, "runtime", "python");
            string archiveSource = Path.Combine(folder, "archive-source"); Directory.CreateDirectory(archiveSource);
            string staging = Path.Combine(folder, ".audio-setup-offline-core"); Directory.CreateDirectory(staging);
            foreach (string name in new[] { "python.exe", "python310.dll", "python3.dll", "python310.zip" })
                if (scenario != "无效压缩包" || name != "python310.dll") File.WriteAllText(Path.Combine(archiveSource, name), "restored core " + name);
            string exe = Path.Combine(runtime, "python.exe"), originalExe = File.ReadAllText(exe);
            string model = Path.Combine(runtime, "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx");
            string originalModel = File.ReadAllText(model);
            if (scenario != "完整") File.Delete(Path.Combine(runtime, "python310.dll"));
            string quotedFolder = folder.Replace("'", "''");
            string script = bootstrap + "\nfunction Invoke-WebRequest { param([switch]$UseBasicParsing,[string]$Uri,[string]$OutFile)\n" +
                "if ($Uri -ne 'https://www.python.org/ftp/python/3.10.11/python-3.10.11-embed-amd64.zip') { throw 'Unexpected download' }\n" +
                "[IO.File]::AppendAllText('" + quotedFolder + "/downloads.txt','once;')\n" +
                "[IO.File]::Copy('" + quotedFolder + "/offline-core.zip',$OutFile)\n}\n" +
                "Add-Type -AssemblyName System.IO.Compression.FileSystem\n" +
                "[IO.Compression.ZipFile]::CreateFromDirectory('" + quotedFolder + "/archive-source','" + quotedFolder + "/offline-core.zip')\n" +
                "Initialize-PythonBootstrap '" + runtime.Replace("'", "''") + "' '" + staging.Replace("'", "''") + "'\n" +
                "Initialize-PythonBootstrap '" + runtime.Replace("'", "''") + "' '" + staging.Replace("'", "''") + "'\n";
            Script(folder, script);
            // Match the production installer's plain UTF-8 diagnostic wrapper.
            // Uncaught PowerShell errors otherwise use legacy-encoded CLIXML.
            string invocation = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try { & '" +
                Path.Combine(folder, "scripts", "setup-audio-runtime.ps1").Replace("'", "''") +
                "' } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            Task operation = Task.Run(delegate { AudioMidiConverter.RunTranscriptionProcess(
                WorkerInfo(invocation, folder), null, CancellationToken.None); });
            if (scenario == "无效压缩包")
            {
                Exception failure = Failure(operation);
                Assert(failure.Message.Contains("Python 安装包缺少核心文件"), "不完整压缩包给出具体的核心文件错误。实际诊断：" + failure.Message);
                Assert(File.ReadAllText(exe) == originalExe && !File.Exists(Path.Combine(runtime, "python310.dll")), "安装包验证失败时不覆盖原有核心文件。");
            }
            else
            {
                operation.GetAwaiter().GetResult();
                Assert(AudioRuntimeInstaller.Ready(folder), "离线修复后就绪检查认可完整核心。");
                if (scenario == "完整")
                    Assert(!File.Exists(Path.Combine(folder, "downloads.txt")) && File.ReadAllText(exe) == originalExe, "完整环境不重复下载和改写Python核心。");
                else
                {
                    Assert(File.ReadAllText(exe) == "restored core python.exe" && File.ReadAllText(Path.Combine(runtime, "python310.dll")) == "restored core python310.dll", "已存在python.exe而缺DLL时也能完整恢复核心。");
                    Assert(File.ReadAllText(Path.Combine(folder, "downloads.txt")) == "once;", "成功恢复后重复准备不再次下载或解压。");
                }
            }
            Assert(File.ReadAllText(model) == originalModel, "核心修复保留已有识别模型。");
        }
    }
    private static void TestOwnedTreeShutdown()
    {
        string folder = CaseFolder("自有进程树退出");
        Process control = null;
        ProcessJob owned = null;
        int parent = 0, child = 0;
        using (var lifetime = new OwnedProcessLifetime())
        {
            try
            {
                // This independent control is created by this test only and never
                // registered to the application scope. No existing user process is used.
                control = Process.Start(WorkerInfo("Start-Sleep -Seconds 60", folder));
                owned = lifetime.Start(WorkerInfo(TreeWorker(folder), folder), null, null);
                parent = owned.Id;
                Wait(delegate { return File.Exists(Path.Combine(folder, "child.txt")); }, "退出测试子进程未启动。");
                child = Int32.Parse(File.ReadAllText(Path.Combine(folder, "child.txt")));
                Assert(Alive(parent) && Alive(child) && !control.HasExited, "退出前自有父子进程及独立control均真实存活。");
                Assert(lifetime.Count == 1 && owned.ActiveProcessCount >= 2, "生命周期登记完整Job并统计其后代，不只记父PID。");
                Stopwatch elapsed = Stopwatch.StartNew();
                lifetime.Shutdown();
                Wait(delegate { return !Alive(parent) && !Alive(child); }, "退出后自有父子进程仍存活。");
                Assert(elapsed.ElapsedMilliseconds < 4500, "整树退出有共享限时，不逐个后代叠加等待。");
                Assert(lifetime.IsShuttingDown && lifetime.Count == 0 && !Alive(parent) && !Alive(child), "退出完成没有自有Job或后代残留。");
                Assert(!control.HasExited, "清理仅影响登记的Job，独立control仍运行。");
                bool rejected = false;
                try { lifetime.Start(WorkerInfo("[IO.File]::WriteAllText('late.txt','started')", folder), null, null); }
                catch (OperationCanceledException) { rejected = true; }
                Assert(rejected && !File.Exists(Path.Combine(folder, "late.txt")), "退出标志设置后不允许延迟任务再启动进程。");
                lifetime.Shutdown();
                Assert(lifetime.Count == 0 && !control.HasExited, "重复退出幂等且不碰独立control。");
            }
            finally
            {
                lifetime.Shutdown();
                if (owned != null) owned.Dispose();
                if (control != null) { if (!control.HasExited) { control.Kill(); control.WaitForExit(2000); } control.Dispose(); }
                if (parent != 0 && Alive(parent)) Kill(parent);
                if (child != 0 && Alive(child)) Kill(child);
            }
        }
    }
    private static void TestConcurrentOutputIsolation()
    {
        string folder = CaseFolder("并发任务标准流隔离");
        ProcessJob fast = null, slow = null;
        var lines = new List<string>();
        using (var fastLifetime = new OwnedProcessLifetime())
        using (var slowLifetime = new OwnedProcessLifetime())
        using (var start = new ManualResetEventSlim(false))
        {
            Task<ProcessJob> fastTask = Task.Run(delegate
            {
                start.Wait();
                return fastLifetime.Start(WorkerInfo("Write-Output 'lifecycle-quick'; exit 0", folder),
                    delegate(string line) { lock (lines) lines.Add(line); }, null);
            });
            Task<ProcessJob> slowTask = Task.Run(delegate
            {
                start.Wait();
                return slowLifetime.Start(WorkerInfo("Write-Output 'lifecycle-blocked'; Start-Sleep -Seconds 60", folder), null, null);
            });
            try
            {
                start.Set(); Task.WaitAll(fastTask, slowTask);
                fast = fastTask.Result; slow = slowTask.Result;
                Assert(fast.WaitForExit(5000), "真实并发启动的短任务正常结束。");
                Stopwatch elapsed = Stopwatch.StartNew(); fast.DrainOutput();
                Assert(elapsed.ElapsedMilliseconds < 2500 && lines.Contains("lifecycle-quick"),
                    "另一个worker阻塞时短任务stdout仍及时到EOF，不互相继承写管道。");
                Assert(!slow.HasExited && fast.ExitCode == 0, "短任务读取结束不需终止另一个Job。");
                fast.Dispose();
                Assert(fastLifetime.Count == 0 && !slow.HasExited, "释放短任务只注销本scope的Job。");
            }
            finally
            {
                start.Set(); fastLifetime.Shutdown(); slowLifetime.Shutdown();
                if (fast != null) fast.Dispose(); if (slow != null) slow.Dispose();
            }
        }
    }
    private static void TestTranscriptionCancellation()
    {
        string folder = CaseFolder("转录实际取消任务树");
        int parent = 0, child = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            Task task = Task.Run(delegate { AudioMidiConverter.RunTranscriptionProcess(WorkerInfo(TreeWorker(folder), folder), null, cancellation.Token); });
            try
            {
                Wait(delegate { return File.Exists(Path.Combine(folder, "child.txt")); }, "转录取消测试子进程未启动。");
                parent = Int32.Parse(File.ReadAllText(Path.Combine(folder, "parent.txt")));
                child = Int32.Parse(File.ReadAllText(Path.Combine(folder, "child.txt")));
                Assert(Alive(parent) && Alive(child), "转录生产进程helper实际启动父子树。");
                cancellation.Cancel();
                Exception failure = Failure(task);
                Assert(failure is OperationCanceledException, "转录取消返回取消状态而非失败或成功结果。");
                Wait(delegate { return !Alive(parent) && !Alive(child); }, "转录取消后父子树仍存活。");
                Assert(!Alive(parent) && !Alive(child), "转录实际取消同时清理父与后代。");
                Assert(!File.Exists(Path.Combine(folder, "data", "logs", "audio-runtime.log")), "主动取消转录不伪报安装故障。");
            }
            finally
            {
                cancellation.Cancel(); if (!task.IsCompleted) task.Wait(5000);
                if (parent != 0 && Alive(parent)) Kill(parent);
                if (child != 0 && Alive(child)) Kill(child);
            }
        }
    }
    private static void TestStartShutdownRace()
    {
        string folder = CaseFolder("后台启动退出竞态");
        var pids = new List<int>();
        using (var lifetime = new OwnedProcessLifetime())
        using (var start = new ManualResetEventSlim(false))
        {
            var attempts = new List<Task>();
            for (int i = 0; i < 4; i++) attempts.Add(Task.Run(delegate
            {
                start.Wait();
                try
                {
                    ProcessJob process = lifetime.Start(WorkerInfo("Start-Sleep -Seconds 60", folder), null, null);
                    lock (pids) pids.Add(process.Id);
                }
                catch (OperationCanceledException) { }
            }));
            Task shutdown = Task.Run(delegate { start.Wait(); lifetime.Shutdown(); });
            try
            {
                start.Set(); Task.WaitAll(attempts.ToArray()); shutdown.GetAwaiter().GetResult();
                Assert(lifetime.IsShuttingDown && lifetime.Count == 0, "启动与退出并发后不留未登记任务。");
                foreach (int id in pids) Wait(delegate { return !Alive(id); }, "竞态期间创建的自有进程未退出。");
                Assert(pids.TrueForAll(delegate(int id) { return !Alive(id); }), "竞态期间成功创建的进程全部随scope退出。");
            }
            finally
            {
                start.Set(); lifetime.Shutdown();
                foreach (int id in pids) if (Alive(id)) Kill(id);
            }
        }
    }
    private static ProcessStartInfo WorkerInfo(string code, string folder)
    {
        string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string instruction = "[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false);$OutputEncoding=[Console]::OutputEncoding;" + code;
        return new ProcessStartInfo(powerShell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(instruction)))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
    }
    private static string TreeWorker(string folder)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
        return "[IO.File]::WriteAllText('" + Path.Combine(folder, "parent.txt").Replace("'", "''") + "',[string]$PID)\n" +
            "$child=Start-Process -WindowStyle Hidden -PassThru -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','" + encoded + "'\n" +
            "[IO.File]::WriteAllText('" + Path.Combine(folder, "child.txt").Replace("'", "''") + "',[string]$child.Id)\nStart-Sleep -Seconds 60";
    }
    private const string ReadyFixture = "offline ready / " + AudioRuntimeInstaller.StorageOptimizationMarker;
    private const string UpdateOptimizationMarker =
        "[IO.File]::WriteAllText((Join-Path $PSScriptRoot '../runtime/python/ready.txt'), '" + ReadyFixture + "')\n";
    private const string PrepareRuntime =
        "$runtime=Join-Path $PSScriptRoot '../runtime/python'\n" +
        "$model=Join-Path $runtime 'Lib/site-packages/basic_pitch/saved_models/icassp_2022'\n" +
        "New-Item -ItemType Directory -Path $runtime,$model -Force | Out-Null\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'python.exe'), 'offline test placeholder')\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'python310.dll'), 'offline DLL fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'python3.dll'), 'offline ABI fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'python310.zip'), 'offline standard library fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'python310._pth'), 'offline path fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $model 'nmp.onnx'), 'offline model fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'audio-to-midi.py'), '# offline converter fixture')\n" +
        "[IO.File]::WriteAllText((Join-Path $runtime 'ready.txt'), '" + ReadyFixture + "')\n";
    private static string AssertFailureMetadata(Exception error, string folder, string kind, int? exitCode)
    {
        Assert(Convert.ToString(error.Data["AudioWorkflowStage"]) == "安装", "安装异常明确标记发生阶段。");
        Assert(error.Data.Contains("AudioRuntimeFailureKind") && (kind == null || Convert.ToString(error.Data["AudioRuntimeFailureKind"]) == kind),
            "安装异常提供真实分类" + (kind == null ? "。" : "：" + kind));
        string expected = Path.Combine(folder, "data", "logs", "audio-runtime.log");
        Assert(Convert.ToString(error.Data["AudioRuntimeLogPath"]) == expected && File.Exists(expected), "诊断日志确实保存且异常提供可定位路径。");
        string log = File.ReadAllText(expected, Encoding.UTF8);
        Assert(log.Contains("音频识别组件准备失败") && log.Contains("时间 UTC：") && log.Contains("类型：" + error.GetType().Name),
            "诊断日志包含可读标题、时间和异常类型。");
        if (exitCode.HasValue)
            Assert(error.Data.Contains("InstallerExitCode") && Convert.ToInt32(error.Data["InstallerExitCode"]) == exitCode.Value &&
                log.Contains("退出码：" + exitCode.Value), "真实退出码同时保留在异常和持久化日志中。");
        else
            Assert(!error.Data.Contains("InstallerExitCode"), "没有异常进程退出码时不伪造数字。");
        return log;
    }
    private static string CaseFolder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static void Script(string folder, string text)
    { Directory.CreateDirectory(Path.Combine(folder, "scripts")); File.WriteAllText(Path.Combine(folder, "scripts", "setup-audio-runtime.ps1"), text, new UTF8Encoding(true)); }
    private static Exception Failure(Task task)
    { try { task.GetAwaiter().GetResult(); } catch (Exception error) { return error; } throw new Exception("操作意外成功。"); }
    private static bool Alive(int id)
    { try { using (Process process = Process.GetProcessById(id)) return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static void Kill(int id) { using (Process process = Process.GetProcessById(id)) { process.Kill(); process.WaitForExit(2000); } }
    private static void Wait(Func<bool> condition, string message)
    { Stopwatch time = Stopwatch.StartNew(); while (!condition()) { if (time.ElapsedMilliseconds > 10000) throw new Exception(message); Thread.Sleep(20); } }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
