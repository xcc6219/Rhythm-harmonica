using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class AudioMidiDialog : Form
    {
        private readonly TextBox path;
        private readonly Label status;
        private readonly SoftButton browse, convert, cancel;
        private readonly string songTitle;
        private readonly bool fixedSongTitle;
        private CancellationTokenSource cancellation;
        private bool working, closeRequested, closingDialog;
        private readonly OnlineSong remoteSong;
        private readonly TextBox audioUrl, downloadFolder;
        private readonly SoftButton remoteConvert, folderButton, officialButton, clientButton;
        private readonly System.Windows.Forms.Timer downloadTimer;
        private readonly Dictionary<string, string> observedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool monitoringDownloads, noDirectAudio;
        public byte[] MidiBytes { get; private set; }
        public string AudioFilePath { get { return path.Text; } }
        public bool AutoDownload { get; set; }
        public bool AutoConvertLocal { get; set; }
        public string InitialAudioPath { get; set; }
        internal Func<OnlineSong, string> ResolveSongAudio { get; set; }
        internal Func<string, string, Action<string>, CancellationToken, Task<string>> DownloadSongAudio { get; set; }
        internal Func<string, string, Action<string>, CancellationToken, Task<byte[]>> ConvertLocalAudio { get; set; }
        private string ProviderName { get { return remoteSong == null ? MusicAudioSources.QqProvider : MusicAudioSources.DisplayName(remoteSong); } }
        public string SongName { get { return fixedSongTitle || String.IsNullOrEmpty(path.Text) ? songTitle : Path.GetFileNameWithoutExtension(path.Text); } }
        public AudioMidiDialog(string title) : this(title, null, "", null) { }
        public AudioMidiDialog(string title, OnlineSong song, string folder, Action<string> rememberFolder)
        {
            remoteSong = song;
            ResolveSongAudio = MusicAudioSources.ResolveAudioUrl;
            DownloadSongAudio = SongAudioDownload.DownloadAsync;
            ConvertLocalAudio = ConvertWithRuntime;
            fixedSongTitle = !String.IsNullOrWhiteSpace(title);
            songTitle = fixedSongTitle ? title : "我的音频转录";
            Text = "歌曲音频 → MIDI · " + songTitle; AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(700, 592);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.PixelFont(10);
            AddLabel("将已有歌曲音频转成可演奏 MIDI", 22, 17, 587, 31, 14);
            AddLabel("支持 MP3、WAV、FLAC、OGG。本机识别，不上传音频。", 22, 57, 587, 40, 9);
            path = new TextBox { Location = new Point(22, 105), Width = 463, ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.PixelFont(10) };
            browse = new SoftButton("选择音频", false) { Location = new Point(496, 102), Width = 111 };
            browse.Click += delegate
            {
                using (var picker = new OpenFileDialog { Title = "选择「" + songTitle + "」的歌曲音频", Filter = "支持的音频|*.mp3;*.wav;*.flac;*.ogg|所有文件|*.*" })
                    if (picker.ShowDialog(this) == DialogResult.OK) { monitoringDownloads = false; observedFiles.Clear(); path.Text = picker.FileName; convert.Enabled = true; }
            };
            Controls.AddRange(new Control[] { path, browse });
            AddLabel("混音歌曲可能混入伴奏音符；清晰人声、口琴或单乐器的效果通常更好。\n转录不会自动分离人声和伴奏，完成后请先试听并确认吹奏声轨。", 22, 150, 587, 58, 9);
            AddLabel(remoteSong == null ? "下载歌曲，或使用你取得的普通音频下载链接" : ProviderName + " · 下载所选歌曲并自动转 MIDI", 22, 218, 656, 32, 11);
            audioUrl = new TextBox { Location = new Point(22, 260), Width = 488, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.PixelFont(10), MaxLength = 8192 };
            remoteConvert = new SoftButton("下载并转 MIDI", true) { Location = new Point(522, 256), Width = 156 };
            remoteConvert.Click += delegate { StartRemoteConversion(); };
            Controls.AddRange(new Control[] { audioUrl, remoteConvert });
            AddLabel(remoteSong == null ? "输入普通音频的 HTTPS 下载链接；也可选择已下载的歌曲文件。" : "留空：尝试所选歌曲可下载的普通音频；受限版本请在官方客户端下载。", 22, 295, 656, 36, 10);
            downloadFolder = new TextBox { Location = new Point(22, 344), Width = 488, ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.PixelFont(10), Text = folder ?? "" };
            folderButton = new SoftButton("下载目录", false) { Location = new Point(522, 340), Width = 156 };
            folderButton.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { Description = "选择 " + ProviderName + " 保存普通 MP3 / FLAC 等文件的下载目录", SelectedPath = downloadFolder.Text })
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    { downloadFolder.Text = picker.SelectedPath; observedFiles.Clear(); if (rememberFolder != null) rememberFolder(picker.SelectedPath); monitoringDownloads = remoteSong != null; if (monitoringDownloads) ShowWaitingForDownload(); }
            };
            Controls.AddRange(new Control[] { downloadFolder, folderButton });
            officialButton = new SoftButton(ProviderName + " 登录 / 下载 ↗", false) { Location = new Point(22, 390), Width = 212 };
            officialButton.Click += delegate { OpenOfficialDownload(); };
            clientButton = new SoftButton(ProviderName + " 客户端 ↗", false) { Location = new Point(248, 390), Width = 190 };
            clientButton.Click += delegate { OpenUrl(MusicAudioSources.OfficialDownloadUrl(ProviderName)); };
            Controls.AddRange(new Control[] { officialButton, clientButton });
            AddLabel("选择下载目录后，软件可自动识别同歌手同版本的普通音频并继续转录。\nQMC / MGG / KGM 等受保护文件无法直接转录，请使用普通音频。", 22, 436, 656, 46, 10);
            status = AddLabel(AudioMidiConverter.Available ? "已准备好 · 最多 10 分钟 / 128 MB" : "首次转换会自动准备识别组件，完成后继续本机转录。", 22, 493, 656, 46, 10);
            status.AutoEllipsis = true;
            convert = new SoftButton("转换并加入曲库", true) { Location = new Point(362, 546), Width = 177, Enabled = false };
            cancel = new SoftButton("取消", false) { Location = new Point(551, 546), Width = 127 };
            convert.Click += delegate { StartConversion(); };
            cancel.Click += delegate { Close(); };
            Controls.AddRange(new Control[] { convert, cancel });
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (working && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true; closeRequested = true; CancelActiveWork();
                    if (CanUpdateControls) { cancel.Enabled = false; status.Text = "正在取消转录…"; }
                    return;
                }
                BeginClosing();
            };
            downloadTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            downloadTimer.Tick += delegate { DetectDownloadedAudio(); }; downloadTimer.Start();
            FormClosed += delegate { downloadTimer.Stop(); };
            Shown += delegate
            {
                if (!String.IsNullOrWhiteSpace(InitialAudioPath))
                { path.Text = InitialAudioPath; convert.Enabled = File.Exists(path.Text); }
                if (AutoConvertLocal && convert.Enabled) StartConversion();
                else if (AutoDownload) StartRemoteConversion(); else audioUrl.Focus();
            };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                BeginClosing();
                if (downloadTimer != null) { downloadTimer.Stop(); downloadTimer.Dispose(); }
            }
            base.Dispose(disposing);
        }
        private bool CanUpdateControls { get { return !closingDialog && !IsDisposed && !Disposing; } }
        private void BeginClosing()
        {
            if (closingDialog) return;
            closingDialog = closeRequested = true; monitoringDownloads = false;
            if (downloadTimer != null) downloadTimer.Stop();
            CancelActiveWork();
        }
        private void CancelActiveWork()
        {
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
        private void FinishOperation(CancellationTokenSource operation)
        {
            operation.Dispose();
            if (Object.ReferenceEquals(cancellation, operation)) cancellation = null;
            working = false;
            if (CanUpdateControls) { SetWorkingControls(true); if (closeRequested) Close(); }
        }
        private static void EnsureUiContext()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        private Label AddLabel(string text, int x, int y, int w, int h, float size)
        {
            var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = Theme.PixelFont(size), ForeColor = Theme.Muted };
            Controls.Add(label); return label;
        }
        private async void StartConversion()
        { await RunConversionAsync(); }
        internal async Task RunConversionAsync()
        {
            if (!CanUpdateControls || working || !File.Exists(path.Text)) return;
            EnsureUiContext();
            if (new FileInfo(path.Text).Length > 128L * 1024 * 1024)
            { status.Text = "音频超过 128 MB，请先截取需要的片段。"; status.ForeColor = Theme.Warm; return; }
            if (!SongAudioDownload.IsSupported(path.Text))
            { status.Text = "请选择普通 MP3、WAV、FLAC 或 OGG 音频，受保护文件不能直接转录。"; status.ForeColor = Theme.Warm; return; }
            try
            {
                using (var stream = new FileStream(path.Text, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] header = new byte[32]; int length = stream.Read(header, 0, header.Length); Array.Resize(ref header, length);
                    if (!SongAudioDownload.ValidateAudioHeader(header)) { status.Text = "文件不是普通音频，改扩展名也不能转录受保护文件。"; status.ForeColor = Theme.Warm; return; }
                }
            }
            catch (IOException ex) { status.Text = "无法读取音频：" + ex.Message; return; }
            catch (UnauthorizedAccessException) { status.Text = "无法读取音频，请确认文件已下载完成。"; return; }
            status.ForeColor = Theme.Muted;
            working = true; closeRequested = false;
            var operation = new CancellationTokenSource(); cancellation = operation;
            SetWorkingControls(false); monitoringDownloads = false; status.Text = "正在加载模型并识别音符…";
            try
            {
                byte[] bytes = await ConvertLocalAudio(path.Text, SongName, UpdateProgress, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!CanUpdateControls || closeRequested) return;
                MidiBytes = bytes;
                working = false; DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { if (CanUpdateControls) status.Text = "已取消转录。"; }
            catch (Exception ex) { if (CanUpdateControls) { status.Text = ex.Message; status.ForeColor = Theme.Warm; } }
            finally { FinishOperation(operation); }
        }
        private static async Task<byte[]> ConvertWithRuntime(string input, string title, Action<string> progress, CancellationToken token)
        {
            await AudioRuntimeInstaller.EnsureInstalledAsync(progress, token);
            return await AudioMidiConverter.ConvertAsync(input, title, progress, token);
        }
        private void SetWorkingControls(bool enabled)
        {
            if (!CanUpdateControls) return;
            browse.Enabled = remoteConvert.Enabled = folderButton.Enabled = officialButton.Enabled = clientButton.Enabled = audioUrl.Enabled = enabled;
            convert.Enabled = enabled && File.Exists(path.Text);
            if (enabled) cancel.Enabled = true;
        }
        private async void StartRemoteConversion()
        { await RunRemoteConversionAsync(); }
        internal async Task RunRemoteConversionAsync()
        {
            if (!CanUpdateControls || working) return;
            EnsureUiContext();
            string url = audioUrl.Text.Trim();
            working = true; closeRequested = false;
            var operation = new CancellationTokenSource(); cancellation = operation;
            SetWorkingControls(false); monitoringDownloads = noDirectAudio = false; status.ForeColor = Theme.Muted;
            bool downloaded = false;
            try
            {
                if (url.Length == 0 && remoteSong != null)
                {
                    status.Text = "正在确认 " + ProviderName + " 是否提供所选版本的普通音频…";
                    url = await Task.Run(delegate { return ResolveSongAudio(remoteSong); });
                    operation.Token.ThrowIfCancellationRequested();
                    if (!CanUpdateControls || closeRequested) return;
                }
                if (String.IsNullOrWhiteSpace(url))
                {
                    if (remoteSong == null) { status.Text = "请输入普通音频的 HTTPS 下载链接，或选择已下载的歌曲文件。"; status.ForeColor = Theme.Warm; }
                    else { noDirectAudio = true; monitoringDownloads = true; observedFiles.Clear(); ShowWaitingForDownload(); }
                    return;
                }
                status.Text = "正在下载歌曲音频…";
                string downloadedPath = await DownloadSongAudio(url, songTitle, UpdateProgress, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!CanUpdateControls || closeRequested) return;
                path.Text = downloadedPath;
                downloaded = true;
            }
            catch (OperationCanceledException) { if (CanUpdateControls) status.Text = "已取消下载。"; }
            catch (Exception ex) { if (CanUpdateControls) { status.Text = "音频下载未完成：" + ex.Message + "。可在 " + ProviderName + " 官方客户端取得普通音频后转录。"; status.ForeColor = Theme.Warm; } }
            finally { FinishOperation(operation); }
            if (downloaded && CanUpdateControls && !closeRequested) StartConversion();
        }
        private void OpenOfficialDownload()
        {
            OpenUrl(remoteSong == null ? MusicAudioSources.OfficialSearchUrl(ProviderName, songTitle) : MusicAudioSources.OfficialSongUrl(remoteSong));
            monitoringDownloads = true; observedFiles.Clear();
            ShowWaitingForDownload();
        }
        private void ShowWaitingForDownload()
        {
            string prefix = noDirectAudio ? "所选版本未提供直接下载。" : "";
            status.Text = prefix + (Directory.Exists(downloadFolder.Text) ? "请在 " + ProviderName + " 官方登录下载；正在等待目录中的同版本普通音频…" : "点击“" + ProviderName + " 登录 / 下载”，再指定下载目录；完成后自动转录。");
            status.ForeColor = Theme.Warm;
        }
        private void DetectDownloadedAudio()
        {
            if (!CanUpdateControls || closeRequested || !monitoringDownloads || working || !Directory.Exists(downloadFolder.Text) || remoteSong == null) return;
            try
            {
                List<string> candidates = SongAudioDownload.FindMatchingFiles(downloadFolder.Text, remoteSong);
                if (candidates.Count > 1) { status.Text = "发现多份同名音频，请点击“选择音频”确认版本。"; return; }
                if (candidates.Count == 0) return;
                string candidate = candidates[0]; var info = new FileInfo(candidate);
                string stamp = info.Length.ToString() + ":" + info.LastWriteTimeUtc.Ticks.ToString();
                string previous;
                if (!observedFiles.TryGetValue(candidate, out previous) || previous != stamp) { observedFiles[candidate] = stamp; return; }
                if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds < 2) return;
                using (var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None))
                { byte[] header = new byte[32]; int read = stream.Read(header, 0, header.Length); Array.Resize(ref header, read); if (!SongAudioDownload.ValidateAudioHeader(header)) throw new InvalidDataException("文件不是可直接读取的普通音频。"); }
                monitoringDownloads = false; path.Text = candidate; convert.Enabled = true;
                StartConversion();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { status.Text = "下载目录暂时无法读取，可点击“选择音频”导入文件。"; }
            catch (Exception ex) { monitoringDownloads = false; status.Text = "无法直接转录：" + ex.Message; status.ForeColor = Theme.Warm; }
        }
        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "无法打开 " + ProviderName + " 官方页面：" + ex.Message; }
        }
        private void UpdateProgress(string text)
        {
            if (!CanUpdateControls || closeRequested || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (CanUpdateControls && !closeRequested && working) status.Text = text; })); }
            catch (InvalidOperationException) { }
        }
    }
}
