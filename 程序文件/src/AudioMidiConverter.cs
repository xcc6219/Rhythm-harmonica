using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarmonicaStudio
{
    public static class AudioMidiConverter
    {
        public static bool Available
        {
            get { return AudioRuntimeInstaller.Ready(AppPaths.ContentDirectory); }
        }
        public static Task<byte[]> ConvertAsync(string input, string title, Action<string> progress, CancellationToken cancellation)
        { return ConvertAsync(AppPaths.ContentDirectory, input, title, progress, cancellation); }
        internal static async Task<byte[]> ConvertAsync(string contentDirectory, string input, string title, Action<string> progress, CancellationToken cancellation)
        {
            string root = Path.GetFullPath(contentDirectory);
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(input)) throw new FileNotFoundException("音频文件不存在。", input);
            if (new FileInfo(input).Length > 128L * 1024 * 1024) throw new InvalidDataException("音频超过 128 MB，请先截取需要演奏的片段。");
            await AudioRuntimeInstaller.EnsureInstalledAsync(root, progress, cancellation).ConfigureAwait(false);
            return await Task.Run(delegate
            {
                string python = Path.Combine(root, "runtime", "python", "python.exe");
                string script = Path.Combine(root, "scripts", "audio-to-midi.py");
                if (!AudioRuntimeInstaller.Ready(root))
                {
                    var failure = new InvalidOperationException("识别组件文件在准备后缺失，请重新准备组件。");
                    failure.Data["AudioWorkflowStage"] = "安装"; failure.Data["AudioRuntimeFailureKind"] = "Runtime";
                    throw failure;
                }
                if (!File.Exists(input)) throw new FileNotFoundException("音频文件不存在。", input);
                if (new FileInfo(input).Length > 128L * 1024 * 1024) throw new InvalidDataException("音频超过 128 MB，请先截取需要演奏的片段。");
                string output = Path.Combine(Path.GetTempPath(), "harmonica-audio-" + Guid.NewGuid().ToString("N") + ".mid");
                try
                {
                    var info = new ProcessStartInfo(python, "-I -B " + Quote(script) + " " + Quote(Path.GetFullPath(input)) + " " + Quote(output) + " --title " + Quote(title ?? "音频转录"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = root };
                    info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    // Limit CPU contention with the game; conversion runs independently.
                    info.EnvironmentVariables["OMP_NUM_THREADS"] = "2";
                    info.EnvironmentVariables["NUMBA_NUM_THREADS"] = "2";
                    RunTranscriptionProcess(info, progress, cancellation);
                    if (!File.Exists(output) || new FileInfo(output).Length > ScoreIO.MaximumMidiBytes) throw new InvalidDataException("转换未生成可用 MIDI。");
                    SongScore score = ScoreIO.LoadMidi(output);
                    if (score.Tracks.Count == 0) throw new InvalidDataException("转换结果没有可用音符，请更换清晰的音频。");
                    return File.ReadAllBytes(output);
                }
                finally { if (File.Exists(output)) File.Delete(output); }
            }, cancellation).ConfigureAwait(false);
        }
        internal static void RunTranscriptionProcess(ProcessStartInfo info, Action<string> progress, CancellationToken cancellation)
        {
            var errors = new StringBuilder();
            Action<string> report = delegate(string line)
            {
                if (!String.IsNullOrWhiteSpace(line) && progress != null &&
                    (line.StartsWith("正在", StringComparison.Ordinal) || line.StartsWith("转录", StringComparison.Ordinal))) progress(line);
            };
            Action<string> remember = delegate(string line)
            { if (!String.IsNullOrWhiteSpace(line)) lock (errors) { if (errors.Length > 4096) errors.Clear(); errors.AppendLine(line); } };
            cancellation.ThrowIfCancellationRequested();
            using (ProcessJob process = ProcessJob.Start(info, report, remember))
            using (cancellation.Register(process.Stop))
            {
                Stopwatch clock = Stopwatch.StartNew();
                while (!process.WaitForExit(200))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (clock.Elapsed.TotalMinutes > 10)
                    {
                        process.Stop();
                        throw new TimeoutException("转录超过 10 分钟，请截取更短的歌曲片段。");
                    }
                }
                process.DrainOutput(); cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    string detail;
                    lock (errors) detail = errors.ToString().Trim();
                    int errorLine = detail.LastIndexOf("转录失败：", StringComparison.Ordinal);
                    if (errorLine >= 0) detail = detail.Substring(errorLine + "转录失败：".Length);
                    throw new InvalidOperationException("音频转录失败：" + detail);
                }
            }
        }
        internal static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new ArgumentException("命令参数包含无效字符。");
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append(ch); }
                else { result.Append('\\', slashes); result.Append(ch); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
    }
}
