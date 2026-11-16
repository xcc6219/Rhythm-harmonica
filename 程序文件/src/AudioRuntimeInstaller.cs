using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace HarmonicaStudio
{
    public static class AudioRuntimeInstaller
    {
        private static readonly SemaphoreSlim Installation = new SemaphoreSlim(1, 1);
        internal const string StorageOptimizationMarker = "Storage optimization v1";

        public static Task EnsureInstalledAsync(Action<string> progress, CancellationToken cancellation)
        { return EnsureInstalledAsync(AppPaths.ContentDirectory, progress, cancellation); }

        internal static async Task EnsureInstalledAsync(string contentDirectory, Action<string> progress, CancellationToken cancellation)
        {
            if (String.IsNullOrWhiteSpace(contentDirectory)) throw new ArgumentException("软件目录不存在。", "contentDirectory");
            string root = Path.GetFullPath(contentDirectory);
            await Installation.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (Ready(root)) return;
                await Task.Run(delegate { Install(root, progress, cancellation); }, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                RecordFailure(root, error);
                throw;
            }
            finally { Installation.Release(); }
        }

        internal static bool Ready(string root)
        {
            string marker = Path.Combine(root, "runtime", "python", "ready.txt");
            if (!(File.Exists(marker) &&
                File.Exists(Path.Combine(root, "runtime", "python", "python.exe")) &&
                File.Exists(Path.Combine(root, "runtime", "python", "python310.dll")) &&
                File.Exists(Path.Combine(root, "runtime", "python", "python3.dll")) &&
                File.Exists(Path.Combine(root, "runtime", "python", "python310.zip")) &&
                File.Exists(Path.Combine(root, "runtime", "python", "python310._pth")) &&
                File.Exists(Path.Combine(root, "runtime", "python", "Lib", "site-packages", "basic_pitch", "saved_models", "icassp_2022", "nmp.onnx")) &&
                File.Exists(Path.Combine(root, "scripts", "audio-to-midi.py")))) return false;
            try
            {
                // Upgrade a legacy ready environment once. The setup script probes
                // and reuses its model; the marker also covers unsupported compression.
                if (new FileInfo(marker).Length > 4096) return false;
                return File.ReadAllText(marker).Trim().EndsWith(" / " + StorageOptimizationMarker, StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static void RecordFailure(string root, Exception error)
        {
            error.Data["AudioWorkflowStage"] = "安装";
            string detail = error.Message + Environment.NewLine + Convert.ToString(error.Data["InstallerDiagnostic"]);
            error.Data["AudioRuntimeFailureKind"] = FailureKind(error, detail);
            string logPath = Path.Combine(root, "data", "logs", "audio-runtime.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                // Persist only this operation's diagnostic text; no environment or account data.
                string safe = Regex.Replace(detail, @"https?://\S+", "[下载地址已隐去]", RegexOptions.IgnoreCase);
                safe = Regex.Replace(safe, @"(?im)^(.*?)(authorization|cookie|set-cookie|token|password|passwd|secret|api[_-]?key)\s*[:=][^\r\n]*", "$1$2=[已隐去]");
                if (safe.Length > 16384) safe = safe.Substring(safe.Length - 16384);
                File.WriteAllText(logPath, "音频识别组件准备失败" + Environment.NewLine +
                    "时间 UTC：" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    "类型：" + error.GetType().Name + Environment.NewLine +
                    "分类：" + error.Data["AudioRuntimeFailureKind"] + Environment.NewLine +
                    (error.Data.Contains("InstallerExitCode") ? "退出码：" + error.Data["InstallerExitCode"] + Environment.NewLine : "") + safe,
                    new UTF8Encoding(false));
                error.Data["AudioRuntimeLogPath"] = logPath;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal static string FailureKind(Exception error, string detail)
        {
            string text = (detail ?? "").ToLowerInvariant();
            if (text.Contains("executionpolicy") || text.Contains("execution policy") || text.Contains("digitally signed") ||
                text.Contains("running scripts is disabled") || text.Contains("执行策略") || text.Contains("数字签名")) return "Policy";
            if (text.Contains("disk full") || text.Contains("not enough space") || text.Contains("no space left") ||
                text.Contains("磁盘空间") || text.Contains("磁盘已满") || text.Contains("winerror 112")) return "Disk";
            if (error is UnauthorizedAccessException || text.Contains("access is denied") || text.Contains("permission denied") ||
                text.Contains("拒绝访问") || text.Contains("写入权限")) return "Permission";
            if (error is TimeoutException || text.Contains("timed out") || text.Contains("timeout") || text.Contains("ssl") ||
                text.Contains("tls") || text.Contains("proxy") || text.Contains("connection") || text.Contains("name resolution") ||
                text.Contains("network error") || text.Contains("超时")) return "Network";
            string message = (error.Message ?? "").ToLowerInvariant();
            if (error is FileNotFoundException || message.Contains("初始化") || message.Contains("整理失败") ||
                text.Contains("modulenotfound") || text.Contains("importerror")) return "Runtime";
            if (text.Contains("network") || text.Contains("下载") || text.Contains("网络")) return "Network";
            return "Unknown";
        }
        private static void Install(string root, Action<string> progress, CancellationToken cancellation)
        {
            string script = Path.Combine(root, "scripts", "setup-audio-runtime.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("音频安装文件缺失，请重新复制完整的软件文件夹。", script);
            string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string instruction = "[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false);" +
                "$OutputEncoding=[Console]::OutputEncoding;$ProgressPreference='SilentlyContinue';" +
                "$ErrorActionPreference='Stop';try { & '" + script.Replace("'", "''") +
                "';if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            var info = new ProcessStartInfo(powerShell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(instruction)))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            string temporary = Path.Combine(root, ".audio-setup-" + Guid.NewGuid().ToString("N"));
            info.EnvironmentVariables["HARMONICA_SETUP_TEMP"] = temporary;
            var diagnostics = new StringBuilder();
            Action<string> remember = delegate(string line)
            { if (!String.IsNullOrWhiteSpace(line)) lock (diagnostics) { if (diagnostics.Length > 8192) diagnostics.Remove(0, diagnostics.Length - 4096); diagnostics.AppendLine(line); } };
            Action<string> report = delegate(string line)
            {
                remember(line);
                if (progress != null && (line.StartsWith("正在", StringComparison.Ordinal) ||
                    line.StartsWith("音频", StringComparison.Ordinal) || line.StartsWith("组件", StringComparison.Ordinal))) progress(line);
            };
            if (progress != null) progress("正在准备音频转换组件，首次使用会自动下载，请稍候…");
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using (ProcessJob process = ProcessJob.Start(info, report, remember))
                using (cancellation.Register(process.Stop))
                {
                    Stopwatch elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(200))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed.TotalMinutes > 30)
                        { process.Stop(); throw new TimeoutException("音频组件下载超过 30 分钟，请检查网络后重试。"); }
                    }
                    process.DrainOutput(); cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                    {
                        string detail = diagnostics.ToString();
                        string message = String.IsNullOrWhiteSpace(detail) ? "音频组件安装程序提前退出（错误代码 " + process.ExitCode + "，未返回原因），请重新打开软件后重试。" : FailureMessage(detail);
                        var failure = new InvalidOperationException(message);
                        failure.Data["InstallerDiagnostic"] = detail;
                        failure.Data["InstallerExitCode"] = process.ExitCode;
                        throw failure;
                    }
                    if (!Ready(root)) throw new InvalidOperationException("音频组件未能完成初始化，请检查网络后重试。");
                }
            }
            finally { RemoveTemporaryDirectory(root, temporary); }
            if (progress != null) progress("音频转换组件已就绪，正在继续转换歌曲…");
        }
        private static void RemoveTemporaryDirectory(string root, string temporary)
        {
            // This is an installer-generated GUID path, never a path returned by a subprocess.
            string target = Path.GetFullPath(temporary), boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith(".audio-setup-", StringComparison.Ordinal) || !Directory.Exists(target)) return;
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
            try { Directory.Delete(target, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static string FailureMessage(string diagnostic)
        {
            string[] lines = diagnostic.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].StartsWith("音频", StringComparison.Ordinal) || lines[i].StartsWith("Python 安装", StringComparison.Ordinal) ||
                    lines[i].StartsWith("无法验证", StringComparison.Ordinal) || lines[i].StartsWith("pip 下载校验", StringComparison.Ordinal))
                    return "音频组件安装失败：" + lines[i];
            return "音频组件安装失败，请检查网络后在软件中重试。";
        }
    }
}
