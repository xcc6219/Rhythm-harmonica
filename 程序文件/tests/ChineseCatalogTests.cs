using System;
using System.IO;
using System.Net;
using System.Text;
using HarmonicaStudio;

public static class ChineseCatalogTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        const string json = "{\"songs\":[{\"id\":\"516\",\"title\":\"晴天\",\"artist\":\"周杰伦\"}," +
            "{\"id\":\"292\",\"title\":\"夜曲\",\"artist\":\"周杰伦\"}," +
            "{\"id\":\"528\",\"title\":\"月半小夜曲\",\"artist\":\"陈慧娴\"}," +
            "{\"id\":\"035\",\"title\":\"七里香\",\"artist\":\"刘瑞琦\"}," +
            "{\"id\":\"034\",\"title\":\"七里香\",\"artist\":\"另一位歌手\"}]}";
        Assert(ChineseScoreCatalog.SearchFrom(json, "").Count == 5, "空词浏览实际索引。");
        Assert(ChineseScoreCatalog.SearchFrom(json, "周杰倫").Count == 2, "歌手常见繁简匹配。");
        Assert(ChineseScoreCatalog.SearchFrom(json, "夜曲").Count == 2, "搜索可发现包含关键词的候选。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "夜曲", "").Count == 1, "自动匹配不得将夜曲换成小夜曲。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "晴天（Live）", "周杰倫").Count == 1, "明确版本标签与繁体歌手规范化。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "晴天(伴奏)", "周杰伦").Count == 1, "伴奏标签可关联旋律候选。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "晴天(不确定版本)", "周杰伦").Count == 0, "未知括号文本不可默认为相同曲名。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "七里香", "").Count == 2, "同名不同歌手保留多个选择。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "七里香", "周杰伦").Count == 0, "翻唱歌手不可冒充原唱版本。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "七里香", "劉瑞琦")[0].Id == "035", "歌手精确匹配保留正确MID编号。");
        Assert(ChineseScoreCatalog.FindExactFrom(json, "稻香", "周杰伦").Count == 0, "无结果保持为空。");
        ChineseScore song = ChineseScoreCatalog.SearchFrom(json, "晴天")[0];
        Assert(song.License == "POP909 · 仓库标注 MIT" && song.Attribution.Contains("Music X Lab (2020)") &&
            song.Attribution.Contains("周杰伦") && song.Attribution.Contains(song.SourceUrl), "源许可与署名透传，不推断公版。");
        Assert(song.MidiUrl == "https://raw.githubusercontent.com/music-x-lab/POP909-Dataset/master/POP909/516/516.mid", "真实单曲路径由索引编号生成。");
        Uri uri;
        Assert(ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl, out uri), "认可准确源路径。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl.Replace("https:", "http:"), out uri), "拒绝HTTP。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl.Replace("raw.githubusercontent.com", "raw.githubusercontent.com.evil.example"), out uri), "拒绝冒充主机。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl.Replace("raw.githubusercontent.com", "user@raw.githubusercontent.com"), out uri), "拒绝用户信息网址。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl.Replace("/music-x-lab/", "/other/"), out uri), "拒绝其他仓库。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl.Replace("516.mid", "035.mid"), out uri), "拒绝编号与文件不一致。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl + "?download=1", out uri), "拒绝额外查询。");
        Assert(!ChineseScoreCatalog.TryMidiUri(song.Id, song.MidiUrl + "#x", out uri), "拒绝片段。");
        Assert(!ChineseScoreCatalog.TryMidiUri("../", song.MidiUrl, out uri), "拒绝路径注入编号。");
        Throws<ArgumentException>(delegate { ChineseScoreCatalog.SearchFrom(json, new string('a', 101)); }, "限制搜索长度。");
        Throws<ArgumentException>(delegate { ChineseScoreCatalog.SearchFrom(json, "x\ny"); }, "拒绝搜索控制字符。");
        Throws<InvalidDataException>(delegate { ChineseScoreCatalog.SearchFrom("{\"songs\":{}}", ""); }, "列表格式异常不可假装无结果。");
        Throws<InvalidDataException>(delegate { ChineseScoreCatalog.SearchFrom(json.Replace("\"034\"", "\"035\""), ""); }, "拒绝重复歌曲编号。");
        Throws<InvalidDataException>(delegate { ChineseScoreCatalog.SearchFrom(json.Replace("\"516\"", "\"999\""), ""); }, "拒绝超出源索引编号。");
        Throws<InvalidDataException>(delegate { ChineseScoreCatalog.ValidateMidi(Encoding.UTF8.GetBytes("<html>download</html>")); }, "HTML不得伪装MIDI。");
        byte[] midi = new byte[] { 77,84,104,100,0,0,0,6,0,0,0,1,0,96,77,84,114,107,0,0,0,12,0,144,60,100,96,128,60,0,0,255,47,0 };
        ChineseScoreCatalog.ValidateMidi(midi);
        Assert(true, "标准MIDI通过完整引擎读取。");
        byte[] truncated = new byte[14];
        Array.Copy(midi, truncated, truncated.Length);
        Throws<InvalidDataException>(delegate { ChineseScoreCatalog.ValidateMidi(truncated); }, "仅有效MThd头不足以作为谱。");
        DownloadFaults(song, midi);
        Console.WriteLine("中文曲库测试通过，共 " + checks + " 项断言（未连接网络）。");
        return checks;
    }

    private static void DownloadFaults(ChineseScore song, byte[] midi)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            int requests = 0;
            byte[] actual = ChineseScoreCatalog.Download(song, directory, delegate(Uri url, int limit)
            {
                requests++;
                Assert(limit == ChineseScoreCatalog.MaximumMidiBytes, "备用请求仍限制16MB。");
                if (url.Host == "raw.githubusercontent.com") throw new WebException("DNS", WebExceptionStatus.NameResolutionFailure);
                Assert(url.AbsoluteUri == "https://api.github.com/repos/music-x-lab/POP909-Dataset/contents/POP909/516/516.mid?ref=master",
                    "RAW不可达后只请求同仓库官方Contents接口。");
                return midi;
            });
            Assert(requests == 2 && actual.Length == midi.Length, "原域故障可用官方备用恢复真实MIDI。");
            string path = Path.Combine(directory, "pop909-516.mid");
            Assert(File.Exists(path), "只缓存验证成功的MIDI。");
            actual = ChineseScoreCatalog.Download(song, directory, delegate { throw new Exception("不得联网"); });
            Assert(actual.Length == midi.Length, "完整缓存可在离线时复用。");
            File.WriteAllText(path, "<html>error</html>");
            requests = 0;
            ChineseScoreCatalog.Download(song, directory, delegate { requests++; return midi; });
            Assert(requests == 1 && File.ReadAllBytes(path)[0] == 'M', "损坏缓存重新下载并完整验证后更新。");

            File.Delete(path);
            Throws<InvalidOperationException>(delegate
            {
                ChineseScoreCatalog.Download(song, directory, delegate(Uri url, int limit)
                { throw new WebException("timeout", WebExceptionStatus.Timeout); });
            }, "两源网络失败保留下载异常，不伪装为空谱。");
            Assert(!File.Exists(path), "失败不写入缓存。");
            requests = 0;
            Throws<InvalidOperationException>(delegate
            {
                ChineseScoreCatalog.Download(song, directory, delegate(Uri url, int limit)
                { requests++; throw CatalogDownloads.DescribeFailure(new WebException("certificate", WebExceptionStatus.TrustFailure)); });
            }, "证书异常必须保留明确失败。");
            Assert(requests == 1, "安全连接错误不会换源重试。");
            Throws<InvalidOperationException>(delegate
            {
                ChineseScoreCatalog.Download(song, directory, delegate { return Encoding.UTF8.GetBytes("<html>gate</html>"); });
            }, "两源HTML响应不可缓存或载入。");
            Assert(!File.Exists(path), "无效备用响应未缓存。");
            Uri uri;
            string api = "https://api.github.com/repos/music-x-lab/POP909-Dataset/contents/POP909/516/516.mid?ref=master";
            Assert(ChineseScoreCatalog.TryDownloadUri(api, out uri), "白名单认可官方原字节接口。");
            Assert(!ChineseScoreCatalog.TryDownloadUri(api.Replace("516.mid", "035.mid"), out uri), "API编号不可跨文件。");
            Assert(!ChineseScoreCatalog.TryDownloadUri(api.Replace("ref=master", "ref=other"), out uri), "API不可换分支。");
            Assert(!ChineseScoreCatalog.TryDownloadUri(api.Replace("/music-x-lab/", "/other/"), out uri), "API不可跨仓库。");
            Assert(!ChineseScoreCatalog.TryDownloadUri(api.Replace("https:", "http:"), out uri), "API拒绝不安全协议。");
            string blockedCache = Path.Combine(directory, "not-directory");
            File.WriteAllText(blockedCache, "file");
            actual = ChineseScoreCatalog.Download(song, blockedCache, delegate { return midi; });
            Assert(actual.Length == midi.Length, "缓存目录不可写不影响有效下载。");
            Assert(CatalogDownloads.DescribeFailure(new WebException("DNS", WebExceptionStatus.NameResolutionFailure)).Message.Contains("域名") &&
                CatalogDownloads.DescribeFailure(new WebException("timeout", WebExceptionStatus.Timeout)).Message.Contains("超时"),
                "不同网络故障给出准确中文信息。");
            var http = new WebException("HTTP", WebExceptionStatus.ProtocolError);
            Assert(CatalogDownloads.DescribeFailure(http, 404).Message.Contains("404") &&
                !CatalogDownloads.DescribeFailure(http, 404).Retryable, "文件不存在不换源冒充或重试。");
            Assert(CatalogDownloads.DescribeFailure(http, 429).Message.Contains("限制") &&
                !CatalogDownloads.DescribeFailure(http, 403).Retryable, "限流和权限限制明确且不绕过。");
            Assert(CatalogDownloads.DescribeFailure(http, 503).Retryable, "官方谱源临时异常可走备用。");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
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
