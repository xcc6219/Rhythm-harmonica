using System;
using System.IO;
using System.Net;
using System.Text;
using HarmonicaStudio;

public static class OnlineCatalogTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        string row = "<table class=\"table-bordered result-table\">" +
            "<tr><td>Ode &amp; Joy</td><td>by Beethoven</td><td>&nbsp;</td><td></td></tr>" +
            "<tr><td>for Voice</td><td>1800</td><td>Hymn</td><td></td></tr>" +
            "<tr><td>Various</td><td><a href=\"../legal.html#publicdomain\">Public Domain</a></td>" +
            "<td><a href=\"piece-info.cgi?id=528\">More Information</a></td><td>2009</td></tr>" +
            "<tr><td>Download</td><td><a href=\"https://www.mutopiaproject.org/ftp/BeethovenLv/ode/ode.mid\">.mid</a></td></tr></table>";
        FreeScorePage page = FreeScoreCatalog.ParsePage(row, 1);
        Assert(page.Items.Count == 1 && page.Items[0].Title == "Ode & Joy", "解析真实列表结构与实体。");
        Assert(page.Items[0].License == "Public Domain" && page.Items[0].SourceEdition == "Various", "保留许可与版本来源。");
        Assert(page.Items[0].InfoUrl == "https://www.mutopiaproject.org/cgibin/piece-info.cgi?id=528", "解析相对信息页链接。");
        Assert(page.Items[0].MidiUrl.EndsWith("/ode.mid") && !page.HasNext, "MID直链及末页。");
        FreeScoreCatalog.ApplyAttribution(page.Items[0], "<td><b>Maintainer:</b> Peter Chubb</td><td>private@example.com</td>");
        Assert(page.Items[0].Attribution.Contains("Maintainer: Peter Chubb") && !page.Items[0].Attribution.Contains("private@"), "署名保留维护者且不夹带其他表格内容。");
        string next = "<a href=\"make-table.cgi?startat=10&amp;searchingfor=ODE\">Next 10</a>";
        page = FreeScoreCatalog.ParsePage(row.Replace("/ode.mid", "/ode-mids.zip") + next, 1);
        Assert(page.Items.Count == 0 && page.SkippedArchives == 1 && page.HasNext, "跳过ZIP但保留源页下一页。");
        page = FreeScoreCatalog.ParsePage(row.Replace("https://www.mutopiaproject.org/ftp/", "https://evil.example/ftp/"), 1);
        Assert(page.Items.Count == 0, "拒绝外部MID域名。");
        Assert(FreeScoreCatalog.ParsePage("<div>Sorry, no matches were found for your search criteria.</div>", 1).Items.Count == 0, "识别明确无结果。");
        Throws<InvalidDataException>(delegate { FreeScoreCatalog.ParsePage("<html>维护中</html>", 1); }, "页面变更不可误报无结果。");
        Uri uri;
        Assert(!FreeScoreCatalog.TryOfficialUri("https://www.mutopiaproject.org.evil.example/ftp/a.mid", out uri), "拒绝域名后缀冒充。");
        Assert(!FreeScoreCatalog.TryOfficialUri("https://user@www.mutopiaproject.org/ftp/a.mid", out uri), "拒绝用户名网址。");
        Assert(!FreeScoreCatalog.TryOfficialUri("http://www.mutopiaproject.org/ftp/a.mid", out uri), "拒绝HTTP。");
        Assert(!FreeScoreCatalog.TryOfficialUri("https://www.mutopiaproject.org:444/ftp/a.mid", out uri), "拒绝其他端口。");
        Assert(FreeScoreCatalog.OfficialSearchUrl("欢乐颂").EndsWith("Ode%20to%20Joy"), "中文常见曲名别名。");
        Assert(FreeScoreCatalog.OfficialSearchUrl("").EndsWith("searchingfor="), "允许空词浏览。");
        Throws<ArgumentException>(delegate { FreeScoreCatalog.OfficialSearchUrl(new string('x', 101)); }, "限制搜索长度。");
        Throws<ArgumentException>(delegate { FreeScoreCatalog.OfficialSearchUrl("x\ny"); }, "拒绝搜索控制字符。");
        Throws<InvalidDataException>(delegate { FreeScoreCatalog.ValidateMidi(Encoding.UTF8.GetBytes("<html>login</html>")); }, "HTML不可假装MIDI。");
        byte[] midi = new byte[] { 77,84,104,100,0,0,0,6,0,0,0,1,0,96,77,84,114,107,0,0,0,12,0,144,60,100,96,128,60,0,0,255,47,0 };
        FreeScoreCatalog.ValidateMidi(midi);
        Assert(true, "下载验证通过实际ScoreIO解析。");
        byte[] truncated = new byte[14];
        Array.Copy(midi, truncated, truncated.Length);
        Throws<InvalidDataException>(delegate { FreeScoreCatalog.ValidateMidi(truncated); }, "MThd头有效仍须完整解析。");
        DownloadFaults(midi);
        var songs = KugouCatalog.ParseResponse("{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[{\"SongName\":\"<em>测试</em> &amp; 标题\",\"SingerName\":\"歌手\",\"Duration\":128,\"MixSongID\":\"123\"}]}}");
        Assert(songs.Count == 1 && songs[0].Title == "测试 & 标题" && songs[0].DurationSeconds == 128, "酷狗元数据高亮还原。");
        Assert(KugouCatalog.ParseResponse("{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[]}}").Count == 0, "酷狗正常无结果。");
        Throws<InvalidOperationException>(delegate { KugouCatalog.ParseResponse("{\"status\":0,\"error_code\":152}"); }, "酷狗拒绝匿名访问。");
        Throws<InvalidDataException>(delegate { KugouCatalog.ParseResponse("{\"status\":1,\"error_code\":0,\"data\":{}}"); }, "酷狗页面变更不假装无结果。");
        Console.WriteLine("在线曲库测试通过，共 " + checks + " 项断言（未连接网络）。");
        return checks;
    }

    private static void DownloadFaults(byte[] midi)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mutopia-cache-" + Guid.NewGuid().ToString("N"));
        var score = new FreeScore { Title = "Ode to Joy", Composer = "Beethoven", License = "Public Domain",
            SourceEdition = "Various", LicenseUrl = "https://www.mutopiaproject.org/legal.html#publicdomain",
            InfoUrl = "https://www.mutopiaproject.org/cgibin/piece-info.cgi?id=528",
            MidiUrl = "https://www.mutopiaproject.org/ftp/BeethovenLv/ode/ode.mid", Attribution = "Mutopia Project；Beethoven；来源" };
        try
        {
            int requests = 0;
            byte[] actual = FreeScoreCatalog.Download(score, directory, delegate(Uri url, int limit)
            {
                requests++;
                if (url.AbsolutePath.EndsWith("piece-info.cgi")) throw new WebException("metadata timeout", WebExceptionStatus.Timeout);
                return midi;
            });
            Assert(actual.Length == midi.Length && requests == 2, "补充署名页超时不阻断真实MIDI。");
            Assert(score.License == "Public Domain" && score.SourceEdition == "Various" &&
                score.LicenseUrl.EndsWith("#publicdomain") && score.Attribution.Contains("Beethoven"), "信息页失败保留既有许可来源与署名。");
            requests = 0;
            actual = FreeScoreCatalog.Download(score, directory, delegate(Uri url, int limit)
            {
                requests++;
                if (url.AbsolutePath.EndsWith("piece-info.cgi")) throw new WebException("offline", WebExceptionStatus.ConnectFailure);
                throw new Exception("MIDI不得再次请求");
            });
            Assert(actual.Length == midi.Length && requests == 1, "补充页面失败时仍可复用已验MIDI缓存。");
            string path = Path.Combine(directory, CatalogDownloads.CacheKey("mutopia", score.MidiUrl));
            File.WriteAllText(path, "MThd");
            int midiRequests = 0;
            actual = FreeScoreCatalog.Download(score, directory, delegate(Uri url, int limit)
            {
                if (url.AbsolutePath.EndsWith("piece-info.cgi")) return Encoding.UTF8.GetBytes("<td>Maintainer: Peter Chubb</td>");
                midiRequests++;
                if (midiRequests == 1) throw new WebException("interrupted", WebExceptionStatus.ReceiveFailure);
                return midi;
            });
            Assert(midiRequests == 2 && actual.Length == midi.Length, "损坏缓存与连接中断可一次重试恢复。");
            Assert(score.Attribution.Contains("Peter Chubb"), "可用信息页依旧保留贡献者署名。");
            File.Delete(path);
            midiRequests = 0;
            Throws<InvalidOperationException>(delegate
            {
                FreeScoreCatalog.Download(score, directory, delegate(Uri url, int limit)
                {
                    if (url.AbsolutePath.EndsWith("piece-info.cgi")) return Encoding.UTF8.GetBytes("<html>no credit</html>");
                    midiRequests++; throw CatalogDownloads.DescribeFailure(new WebException("TLS", WebExceptionStatus.SecureChannelFailure));
                });
            }, "Mutopia安全连接失败不掩盖为缺谱。");
            Assert(midiRequests == 1 && !File.Exists(path), "安全错误不重试也不缓存。");
            string original = score.MidiUrl;
            score.MidiUrl += "?other=1";
            Throws<ArgumentException>(delegate { FreeScoreCatalog.Download(score, directory, delegate { throw new Exception("不得联网"); }); }, "下载拒绝附加查询。");
            score.MidiUrl = original;
            Throws<InvalidDataException>(delegate
            {
                FreeScoreCatalog.Download(score, directory, delegate(Uri url, int limit)
                { return Encoding.UTF8.GetBytes("<html>error</html>"); });
            }, "MIDI HTML响应不可当作乐谱。");
            Assert(!File.Exists(path), "无效Mutopia文件不写缓存。");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        checks++;
        try { action(); }
        catch (T) { return; }
        throw new Exception(message);
    }
}
