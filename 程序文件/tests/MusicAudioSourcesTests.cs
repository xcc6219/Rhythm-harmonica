using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using HarmonicaStudio;

public static class MusicAudioSourcesTests
{
    private static int checks;
    private const string SongHash = "EE73E142321ADDEC9317DB43A5C776E4";
    private const string AudioUrl = "https://sharefs.kugou.com/202610020055/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/v3/ee73e142321addec9317db43a5c776e4/yp/full/ap1000_us0_pi409_s4177514493.mp3";

    public static int Run()
    {
        checks = 0;
        Dictionary<string, object> row = SearchRow();
        List<OnlineSong> songs = KugouCatalog.ParseResponse(SearchResponse(row));
        Assert(songs.Count == 1 && songs[0].Provider == MusicAudioSources.KugouProvider, "酷狗保留真实平台来源。");
        OnlineSong song = songs[0];
        Assert(song.Title == "茉莉花 (Live) & 现场" && song.Artist == "宋祖英", "保留中文歌名与现场版本。");
        Assert(song.Album == "现场专辑" && song.DurationSeconds == 249, "保留选中版本专辑与时长。");
        Assert(song.FileHash == SongHash && song.AlbumId == "2852128" && song.Id == "67422551", "歌曲编号、音频hash和专辑编号分别保留。");
        Assert(song.FreeDownload && song.SourceUrl == "https://www.kugou.com/song/#hash=" + SongHash + "&album_id=2852128", "全部明确免费条件才允许尝试普通完整音频。");
        foreach (string field in new string[] { "PayType", "Price", "Privilege", "FailProcess" })
        {
            Dictionary<string, object> restricted = SearchRow(); restricted[field] = 1;
            Assert(!KugouCatalog.ParseResponse(SearchResponse(restricted))[0].FreeDownload, "收费或受限标记阻止自动下载：" + field);
            restricted = SearchRow(); restricted.Remove(field);
            Assert(!KugouCatalog.ParseResponse(SearchResponse(restricted))[0].FreeDownload, "缺少权限不能默认免费：" + field);
        }
        row = SearchRow(); row["FileHash"] = "../other";
        OnlineSong incomplete = KugouCatalog.ParseResponse(SearchResponse(row))[0];
        Assert(!incomplete.FreeDownload && incomplete.FileHash == "" && incomplete.SourceUrl.StartsWith("https://www.kugou.com/yy/html/search.html#"), "异常音频hash保留搜索结果但禁止自动下载。");
        row = SearchRow(); row["AlbumID"] = "1&token=other";
        Assert(KugouCatalog.ParseResponse(SearchResponse(row))[0].AlbumId == "", "专辑编号不能向官网URL注入参数。");
        row = SearchRow(); row["ExtName"] = "kgm";
        Assert(!KugouCatalog.ParseResponse(SearchResponse(row))[0].FreeDownload, "受保护格式不能当普通MP3。");
        row = SearchRow(); row["FileHash"] = ""; row["SongName"] = new string('歌', 99) + "\U0001F3B5现场";
        Assert(KugouCatalog.ParseResponse(SearchResponse(row))[0].SourceUrl.StartsWith("https://www.kugou.com/yy/html/search.html#"), "长歌曲元数据不阻断列表也不截断Unicode代理对。");
        Throws<InvalidOperationException>(delegate { KugouCatalog.ParseResponse("{\"status\":0,\"error_code\":152}"); }, "匿名拒绝继续显示实际错误而不是空曲库。");
        Assert(KugouCatalog.ParseResponse("{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[]}}").Count == 0, "只有明确空列表表示无结果。");

        Dictionary<string, object> audio = AudioResponse();
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), song) == AudioUrl, "平台授予的普通完整HTTPS音频可直接使用。");
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), new OnlineSong { FreeDownload = false }) == null, "搜索阶段受限歌曲不接受任何普通流。");
        Assert(KugouCatalog.ResolveAudioUrl(new OnlineSong { Provider = "酷狗音乐", FreeDownload = false }) == null, "受限歌曲不发出匿名音频请求。");
        foreach (string field in new string[] { "pay_type", "privilege", "128privilege", "fail_process" })
        {
            Dictionary<string, object> restricted = AudioResponse(); restricted[field] = 3;
            Assert(KugouCatalog.ParseAudioResponse(Json(restricted), song) == null, "音频阶段再次核查权限：" + field);
            restricted = AudioResponse(); restricted.Remove(field);
            Assert(KugouCatalog.ParseAudioResponse(Json(restricted), song) == null, "未返回权限不可推断可下载：" + field);
        }
        audio = AudioResponse(); audio["status"] = 0;
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), song) == null, "官方拒绝获取音频不伪造成功。");
        audio = AudioResponse(); audio["status"] = 2;
        Throws<InvalidOperationException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "异常音频状态显示故障。");
        audio = AudioResponse(); audio["errcode"] = 152;
        Throws<InvalidOperationException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "非零音频错误码不能默认成功。");
        audio = AudioResponse(); audio.Remove("errcode");
        Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "缺少错误码属于格式变化。");
        foreach (string field in new string[] { "hash", "req_hash" })
        {
            audio = AudioResponse(); audio[field] = new string('F', 32);
            Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "不得下载其他歌曲hash：" + field);
        }
        audio = AudioResponse(); audio["timeLength"] = 30;
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), song) == null, "只有30秒试听授权不当完整249秒歌曲下载。");
        audio = AudioResponse(); audio["timeLength"] = 0;
        Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "零时长不是有效完整音频。");
        audio = AudioResponse(); audio["extName"] = "kgma";
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), song) == null, "不解密受保护音频格式。");
        audio = AudioResponse(); audio["url"] = "";
        Assert(KugouCatalog.ParseAudioResponse(Json(audio), song) == null, "空音频地址表示需要官方方式下载。");
        foreach (string badUrl in new string[]
        {
            AudioUrl.Replace("https://", "http://"), AudioUrl.Replace("sharefs.kugou.com", "sharefs.kugou.com.evil.example"),
            AudioUrl.Replace("sharefs.kugou.com", "example.com"), AudioUrl.Replace("https://", "https://user@"),
            AudioUrl.Replace("sharefs.kugou.com/", "sharefs.kugou.com:444/"), AudioUrl + "#fragment",
            AudioUrl.Replace("/full/", "/preview/"), AudioUrl.Replace(".mp3", ".kgm"),
            AudioUrl.Replace("ee73e142321addec9317db43a5c776e4", new string('b', 32)), "/song.mp3"
        })
        {
            audio = AudioResponse(); audio["url"] = badUrl;
            Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse(Json(audio), song); }, "拒绝异常、试听或替换歌曲的音频URL。");
        }
        Assert(KugouCatalog.IsOrdinaryAudioUri(new Uri(AudioUrl)), "允许实测主音频域HTTPS完整MP3路径。");
        Assert(KugouCatalog.IsOrdinaryAudioUri(new Uri(AudioUrl.Replace("sharefs.kugou.com", "sharefs.tx.kugou.com"))), "允许已实测备份音频域。");
        Assert(!KugouCatalog.IsOrdinaryAudioUri(null) && !KugouCatalog.IsOrdinaryAudioUri(new Uri("song.mp3", UriKind.Relative)), "拒绝空和相对音频地址。");
        Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse("<html>登录</html>", song); }, "登录HTML不当音频JSON。");
        Throws<InvalidDataException>(delegate { KugouCatalog.ParseAudioResponse(new string('x', KugouCatalog.MaximumResponseBytes + 1), song); }, "限制音频元数据大小。");

        Assert(MusicAudioSources.DisplayName(song) == "酷狗音乐" && MusicAudioSources.DisplayName(new OnlineSong()) == "QQ音乐", "统一平台名称并兼容旧QQ数据。");
        Assert(MusicAudioSources.OfficialSongUrl(song) == song.SourceUrl, "酷狗统一入口指向选中hash与专辑。");
        Assert(MusicAudioSources.OfficialSongUrl(new OnlineSong { Provider = "酷狗音乐", Title = "茉莉花", Artist = "宋祖英" }).Contains("searchKeyWord="), "酷狗无hash使用官方标题检索。");
        Assert(MusicAudioSources.OfficialSongUrl(new OnlineSong { Id = "0048Ts0Q0oi3Ti" }) == "https://y.qq.com/n/ryqq/songDetail/0048Ts0Q0oi3Ti", "QQ统一入口保留原歌曲编号。");
        Assert(MusicAudioSources.OfficialSongUrl(new OnlineSong { Id = "../invalid", Title = "茉莉花", Artist = "宋祖英" }).StartsWith("https://y.qq.com/portal/search.html#"), "QQ无有效编号使用官方标题检索。");
        Assert(MusicAudioSources.OfficialSongUrl(new OnlineSong { Title = new string('歌', 99) + "\U0001F3B5现场" }).StartsWith("https://y.qq.com/portal/search.html#"), "QQ长元数据官网兜底保持有效Unicode。");
        Assert(MusicAudioSources.OfficialSearchUrl("酷狗音乐", "茉莉花 & 现场").Contains("%26"), "统一酷狗搜索正确编码特殊字符。");
        Assert(MusicAudioSources.OfficialSearchUrl("QQ音乐", "").Equals("https://y.qq.com/"), "统一QQ空搜索打开官网。");
        Assert(MusicAudioSources.OfficialDownloadUrl("酷狗音乐") == "https://download.kugou.com/" && MusicAudioSources.OfficialDownloadUrl("QQ音乐") == QqMusicCatalog.OfficialDownloadUrl, "两平台客户端采用已核实官方地址。");
        Assert(MusicAudioSources.ResolveAudioUrl(new OnlineSong { Provider = "酷狗音乐", FreeDownload = false }) == null && MusicAudioSources.ResolveAudioUrl(new OnlineSong { Provider = "QQ音乐", FreeDownload = false }) == null, "两平台受限歌曲不会发下载请求。");
        Throws<ArgumentException>(delegate { MusicAudioSources.Search("搜狗", "茉莉花", 1); }, "不把其他搜索来源伪装音乐平台。");
        Throws<ArgumentException>(delegate { MusicAudioSources.OfficialDownloadUrl("外部网站"); }, "不静默退到错误来源。");
        Throws<ArgumentNullException>(delegate { MusicAudioSources.ResolveAudioUrl(null); }, "空歌曲不能发下载请求。");
        Throws<ArgumentNullException>(delegate { MusicAudioSources.OfficialSongUrl(null); }, "空歌曲不能生成来源链接。");
        Console.WriteLine("QQ音乐与酷狗统一来源测试通过，共 " + checks + " 项断言（未连接网络）。");
        return checks;
    }

    private static Dictionary<string, object> SearchRow()
    {
        return new Dictionary<string, object>
        {
            { "SongName", "<em>茉莉花</em> (Live) &amp; 现场" }, { "SingerName", "宋祖英" },
            { "AlbumName", "现场专辑" }, { "Duration", 249 }, { "MixSongID", "67422551" },
            { "FileHash", SongHash }, { "AlbumID", "2852128" }, { "ExtName", "mp3" },
            { "PayType", 0 }, { "Price", 0 }, { "Privilege", 0 }, { "FailProcess", 0 }
        };
    }

    private static string SearchResponse(Dictionary<string, object> row)
    {
        return Json(new { status = 1, error_code = 0, data = new { lists = new object[] { row } } });
    }

    private static Dictionary<string, object> AudioResponse()
    {
        return new Dictionary<string, object>
        {
            { "status", 1 }, { "errcode", 0 }, { "hash", SongHash }, { "req_hash", SongHash },
            { "pay_type", 0 }, { "privilege", 0 }, { "128privilege", 0 }, { "fail_process", 0 },
            { "timeLength", 249 }, { "extName", "mp3" }, { "url", AudioUrl }
        };
    }

    private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
}
