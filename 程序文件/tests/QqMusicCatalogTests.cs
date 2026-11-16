using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HarmonicaStudio;

public static class QqMusicCatalogTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        string row = "{\"name\":\"七里香\",\"title\":\"<em>七里香</em> (Live) &amp; 现场\",\"mid\":\"004Z8Ihr0JIu5s\",\"interval\":269," +
            "\"singer\":[{\"name\":\"周杰伦\"},{\"name\":\"嘉宾\"}],\"album\":{\"name\":\"演唱会\"},\"file\":{\"media_mid\":\"002ZQQ4Q287odQ\"},\"pay\":{\"pay_down\":0}}";
        List<OnlineSong> songs = QqMusicCatalog.ParseResponse(SearchResponse(row));
        Assert(songs.Count == 1 && songs[0].Title == "七里香 (Live) & 现场", "中文标题、现场版本与高亮实体应完整保留。");
        Assert(songs[0].Artist == "周杰伦 / 嘉宾" && songs[0].Album == "演唱会", "多位歌手与专辑应可读。");
        Assert(songs[0].Provider == "QQ音乐" && songs[0].SourceUrl.EndsWith("/004Z8Ihr0JIu5s"), "保留真实QQ音乐版本来源。");
        Assert(songs[0].MediaId == "002ZQQ4Q287odQ" && songs[0].FreeDownload && songs[0].DurationSeconds == 269, "媒体编号独立于歌曲编号且免费标记明确。");
        Assert(QqMusicCatalog.ParseResponse(SearchResponse("")).Count == 0, "明确空列表才表示搜索无结果。");
        Assert(QqMusicCatalog.ParseResponse(SearchResponse(row + "," + row)).Count == 1, "重复歌曲编号只展示一次。");
        Assert(!QqMusicCatalog.ParseResponse(SearchResponse(row.Replace("\"pay_down\":0", "\"pay_down\":1")))[0].FreeDownload, "收费下载歌曲不得尝试匿名下载。");
        Assert(!QqMusicCatalog.ParseResponse(SearchResponse(row.Replace("\"pay_down\":0", "\"other\":0")))[0].FreeDownload, "缺少权限标记不能推断免费。");
        Assert(QqMusicCatalog.ParseResponse(SearchResponse(row.Replace("\"interval\":269", "\"interval\":-1")))[0].DurationSeconds == 0, "异常时长不得展示为有效时长。");
        Assert(QqMusicCatalog.ParseResponse(SearchResponse(row).Replace("music.search.SearchCgiService", "req_1")).Count == 1, "兼容相同模块的req_1返回信封。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseResponse("<html>登录</html>"); }, "登录HTML不伪装成无结果。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseResponse("{\"code\":0}"); }, "缺少列表不伪装成无结果。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseResponse(SearchResponse(row).Replace("\"code\":0", "\"status\":0")); }, "缺少明确成功码不可默认成功。");
        Throws<InvalidOperationException>(delegate { QqMusicCatalog.ParseResponse(SearchResponse(row).Replace("\"code\":0", "\"code\":104003")); }, "服务拒绝时给出错误。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseResponse(SearchResponse(row.Replace("004Z8Ihr0JIu5s", "../song"))); }, "非法编号不可生成歌曲链接。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseResponse(new string('x', QqMusicCatalog.MaximumResponseBytes + 1)); }, "限制不可信响应大小。");
        string searchUrl = QqMusicCatalog.BuildSearchRequestUrl(" 周杰伦 \"现场\" ", 2);
        string requestJson = Uri.UnescapeDataString(new Uri(searchUrl).Query.Substring("?data=".Length));
        Dictionary<string, object> request = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(requestJson);
        Dictionary<string, object> module = (Dictionary<string, object>)request["music.search.SearchCgiService"];
        Dictionary<string, object> parameters = (Dictionary<string, object>)module["param"];
        Assert((string)parameters["query"] == "周杰伦 \"现场\"" && (int)parameters["page_num"] == 2 && (int)parameters["num_per_page"] == 20, "JSON正确编码中文引号与分页。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.BuildSearchRequestUrl("", 1); }, "不发送空搜索。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.BuildSearchRequestUrl("歌\n曲", 1); }, "拒绝控制字符。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.BuildSearchRequestUrl(new string('歌', 101), 1); }, "限制查询长度。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.BuildSearchRequestUrl("\ud800", 1); }, "拒绝无效Unicode。");
        Throws<ArgumentOutOfRangeException>(delegate { QqMusicCatalog.BuildSearchRequestUrl("歌曲", 0); }, "拒绝越界页码。");
        Assert(QqMusicCatalog.OfficialSearchUrl("七里香 & 现场").Contains("%E4%B8%83%E9%87%8C%E9%A6%99%20%26%20"), "官网检索正确编码搜索词。");
        Assert(QqMusicCatalog.OfficialSearchUrl(" ") == "https://y.qq.com/" && QqMusicCatalog.OfficialDownloadUrl == "https://y.qq.com/download/download.html", "登录下载采用真实官网。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.OfficialSongUrl("foo?token=bar"); }, "歌曲编号不可插入查询参数。");
        TestLegacySearchAndFallback();
        OnlineSong audioSong = new OnlineSong { Id = "0048Ts0Q0oi3Ti", MediaId = "002ZQQ4Q287odQ", FreeDownload = true };
        string audio = AudioResponse(0, "M500002ZQQ4Q287odQ.mp3?vkey=example&guid=123", "http://aqqmusic.tc.qq.com/amobile.music.tc.qq.com/");
        string url = QqMusicCatalog.ParseAudioResponse(audio, audioSong);
        Assert(url.StartsWith("https://aqqmusic.tc.qq.com/amobile.music.tc.qq.com/M500002ZQQ4Q287odQ.mp3?") && url.EndsWith("vkey=example&guid=123"), "官方相对地址升级HTTPS并保留原始查询。");
        Assert(QqMusicCatalog.IsOrdinaryAudioUri(new Uri(url)), "验证普通官方MP3地址。");
        Assert(QqMusicCatalog.ParseAudioResponse(AudioResponse(104003, "", "https://sjy6.stream.qqmusic.qq.com/"), audioSong) == null, "受限歌曲不会伪造下载成功。");
        Assert(QqMusicCatalog.ParseAudioResponse(AudioResponse(0, "", "https://sjy6.stream.qqmusic.qq.com/"), audioSong) == null, "空purl表示不可下载。");
        Assert(QqMusicCatalog.ParseAudioResponse(audio.Replace("0048Ts0Q0oi3Ti", "OtherSong"), audioSong) == null, "不下载错误的歌曲版本。");
        Assert(QqMusicCatalog.ParseAudioResponse(audio, new OnlineSong { Id = audioSong.Id, MediaId = audioSong.MediaId, FreeDownload = false }) == null, "付费标记阻止音频解析。");
        Assert(QqMusicCatalog.ResolveAudioUrl(new OnlineSong { FreeDownload = false }) == null, "付费歌曲不发送匿名音频请求。");
        foreach (string badServer in new string[] { "https://aqqmusic.tc.qq.com.evil.example/", "https://user@aqqmusic.tc.qq.com/", "https://aqqmusic.tc.qq.com:444/", "https://qpic.cn/", "https://example.com/" })
            Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseAudioResponse(AudioResponse(0, "M500002ZQQ4Q287odQ.mp3?x=1", badServer), audioSong); }, "拒绝非官方或异常音频服务器。");
        foreach (string badPath in new string[] { "https://example.com/song.mp3", "//example.com/song.mp3", "/M500002ZQQ4Q287odQ.mp3", "M500OtherSong.mp3", "M500002ZQQ4Q287odQ.mp3#fragment", "M500002ZQQ4Q287odQ.mp3\\other" })
            Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseAudioResponse(AudioResponse(0, badPath, "https://sjy6.stream.qqmusic.qq.com/"), audioSong); }, "拒绝替换或异常媒体路径。");
        Assert(QqMusicCatalog.ParseAudioResponse(AudioResponse(0, "M500002ZQQ4Q287odQ.mp3", "https://sjy6.stream.qqmusic.qq.com/"), audioSong).StartsWith("https://sjy6.stream.qqmusic.qq.com/"), "认可已实测的QQ音乐流媒体HTTPS主机。");
        TestAccountSessionAndPermissions();
        TestRedactedCurrentAccount();
        TestCompleteAudio();
        Console.WriteLine("QQ音乐搜索与普通音频权限测试通过，共 " + checks + " 项断言（未连接网络）。");
        return checks;
    }

    private static QqAccountSession Candidate(params QqMusicCookie[] additional)
    {
        var cookies = new List<QqMusicCookie> { Cookie("uin", "o0012345678", ".y.qq.com"), Cookie("qm_keyst", "test-music-session", "y.qq.com") };
        cookies.AddRange(additional); return QqAccountSession.FromBrowserCookies(cookies);
    }
    private static QqMusicCookie Cookie(string name, string value, string domain)
    { return new QqMusicCookie { Name = name, Value = value, Domain = domain, IsSession = true }; }
    private static string AccountResponse(string identity)
    { return "{\"code\":0,\"account\":{\"code\":0,\"data\":" + identity + "}}"; }
    private static void TestAccountSessionAndPermissions()
    {
        QqAccountSession session = Candidate();
        Assert(session != null && session.Uin == "12345678" && !session.Verified && !session.IsUsable,
            "官方页面的Cookie仅是候选会话，不能直接宣称登录成功。");
        Assert(QqAccountSession.NormalizeUin("o000123") == "123" && QqAccountSession.NormalizeUin("000") == "", "QQ编号仅接受规范数字，去掉合法前缀及零。");
        foreach (string bad in new[] { "123x", "123&admin=1", " 123", "o123\r\n", "123456789012345678901" })
            Assert(QqAccountSession.NormalizeUin(bad) == "", "拒绝账号编号注入与超长编号。");
        Assert(QqAccountSession.FromBrowserCookies(new[] { Cookie("uin", "123", "qq.com"), Cookie("p_skey", "not-a-music-session", "qq.com") }) == null,
            "一般QQ浏览器会话不能代替QQ音乐会话。");
        foreach (string bad in new[] { "evil.example", "qq.com.evil.example", "evily.qq.com", "ptlogin2.qq.com" })
            Assert(QqAccountSession.FromBrowserCookies(new[] { Cookie("uin", "123", bad), Cookie("qm_keyst", "test", bad) }) == null,
                "只使用本软件读取的音乐域必要会话。");
        Assert(QqAccountSession.FromBrowserCookies(new[] { Cookie("uin", "123", "y.qq.com"), Cookie("qm_keyst", "test\r\nInjected=x", "y.qq.com") }) == null,
            "Cookie中控制字符不能写入HTTP请求。");
        var expired = Cookie("qm_keyst", "test", "y.qq.com"); expired.IsSession = false; expired.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert(QqAccountSession.FromBrowserCookies(new[] { Cookie("uin", "123", "y.qq.com"), expired }) == null, "过期核心票据不可使用。");
        QqAccountSession wx = Candidate(Cookie("login_type", "2", "y.qq.com"), Cookie("wxuin", "24680", "y.qq.com"));
        Assert(wx.Uin == "24680", "微信登录使用官方音乐wxuin，不冒充原QQ编号。");
        Assert(Candidate(Cookie("qqmusic_uin", "9876", "y.qq.com")).Uin == "12345678", "官网正常QQ会话优先当前uin，不混用另一qqmusic_uin。");
        const string wxId = "1234567890123456789";
        QqAccountSession modernWx = QqAccountSession.FromBrowserCookies(new[]
        {
            Cookie("wxuin", wxId, ".y.qq.com"), Cookie("tmeLoginType", "1", ".qq.com"),
            Cookie("qm_keyst", "legacy-key-99", ".y.qq.com"), Cookie("qqmusic_key", "official-web-key-99", ".qq.com")
        });
        Assert(modernWx != null && modernWx.Uin == wxId && modernWx.LoginType == "1" && !modernWx.IsUsable,
            "真实现代微信cookie形状无uin/login_type也可建立19位ID候选，尚不伪报通过账号确认。");
        Assert(modernWx.MusicKey == "official-web-key-99", "音乐会话票据优先级与当前官网qqmusic_key一致。");
        Assert(QqAccountSession.FromBrowserCookies(new[] { Cookie("wxuin", wxId, "y.qq.com"), Cookie("qm_keyst", "test", "y.qq.com") }).Uin == wxId,
            "缺类型字段但存在唯一合法wxuin时能安全恢复音乐账号候选。");
        Assert(modernWx.CookiesFor(new Uri("https://c.y.qq.com/")).GetCookies(new Uri("https://c.y.qq.com/"))["tmeLoginType"].Value == "1",
            "自家微信音乐登录类型随必要音乐Cookie发往官方接口。");
        Assert(Candidate(Cookie("uin", "999", ".qq.com")).Uin == "12345678", "音乐域同名Cookie优先于上级QQ域。");
        Assert(!QqMusicCatalog.ParseAccountResponse("{\"code\":0,\"account\":{\"code\":1000,\"data\":{}}}", session), "实测匿名code1000不会误报登录。");
        Assert(!QqMusicCatalog.ParseAccountResponse(AccountResponse("{}"), session), "成功码没有账号身份也不能声称登录。");
        Assert(!QqMusicCatalog.ParseAccountResponse(AccountResponse("{\"userid\":12345678,\"nick\":\"公开资料\"}"), session), "普通userid或公开用户资料不能当当前账号证据。");
        Assert(!QqMusicCatalog.ParseAccountResponse(AccountResponse("{\"uin\":87654321}"), session), "返回其他账号不能混用会话。");
        Assert(QqMusicCatalog.ParseAccountResponse(AccountResponse("{\"userInfo\":{\"uin\":12345678,\"nick\":\"测试账号\"}}"), session) && session.IsUsable && session.Nickname == "测试账号",
            "仅官方当前账号接口返回一致身份后才启用会话。");
        Assert(QqMusicCatalog.ParseAccountResponse(AccountResponse("{\"str_musicid\":\"12345678\",\"musicid\":12345678,\"uin\":999}"), session),
            "明确音乐ID优先于可能不同的原QQ编号。");
        Assert(!QqMusicCatalog.ParseAccountResponse(AccountResponse("{\"str_musicid\":\"12345678\",\"musicid\":999}"), session) && !session.IsUsable,
            "冲突的音乐身份拒绝授权，并停用之前的确认状态。");
        Assert(session.AcceptIdentity("12345678", "测试账号"), "离线测试建立明确身份，不实际登录用户账号。");
        TestWebProfileConfirmation();
        foreach (string address in new[] { "https://u.y.qq.com/cgi-bin/musicu.fcg", "https://c.y.qq.com/api", "https://c6.y.qq.com/api" })
            Assert(session.CookiesFor(new Uri(address)).GetCookies(new Uri(address))["qm_keyst"] != null, "必要会话只能附给明确官方账号接口。");
        foreach (string address in new[] { "http://u.y.qq.com/", "https://u.y.qq.com.evil.example/", "https://evil.example/", "https://aqqmusic.tc.qq.com/", "https://user@u.y.qq.com/", "https://u.y.qq.com:444/" })
            Throws<InvalidOperationException>(delegate { session.CookiesFor(new Uri(address)); }, "账号会话不能泄露给HTTP、第三方或音频CDN。");
        Dictionary<string, object> request = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
            Uri.UnescapeDataString(new Uri(QqMusicCatalog.BuildAccountRequestUrl(session)).Query.Substring("?data=".Length).Split('&')[0]));
        var comm = (Dictionary<string, object>)request["comm"];
        Assert((string)comm["uin"] == session.Uin && (string)comm["authst"] == session.MusicKey && Convert.ToUInt32(comm["g_tk"]) == session.Gtk,
            "当前账号确认请求携带自身音乐会话与对应CSRF值。");
        OnlineSong paid = new OnlineSong { Id = "0048Ts0Q0oi3Ti", MediaId = "002ZQQ4Q287odQ", FreeDownload = false };
        int requests = 0;
        Func<string, string, string> grant = delegate(string url, string operation)
        {
            requests++;
            var body = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Uri.UnescapeDataString(new Uri(url).Query.Substring("?data=".Length)));
            var param = (Dictionary<string, object>)((Dictionary<string, object>)body["req_0"])["param"];
            Assert((string)param["uin"] == session.Uin && (int)param["loginflag"] == 1, "下载请求使用已确认自身账号，保留所选版本。");
            return AudioResponse(0, "M500002ZQQ4Q287odQ.mp3?vkey=test", "https://sjy6.stream.qqmusic.qq.com/");
        };
        Assert(QqMusicCatalog.ResolveAudioUrl(paid, null, grant) == null && requests == 0, "匿名账号仍不能请求受限版本。");
        Assert(QqMusicCatalog.ResolveAudioUrl(paid, session, grant) != null && requests == 1 && !paid.FreeDownload,
            "确认账号可请求平台授权的普通MP3；不篡改免费标志或保证会员权限。");
        string granted = AudioResponse(0, "M500002ZQQ4Q287odQ.mp3?vkey=test", "https://sjy6.stream.qqmusic.qq.com/");
        foreach (string preview in new[] { "trial", "is_trial", "free_trial", "preview", "is_preview", "trial_begin", "trial_end" })
            Assert(QqMusicCatalog.ParseAudioResponse(granted.Replace("\"result\":0", "\"result\":0,\"" + preview + "\":1"), paid, true) == null,
                "服务器明确试听标记不得作为整曲下载。");
        Assert(QqMusicCatalog.ParseAudioResponse(AudioResponse(104003, "", "https://sjy6.stream.qqmusic.qq.com/"), paid, true) == null,
            "登录后平台拒绝授权仍明确受限。");
        string directory = Path.Combine(Path.GetTempPath(), "qq-session-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "session.dat");
        try
        {
            QqAccountStore.Write(path, session);
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(session.MusicKey), "会话以Windows DPAPI当前用户加密，不在磁盘保存明文票据。");
            QqAccountSession restored = QqAccountStore.Read(path);
            Assert(restored != null && restored.Uin == session.Uin && !restored.Verified && !restored.IsUsable, "恢复的已保存会话必须重新通过官方确认，不能盲显示已登录。");
            File.WriteAllText(path, "invalid encrypted record");
            Assert(QqAccountStore.Read(path) == null, "损坏加密会话安全回退匿名，不导致启动崩溃。");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
        Assert(QqWebViewHost.IsOfficialNavigation("https://y.qq.com/") && QqWebViewHost.IsOfficialNavigation("https://open.weixin.qq.com/connect/qrconnect") &&
            QqWebViewHost.IsOfficialNavigation("https://xui.ptlogin2.qq.com/"), "软件内官方QQ与微信授权页均允许导航。");
        foreach (string address in new[] { "http://y.qq.com/", "https://y.qq.com.evil.example/", "file:///C:/Windows/win.ini", "javascript:alert(1)", "https://qq.com/", "https://y.qq.com:444/" })
            Assert(!QqWebViewHost.IsOfficialNavigation(address), "登录宿主阻止非官方页面、脚本地址及本地文件导航。");
    }
    private static QqAccountSession ModernWechat()
    {
        return QqAccountSession.FromBrowserCookies(new[]
        {
            Cookie("wxuin", "1234567890123456789", ".y.qq.com"), Cookie("tmeLoginType", "1", ".qq.com"),
            Cookie("qm_keyst", new string('k', 99), ".y.qq.com"), Cookie("qqmusic_key", new string('k', 99), ".qq.com")
        });
    }
    private static Dictionary<string, object> AccountPayload(string url)
    {
        return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
            Uri.UnescapeDataString(new Uri(url).Query.Substring("?data=".Length).Split('&')[0]));
    }
    private static void TestRedactedCurrentAccount()
    {
        const string current = "{\"code\":0,\"account\":{\"code\":0,\"data\":{\"errMsg\":\"\",\"identify\":0,\"info\":{\"nick\":\"微信音乐账号\",\"face\":\"https://qlogo.cn/avatar\"},\"celebrityInfo\":{\"uin\":0}}}}";
        const string refused = "{\"code\":0,\"account\":{\"code\":1000}}";
        var session = ModernWechat(); var urls = new List<string>(); var accounts = new List<QqAccountSession>();
        Assert(QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
        {
            urls.Add(url); accounts.Add(authority); return authority == null ? refused : current;
        }) && session.IsUsable && session.Nickname == "微信音乐账号" && session.ValidationFailureKind == "",
            "实证去标识的现代微信返回可用真实鉴权对照确认，不再强求接口已隐藏的19位数字ID。");
        Assert(urls.Count == 2 && Object.ReferenceEquals(accounts[0], session) && accounts[1] == null,
            "去标识确认需要官方授权成功与无凭据拒绝两个响应，不能仅凭昵称成功。");
        var authComm = (Dictionary<string, object>)AccountPayload(urls[0])["comm"];
        var noAuthComm = (Dictionary<string, object>)AccountPayload(urls[1])["comm"];
        Assert((string)authComm["uin"] == session.Uin && (string)authComm["tmeLoginType"] == "1" && (string)authComm["authst"] == session.MusicKey,
            "当前接口携带原样19位微信ID和本人的现代音乐登录类型。");
        Assert(!noAuthComm.ContainsKey("authst") && (string)noAuthComm["uin"] == session.Uin &&
            Convert.ToString(noAuthComm["g_tk"]) == Convert.ToString(authComm["g_tk"]) && (int)noAuthComm["ct"] == (int)authComm["ct"] &&
            new Uri(urls[0]).Query.Substring(new Uri(urls[0]).Query.IndexOf("&g_tk=", StringComparison.Ordinal)) ==
            new Uri(urls[1]).Query.Substring(new Uri(urls[1]).Query.IndexOf("&g_tk=", StringComparison.Ordinal)),
            "无授权对照移除URL数据内authst及Cookie，保留同一ID/平台/CSRF，不能把仍带key的请求叫匿名。");
        session = ModernWechat();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate { return current; }) && !session.IsUsable && session.ValidationFailureKind == "Permission",
            "无授权也可返回的userinfo不得伪报登录成功。");
        session = ModernWechat();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
            { return authority == null ? "{\"code\":0,\"account\":{\"code\":2001}}" : current; }) && session.ValidationFailureKind == "Permission",
            "未知错误码不是明确鉴权拒绝证据，不能拿来通过账号确认。");
        session = ModernWechat(); int calls = 0;
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate
        { calls++; return current.Replace("\"nick\":", "\"uin\":123,\"nick\":"); }) && calls == 1 && session.ValidationFailureKind == "Identity",
            "接口明确返回矛盾数字身份时不得用昵称/隐私字段覆盖身份冲突。");
        session = ModernWechat();
        Throws<InvalidOperationException>(delegate
        { QqMusicCatalog.ValidateAccount(session, delegate { throw new InvalidOperationException("masked transport", new WebException("masked timeout")); }); },
            "网络失败不应被当作凭据有效或登录过期。");
        Assert(!session.IsUsable && session.ValidationFailureKind == "Network", "网络失败保留准确分类给UI重试，不能声称账号过期。");
        session = ModernWechat();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
        { return url.Contains("musicu.fcg") ? refused : "{\"code\":1000,\"data\":{}}"; }) && session.ValidationFailureKind == "Expired",
            "当前账号及官网自身资料都明确拒绝授权时才提示会话失效。");
        const string selfProfile = "{\"code\":0,\"data\":{\"creator\":{\"uin\":0,\"uin_web\":\"masked\",\"encrypt_uin\":\"anonymous-profile-identifier\",\"ishost\":1,\"nick\":\"微信音乐账号\",\"headpic\":\"https://qlogo.cn/avatar\"}}}";
        session = ModernWechat(); urls.Clear();
        Assert(QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
        {
            urls.Add(url); return url.Contains("musicu.fcg") ? refused : authority == null ? "{\"code\":1000,\"data\":{}}" : selfProfile;
        }) && session.IsUsable,
            "官网userid0当前自身资料可由ishost与encrypt_uin及匿名拒绝确认，兼容实测uin0隐私返回。");
        Assert(urls.Count == 3 && urls[1] == urls[2] && urls[1].Contains("userid=0") && urls[1].Contains("ct=24") && urls[1].Contains("reqtype=0"),
            "自身资料请求与当前官网参数一致；匿名对照URL完全相同。");
        foreach (string badProfile in new[] { selfProfile.Replace("\"ishost\":1", "\"ishost\":0"), selfProfile.Replace("anonymous-profile-identifier", ""), selfProfile.Replace("\"uin\":0", "\"uin\":123") })
        {
            session = ModernWechat();
            Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
            { return url.Contains("musicu.fcg") ? refused : authority == null ? "{\"code\":1000,\"data\":{}}" : badProfile; }) && !session.IsUsable,
                "非自身资料、缺服务端去标识身份或矛盾数字身份均不能确认账号。");
        }
        session = ModernWechat();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
        { return url.Contains("musicu.fcg") ? refused : selfProfile; }), "匿名也能获得的自身资料不能当授权证据。");
        session = ModernWechat();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession authority)
        { return url.Contains("musicu.fcg") ? refused : authority == null ? "{\"code\":2001}" : selfProfile; }), "官网匿名对照未知错误不通过鉴权。");
    }
    private static void TestWebProfileConfirmation()
    {
        string refused = "{\"code\":1000,\"data\":{}}";
        string profile = "{\"code\":0,\"data\":{\"creator\":{\"uin\":12345678,\"nick\":\"官方当前账号\"}}}";
        var session = Candidate();
        var urls = new List<string>();
        var accounts = new List<QqAccountSession>();
        Func<string, QqAccountSession, string> currentWeb = delegate(string url, QqAccountSession account)
        {
            urls.Add(url); accounts.Add(account);
            if (url.Contains("musicu.fcg")) return "{\"code\":0,\"account\":{\"code\":1000,\"data\":{}}}";
            return account == null ? refused : profile;
        };
        Assert(QqMusicCatalog.ValidateAccount(session, currentWeb) && session.IsUsable && session.Nickname == "官方当前账号",
            "WEB会话可经官方profile匹配身份和无Cookie拒绝对照后确认。");
        Assert(urls.Count == 3 && urls[1] == urls[2] && Object.ReferenceEquals(accounts[1], session) && accounts[2] == null,
            "WEB对照必须使用完全同一URL，差别仅为本软件自身会话Cookie。");
        session = Candidate();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession account)
            { return url.Contains("musicu.fcg") ? "{\"code\":0,\"account\":{\"code\":1000,\"data\":{}}}" : profile; }) && !session.IsUsable,
            "匿名也能读取的公开资料不得证明登录成功。");
        session = Candidate(); int calls = 0;
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession account)
            { calls++; return url.Contains("musicu.fcg") ? "{\"code\":0,\"account\":{\"code\":1000,\"data\":{}}}" : profile.Replace("12345678", "87654321"); }) && calls == 2,
            "WEB响应为别的账号则拒绝，无需查询公开对照。");
        session = Candidate();
        Assert(!QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession account)
            { return url.Contains("musicu.fcg") ? "{\"code\":0,\"account\":{\"code\":1000,\"data\":{}}}" : account == null ? "{\"data\":{}}" : profile; }),
            "无Cookie响应没有明确拒绝码时不能确认证据成立。");
        session = Candidate(); calls = 0;
        Assert(QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession account)
            { calls++; return AccountResponse("{\"uin\":12345678}"); }) && calls == 1,
            "当前账号API已返回一致身份时不额外请求WEB对照。");
        session = Candidate();
        Assert(QqMusicCatalog.ValidateAccount(session, delegate(string url, QqAccountSession account)
        {
            if (url.Contains("musicu.fcg")) throw new InvalidDataException("upstream changed");
            return account == null ? refused : profile;
        }), "当前账号响应格式变化可安全降级到带匿名对照的WEB验证。");
    }
    private static MemoryStream Mp3Frames(int count, bool id3)
    {
        var stream = new MemoryStream();
        if (id3) stream.Write(new byte[] { 73, 68, 51, 4, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0 }, 0, 14);
        byte[] frame = new byte[417]; frame[0] = 255; frame[1] = 251; frame[2] = 144;
        for (int i = 0; i < count; i++) stream.Write(frame, 0, frame.Length);
        stream.Position = 0; return stream;
    }
    private static void TestCompleteAudio()
    {
        using (MemoryStream full = Mp3Frames(3828, false))
            Assert(Math.Abs(QqCompleteAudio.ReadDuration(full, CancellationToken.None) - 100.0) < 0.1, "扫描MP3帧获得整曲时长，不以文件大小估算。");
        using (MemoryStream full = Mp3Frames(3828, true))
            Assert(Math.Abs(QqCompleteAudio.ReadDuration(full, CancellationToken.None) - 100.0) < 0.1, "跳过合法ID3元数据后仍能核对全曲时长。");
        using (MemoryStream preview = Mp3Frames(1148, false))
            Assert(QqCompleteAudio.ReadDuration(preview, CancellationToken.None) < 31, "30秒试听的真实时长不会伪装成100秒全曲。");
        using (MemoryStream invalid = new MemoryStream(Encoding.UTF8.GetBytes("not mp3")))
            Assert(QqCompleteAudio.ReadDuration(invalid, CancellationToken.None) == 0, "非MP3文件不被认证为完整普通MP3。");
        using (MemoryStream truncated = Mp3Frames(3828, false))
        { truncated.SetLength(truncated.Length - 1024); Assert(QqCompleteAudio.ReadDuration(truncated, CancellationToken.None) == 0, "截断的MP3帧不通过完整性检查。"); }
        using (MemoryStream full = Mp3Frames(3828, false))
        using (var cancel = new CancellationTokenSource())
        { cancel.Cancel(); Throws<OperationCanceledException>(delegate { QqCompleteAudio.ReadDuration(full, cancel.Token); }, "完整音频核对支持取消。"); }
        string path = Path.Combine(Path.GetTempPath(), "qq-full-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            using (MemoryStream full = Mp3Frames(3828, false)) File.WriteAllBytes(path, full.ToArray());
            QqCompleteAudio.Validate(path, 100, CancellationToken.None);
            Assert(true, "与歌曲元数据时长一致的普通完整MP3可继续转录。");
            Throws<InvalidDataException>(delegate { QqCompleteAudio.Validate(path, 0, CancellationToken.None); }, "元数据缺失不能认证受限版本为整曲。");
            using (MemoryStream preview = Mp3Frames(1148, false)) File.WriteAllBytes(path, preview.ToArray());
            Throws<InvalidDataException>(delegate { QqCompleteAudio.Validate(path, 100, CancellationToken.None); }, "30秒试听不能冒充100秒完整歌曲。");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string SearchResponse(string rows) { return "{\"code\":0,\"music.search.SearchCgiService\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[" + rows + "]}}}}}"; }
    private static string LegacyResponse(string rows)
    { return "{\"code\":0,\"data\":{\"song\":{\"list\":[" + rows + "]}}}"; }
    private static void TestLegacySearchAndFallback()
    {
        string row = "{\"songname\":\"像我这样的人 (Live) &amp; 现场\",\"songmid\":\"003hzeOV0LsPyx\",\"interval\":171," +
            "\"singer\":[{\"name\":\"毛不易\"},{\"name\":\"嘉宾\"}],\"albumname\":\"指定演唱会\",\"media_mid\":\"001Z9MJl29sdnT\",\"pay\":{\"paydownload\":0,\"payalbum\":0}}";
        List<OnlineSong> songs = QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row));
        Assert(songs.Count == 1 && songs[0].Title == "像我这样的人 (Live) & 现场" && songs[0].Artist == "毛不易 / 嘉宾", "官方备用搜索保留现场版本、实体与所有演唱者。");
        Assert(songs[0].Id == "003hzeOV0LsPyx" && songs[0].MediaId == "001Z9MJl29sdnT" && songs[0].Album == "指定演唱会" && songs[0].DurationSeconds == 171,
            "备用搜索按真实旧字段解析歌MID、媒体MID、专辑和时长，二种MID不能混同。");
        Assert(songs[0].Provider == "QQ音乐" && songs[0].SourceUrl == "https://y.qq.com/n/ryqq/songDetail/003hzeOV0LsPyx" && songs[0].FreeDownload,
            "备用结果仍是确切QQ版本，免费权限来自明确的下载及专辑字段。");
        Assert(!QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row.Replace("\"paydownload\":0", "\"paydownload\":1")))[0].FreeDownload, "备用来源的收费下载标记不尝试匿名音频解析。");
        Assert(!QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row.Replace("\"payalbum\":0", "\"payalbum\":1")))[0].FreeDownload, "付费专辑版本也不自动视作免费。");
        Assert(!QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row.Replace("\"payalbum\":0", "\"unknown\":0")))[0].FreeDownload, "缺专辑权限字段保守关闭自动下载。");
        Assert(!QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row.Replace("\"paydownload\":0", "\"unknown\":0")))[0].FreeDownload, "缺下载权限字段不能推断免费。");
        Assert(QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row + "," + row)).Count == 1, "备用结果按歌MID去重，不重复列同一版本。");
        Assert(QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse("")).Count == 0, "备用入口的明确空列表才表示没有结果。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseLegacySearchResponse("{\"code\":0,\"data\":{}}"); }, "备用入口缺歌曲列表不能伪报搜索无结果。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseLegacySearchResponse(LegacyResponse(row.Replace("003hzeOV0LsPyx", "../song"))); }, "备用编号同样不能生成路径穿越的官方链接。");
        Throws<InvalidDataException>(delegate { QqMusicCatalog.ParseLegacySearchResponse("<html>登录</html>"); }, "备用网页或登录HTML不能当成歌曲JSON。");
        string url = QqMusicCatalog.BuildLegacySearchRequestUrl(" 周杰伦 & \"现场\" ", 2);
        Assert(url.StartsWith("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=2&n=20&w=") &&
            Uri.UnescapeDataString(url.Substring(url.IndexOf("&w=", StringComparison.Ordinal) + 3)) == "周杰伦 & \"现场\"",
            "备用只访问已验证官方HTTPS地址，并正确编码中文、引号及分页。");
        Throws<ArgumentException>(delegate { QqMusicCatalog.BuildLegacySearchRequestUrl("歌\n曲", 1); }, "备用请求同样拒绝控制字符。");
        Throws<ArgumentOutOfRangeException>(delegate { QqMusicCatalog.BuildLegacySearchRequestUrl("歌", 0); }, "备用请求限制页码。");
        int calls = 0;
        songs = QqMusicCatalog.Search("像我这样的人", 2, delegate(string requestUrl, string operation)
        {
            calls++;
            if (calls == 1) return "{\"code\":0,\"music.search.SearchCgiService\":{\"code\":2001,\"data\":{}}}";
            Assert(requestUrl == QqMusicCatalog.BuildLegacySearchRequestUrl("像我这样的人", 2) && operation.Contains("备用"), "2001后只请求同一搜索词与页码的官方备用入口。");
            return LegacyResponse(row);
        });
        Assert(calls == 2 && songs.Count == 1 && songs[0].Id == "003hzeOV0LsPyx" && songs[0].Artist == "毛不易 / 嘉宾", "主入口2001时确实降级得到真实备用版本，而非只换错误提示。");
        calls = 0;
        songs = QqMusicCatalog.Search("无结果", 1, delegate { calls++; return SearchResponse(""); });
        Assert(calls == 1 && songs.Count == 0, "主入口明确成功空列表不重复请求备用来源。");
        calls = 0;
        songs = QqMusicCatalog.Search("像我这样的人", 1, delegate { calls++; if (calls == 1) throw new TimeoutException("模拟主入口超时。"); return LegacyResponse(row); });
        Assert(calls == 2 && songs.Count == 1, "主入口连接超时也可通过官方备用入口恢复。");
        calls = 0;
        songs = QqMusicCatalog.Search("像我这样的人", 1, delegate { calls++; return calls == 1 ? "{\"code\":0}" : LegacyResponse(row); });
        Assert(calls == 2 && songs.Count == 1, "主入口结构变化时可从受检备用结构恢复。");
        calls = 0;
        try
        {
            QqMusicCatalog.Search("像我这样的人", 1, delegate { calls++; return calls == 1 ? "{\"code\":2001}" : "{\"code\":104003}"; });
            Assert(false, "双入口失败不可虚报成功。");
        }
        catch (InvalidOperationException error)
        {
            Assert(calls == 2 && error.Message.Contains("2001") && error.Message.Contains("104003") && error.Message.Contains("酷狗"), "双入口失败准确保留服务码并提供实际替代平台。");
            Assert(!error.Message.Contains("登录"), "2001含义未证实，不编造成必须登录。");
        }
        calls = 0;
        Throws<ArgumentException>(delegate { QqMusicCatalog.Search("", 1, delegate { calls++; return LegacyResponse(row); }); }, "非法搜索在网络请求之前拒绝。");
        Assert(calls == 0, "无效输入不发送主入口或备用网络请求。");
        calls = 0;
        Throws<ArgumentException>(delegate { QqMusicCatalog.Search("歌曲", 1, delegate { calls++; throw new ArgumentException("模拟实现参数错误。"); }); }, "实现参数错误不能被隐藏成服务降级。");
        Assert(calls == 1, "未识别的程序错误不触发额外来源请求。");
    }
    private static string AudioResponse(int result, string purl, string server)
    {
        return new JavaScriptSerializer().Serialize(new { code = 0, req_0 = new { code = 0, data = new { sip = new string[] { server }, midurlinfo = new object[] { new { songmid = "0048Ts0Q0oi3Ti", filename = "M500002ZQQ4Q287odQ.mp3", purl = purl, result = result } } } } });
    }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
}
