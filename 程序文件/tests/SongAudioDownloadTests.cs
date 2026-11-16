using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using HarmonicaStudio;

public static class SongAudioDownloadTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        byte[] wav = new byte[48]; Array.Copy(Encoding.ASCII.GetBytes("RIFF"), wav, 4); wav[4] = 40;
        Array.Copy(Encoding.ASCII.GetBytes("WAVEfmt "), 0, wav, 8, 8);
        byte[] mp3 = new byte[48]; mp3[0] = 0xff; mp3[1] = 0xfb; mp3[2] = 0x90; mp3[3] = 0x64;
        byte[] id3 = new byte[48]; Array.Copy(Encoding.ASCII.GetBytes("ID3"), id3, 3); id3[3] = 3;
        byte[] flac = new byte[48]; Array.Copy(Encoding.ASCII.GetBytes("fLaC"), flac, 4);
        byte[] ogg = new byte[48]; Array.Copy(Encoding.ASCII.GetBytes("OggS"), ogg, 4);
        foreach (byte[] valid in new byte[][] { wav, mp3, id3, flac, ogg }) Assert(SongAudioDownload.ValidateAudioHeader(valid), "识别普通音频格式魔数。");
        foreach (byte[] invalid in new byte[][] { null, new byte[3], Encoding.UTF8.GetBytes("<html>需要登录</html>"), Encoding.UTF8.GetBytes("{\"error\":\"VIP\"}"), new byte[] { 0xff, 0xff, 0xff, 0xff }, new byte[] { 0xff, 0xe0, 0, 0 } })
            Assert(!SongAudioDownload.ValidateAudioHeader(invalid), "网页、JSON、无效MPEG与受保护字节不是普通音频。");
        Assert(SongAudioDownload.IsSupported("歌曲.MP3") && SongAudioDownload.IsSupported("歌曲.wav") && SongAudioDownload.IsSupported("歌曲.flac") && SongAudioDownload.IsSupported("歌曲.ogg"), "支持四种普通扩展名。");
        Assert(!SongAudioDownload.IsSupported("歌曲.qmc0") && !SongAudioDownload.IsSupported("歌曲.mflac") && !SongAudioDownload.IsSupported(null), "受保护扩展名不可改名导入。");
        Uri parsed;
        Assert(SongAudioDownload.TryPublicHttpsUri("https://music.example/song?signature=example", out parsed), "允许用户提供的普通公开HTTPS地址。");
        foreach (string invalid in new string[] { "http://music.example/song.mp3", "https://user@music.example/song.mp3", "https://127.0.0.1/song.mp3", "https://192.168.1.1/song.mp3", "https://[::1]/song.mp3", "https://[fc00::1]/song.mp3", "https://music.example/song.qmc0", "https://music.example/song.mflac", "https://music.example/song.mp3#secret" })
            Assert(!SongAudioDownload.TryPublicHttpsUri(invalid, out parsed), "拒绝非普通公开HTTPS音频地址。");
        string root = Path.Combine(Path.GetTempPath(), "harmonica-download-check-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "music");
        Directory.CreateDirectory(root); Directory.CreateDirectory(folder);
        try
        {
            string[] suffixes = new string[] { ".wav", ".mp3", ".mp3", ".flac", ".ogg" };
            byte[][] fixtures = new byte[][] { wav, mp3, id3, flac, ogg };
            for (int i = 0; i < fixtures.Length; i++)
            {
                string downloaded = SongAudioDownload.Download("https://music.example/download?id=1", "../测试:歌曲", root, null, CancellationToken.None,
                    delegate(Uri uri, int timeout, CancellationToken token) { return Response(new FragmentedStream(fixtures[i], 2), fixtures[i].Length); }, 1024, 5000);
                Assert(Path.GetDirectoryName(downloaded) == root && Path.GetExtension(downloaded) == suffixes[i], "只在目标目录落地，后缀由普通音频魔数确定。");
                Assert(File.Exists(downloaded) && File.ReadAllBytes(downloaded).Length == fixtures[i].Length, "短分包的真实完整音频流下载成功。");
                Assert(Directory.GetFiles(root, "*.partial").Length == 0, "成功下载不留partial。");
            }
            TrackingStream invalidStream = new TrackingStream(Encoding.UTF8.GetBytes("<html>下载需要登录或付费</html>"));
            Throws<InvalidDataException>(delegate { Download(root, delegate { return Response(invalidStream, -1); }); }, "HTML不得存为MP3。");
            Assert(invalidStream.Disposed && Directory.GetFiles(root, "*.partial").Length == 0, "格式错误关闭响应并清理本次临时文件。");
            Throws<InvalidDataException>(delegate { Download(root, delegate { AudioDownloadResponse response = Response(new MemoryStream(wav), wav.Length); response.ContentType = "application/json"; return response; }); }, "错误内容类型不能冒充普通音频。");
            TrackingStream tooLarge = new TrackingStream(wav);
            Throws<InvalidDataException>(delegate { Download(root, delegate { return Response(tooLarge, (long)SongAudioDownload.MaximumDownloadBytes + 1); }); }, "声明超过128MB时读正文前拒绝。");
            Assert(tooLarge.TotalRead == 0 && tooLarge.Disposed, "超大声明不浪费下载流量且释放响应。");
            Throws<InvalidDataException>(delegate { SongAudioDownload.Download("https://music.example/song", "歌曲", root, null, CancellationToken.None,
                delegate { return Response(new MemoryStream(wav), -1); }, 32, 5000); }, "无Content-Length的流仍执行硬大小边界。");
            Throws<InvalidDataException>(delegate { Download(root, delegate { return Response(new MemoryStream(wav), wav.Length + 1); }); }, "声明长度与收到长度不同则不接受半文件。");
            Throws<InvalidOperationException>(delegate { Download(root, delegate { AudioDownloadResponse response = Response(new MemoryStream(wav), wav.Length); response.StatusCode = 403; return response; }); }, "HTTP权限失败不给出下载成功。");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                Throws<OperationCanceledException>(delegate { SongAudioDownload.Download("https://music.example/song", "歌曲", root,
                    delegate { cancellation.Cancel(); }, cancellation.Token, delegate { return Response(new MemoryStream(wav), wav.Length); }, 1024, 5000); }, "下载中取消应终止并清理文件。");
            }
            Assert(Directory.GetFiles(root, "*.partial").Length == 0, "取消和异常均不留下临时下载。");
            Throws<TimeoutException>(delegate { SongAudioDownload.Download("https://music.example/song", "歌曲", root, null, CancellationToken.None,
                delegate { return Response(new SlowStream(wav), wav.Length); }, 1024, 20); }, "总耗时限制包括正文读取。");
            int calls = 0;
            string redirected = SongAudioDownload.Download("https://music.example/start", "歌曲", root, null, CancellationToken.None,
                delegate(Uri uri, int timeout, CancellationToken token)
                {
                    calls++; if (calls == 1) return new AudioDownloadResponse { StatusCode = 302, Location = "https://cdn.example/audio" };
                    return Response(new MemoryStream(wav), wav.Length);
                }, 1024, 5000);
            Assert(calls == 2 && File.Exists(redirected), "普通用户HTTPS链接可跟随公开HTTPS跳转。");
            Throws<InvalidDataException>(delegate { Download(root, delegate { return new AudioDownloadResponse { StatusCode = 302, Location = "http://music.example/audio" }; }); }, "禁止跳转降级HTTP。");
            Throws<InvalidDataException>(delegate { SongAudioDownload.Download("https://sjy6.stream.qqmusic.qq.com/M500Song.mp3", "歌曲", root, null, CancellationToken.None,
                delegate { return new AudioDownloadResponse { StatusCode = 302, Location = "https://cdn.example/audio.mp3" }; }, 1024, 5000); }, "QQ签名音频不跟随外部来源。");
            const string kugouFixture = "https://sharefs.kugou.com/202610020055/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/v3/ee73e142321addec9317db43a5c776e4/yp/full/ap1000_us0_pi409_s4177514493.mp3";
            Throws<InvalidDataException>(delegate { SongAudioDownload.Download(kugouFixture, "歌曲", root, null, CancellationToken.None,
                delegate { return new AudioDownloadResponse { StatusCode = 302, Location = "https://cdn.example/audio.mp3" }; }, 1024, 5000); }, "酷狗完整音频不跟随外部来源。");
            int kugouCalls = 0;
            string kugouRedirected = SongAudioDownload.Download(kugouFixture, "歌曲", root, null, CancellationToken.None,
                delegate
                {
                    if (++kugouCalls == 1) return new AudioDownloadResponse { StatusCode = 302, Location = kugouFixture.Replace("sharefs.kugou.com", "sharefs.tx.kugou.com") };
                    return Response(new MemoryStream(mp3), mp3.Length);
                }, 1024, 5000);
            Assert(kugouCalls == 2 && File.Exists(kugouRedirected), "酷狗可在已验证的完整音频备用域之间跳转并保存普通音频。");
            int loopCalls = 0;
            Throws<InvalidDataException>(delegate { Download(root, delegate { loopCalls++; return new AudioDownloadResponse { StatusCode = 302, Location = "/again" }; }); }, "限制重定向循环。");
            Assert(loopCalls == 6, "最多五次跳转后终止。");
            Assert(Directory.GetFiles(root, "*.partial").Length == 0, "所有下载失败的partial都已清理。");

            Write(folder, "周杰伦 - 晴天.mp3", mp3);
            Write(folder, "晴天 - 周杰伦.wav", wav);
            Write(folder, "晴天.mp3", mp3);
            Write(folder, "刘瑞琦 - 晴天.mp3", mp3);
            Write(folder, "周杰伦 - 晴天 (Live).mp3", mp3);
            Write(folder, "周杰伦 - 小晴天.mp3", mp3);
            Write(folder, "周杰伦 - 晴天 (Cover).mp3", mp3);
            Write(folder, "周杰伦_晴天.flac", flac);
            Write(folder, "周杰伦 — 晴天.ogg", ogg);
            Write(folder, "周杰伦 - 晴天.qmc0", mp3);
            Write(folder, "周杰伦 - 晴天假文件.mp3", Encoding.UTF8.GetBytes("<html>网页</html>"));
            string nested = Path.Combine(folder, "nested"); Directory.CreateDirectory(nested); Write(nested, "周杰伦 - 晴天.mp3", mp3);
            OnlineSong song = new OnlineSong { Title = "晴天", Artist = "周杰伦" };
            List<string> matches = SongAudioDownload.FindMatchingFiles(folder, song);
            Assert(matches.Count == 4, "自动匹配只接受同曲同歌手完整名称与普通音频。");
            Assert(!matches.Exists(delegate(string file) { return Path.GetFileName(file) == "晴天.mp3"; }), "有歌手时标题only文件留给手动选择。");
            Assert(SongAudioDownload.FindMatchingFiles(folder, new OnlineSong { Title = "晴天 (Live)", Artist = "周杰伦" }).Count == 1, "现场版本不替换为原版。");
            Assert(SongAudioDownload.FindMatchingFiles(folder, new OnlineSong { Title = "晴天 (Cover)", Artist = "周杰伦" }).Count == 1, "翻唱版本词不被移除。");
            Assert(SongAudioDownload.FindMatchingFiles(folder, new OnlineSong { Title = "晴天" }).Count == 1, "未知歌手只接受完全标题文件。");
            Assert(SongAudioDownload.FindMatchingFiles(Path.Combine(root, "missing"), song).Count == 0, "目录不存在正常无候选。");
            Assert(SongAudioDownload.FindMatchingFiles(folder, null).Count == 0, "未选歌曲正常无候选。");
            string invalidFolder = Path.Combine(root, "invalid"); Directory.CreateDirectory(invalidFolder);
            Write(invalidFolder, "周杰伦 - 晴天.mp3", Encoding.UTF8.GetBytes("<html>歌曲下载失败</html>"));
            Assert(SongAudioDownload.FindMatchingFiles(invalidFolder, song).Count == 0, "同名HTML假音频不进入自动转换。");
            string many = Path.Combine(root, "many"); Directory.CreateDirectory(many);
            for (int i = 0; i < 25; i++) Write(many, "周杰伦" + new string(' ', i + 1) + "- 晴天.mp3", mp3);
            Assert(SongAudioDownload.FindMatchingFiles(many, song).Count == 20, "最多返回二十候选。");
            Assert(SongAudioDownload.MaximumScannedFiles == 1000 && SongAudioDownload.MaximumDownloadBytes == 128 * 1024 * 1024 && SongAudioDownload.TimeoutMilliseconds == 120000, "生产扫描、大小与总时长边界明确。");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("普通音频下载及歌曲文件匹配测试通过，共 " + checks + " 项断言（仅模拟下载，不连接网络）。");
        return checks;
    }

    private static string Download(string root, Func<AudioDownloadResponse> open)
    { return SongAudioDownload.Download("https://music.example/song", "歌曲", root, null, CancellationToken.None, delegate { return open(); }, 1024, 5000); }
    private static AudioDownloadResponse Response(Stream stream, long length)
    { return new AudioDownloadResponse { StatusCode = 200, Body = stream, ContentLength = length, ContentType = "application/octet-stream" }; }
    private static void Write(string folder, string filename, byte[] bytes) { File.WriteAllBytes(Path.Combine(folder, filename), bytes); }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
    private class TrackingStream : MemoryStream
    {
        public bool Disposed; public int TotalRead;
        public TrackingStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) { int read = base.Read(buffer, offset, count); TotalRead += read; return read; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class FragmentedStream : MemoryStream
    {
        private readonly int chunk;
        public FragmentedStream(byte[] bytes, int chunk) : base(bytes) { this.chunk = chunk; }
        public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(count, chunk)); }
    }
    private sealed class SlowStream : MemoryStream
    {
        public SlowStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(40); return base.Read(buffer, offset, count); }
    }
}
