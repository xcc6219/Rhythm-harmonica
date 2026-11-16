using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HarmonicaStudio
{
    public static class QqMusicCatalog
    {
        // Website endpoints are not the partner OpenAPI. Login uses only the
        // user's own verified music session from this application's WebView.
        public const string ReferenceUrl = "https://github.com/WXRIW/Lyricify-Lyrics-Helper";
        public const string OfficialLoginUrl = "https://y.qq.com/";
        public const string OfficialDownloadUrl = "https://y.qq.com/download/download.html";
        public const int PageSize = 20;
        public const int MaximumQueryLength = 100;
        public const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int TimeoutMilliseconds = 12000;
        private const string Endpoint = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        private const string LegacySearchEndpoint = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
        private const string SearchModule = "music.search.SearchCgiService";
        private static readonly Regex Identifier = new Regex("\\A[A-Za-z0-9]{1,64}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex HtmlTags = new Regex("<[^>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex Whitespace = new Regex("\\s+", RegexOptions.CultureInvariant);

        public static List<OnlineSong> Search(string query, int page)
        {
            QqAccountSession account = QqAccountStore.Current;
            if (account != null && !account.IsUsable) account = null;
            return Search(query, page, delegate(string url, string operation) { return RequestJson(url, operation, account); });
        }

        internal static List<OnlineSong> Search(string query, int page, Func<string, string, string> request)
        {
            string primaryUrl = BuildSearchRequestUrl(query, page);
            string alternativeUrl = BuildLegacySearchRequestUrl(query, page);
            if (request == null) throw new ArgumentNullException("request");
            Exception primaryFailure;
            try { return ParseResponse(request(primaryUrl, "QQ音乐搜索")); }
            catch (Exception error)
            {
                if (!IsSearchServiceFailure(error)) throw;
                primaryFailure = error;
            }
            try { return ParseLegacySearchResponse(request(alternativeUrl, "QQ音乐备用搜索")); }
            catch (Exception error)
            {
                if (!IsSearchServiceFailure(error)) throw;
                string primaryCode = ServiceCode(primaryFailure), alternativeCode = ServiceCode(error);
                string detail = primaryCode.Length == 0 ? "" : "主入口错误码 " + primaryCode;
                if (alternativeCode.Length != 0) detail += (detail.Length == 0 ? "" : "，") + "备用入口错误码 " + alternativeCode;
                throw new InvalidOperationException("QQ音乐的两个搜索入口暂不可用" +
                    (detail.Length == 0 ? "" : "（" + detail + "）") + "。可稍后重试或选择酷狗音乐。", error);
            }
        }

        private static bool IsSearchServiceFailure(Exception error)
        { return error is InvalidOperationException || error is IOException || error is InvalidDataException || error is TimeoutException; }

        private static string ServiceCode(Exception error)
        {
            int code;
            return error != null && TryInteger(error.Data["QqServiceCode"], out code) ? code.ToString(CultureInfo.InvariantCulture) : "";
        }

        internal static string BuildSearchRequestUrl(string query, int page)
        {
            query = ValidateQuery(query);
            if (page < 1 || page > 1000)
                throw new ArgumentOutOfRangeException("page", "搜索页码应在 1 到 1000 之间。");
            return JsonRequestUrl(new Dictionary<string, object>
            {
                { SearchModule, new Dictionary<string, object>
                    {
                        { "module", SearchModule }, { "method", "DoSearchForQQMusicDesktop" },
                        { "param", new Dictionary<string, object>
                            { { "search_type", 0 }, { "query", query }, { "page_num", page }, { "num_per_page", PageSize } } }
                    }
                }
            });
        }

        internal static string BuildLegacySearchRequestUrl(string query, int page)
        {
            query = ValidateQuery(query);
            if (page < 1 || page > 1000) throw new ArgumentOutOfRangeException("page", "搜索页码应在 1 到 1000 之间。");
            return LegacySearchEndpoint + "?format=json&p=" + page.ToString(CultureInfo.InvariantCulture) +
                "&n=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&w=" + Uri.EscapeDataString(query);
        }

        internal static List<OnlineSong> ParseResponse(string json)
        {
            Dictionary<string, object> root = ParseJson(json);
            RequireSuccess(root, "QQ音乐搜索");
            Dictionary<string, object> module = Dictionary(Value(root, SearchModule)) ?? Dictionary(Value(root, "req_1"));
            if (module == null) throw ChangedFormat("QQ音乐搜索");
            RequireSuccess(module, "QQ音乐搜索");
            Dictionary<string, object> data = Dictionary(Value(module, "data"));
            Dictionary<string, object> body = Dictionary(Value(data, "body"));
            Dictionary<string, object> songData = Dictionary(Value(body, "song"));
            return ParseSongRows(Value(songData, "list"), false);
        }

        internal static List<OnlineSong> ParseLegacySearchResponse(string json)
        {
            // The official alternate search uses songmid/media_mid and the
            // older paydownload/payalbum names. Missing permission fields do
            // not grant anonymous audio access.
            Dictionary<string, object> root = ParseJson(json);
            RequireSuccess(root, "QQ音乐备用搜索");
            Dictionary<string, object> data = Dictionary(Value(root, "data"));
            Dictionary<string, object> songData = Dictionary(Value(data, "song"));
            return ParseSongRows(Value(songData, "list"), true);
        }

        private static List<OnlineSong> ParseSongRows(object value, bool legacy)
        {
            IEnumerable rows = value as IEnumerable;
            if (rows == null || rows is string || rows is IDictionary)
                throw ChangedFormat("QQ音乐搜索");
            List<OnlineSong> songs = new List<OnlineSong>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int scanned = 0;
            foreach (object item in rows)
            {
                if (++scanned > 200) break;
                Dictionary<string, object> row = Dictionary(item);
                if (row == null) continue;
                string id = PlainText(Value(row, legacy ? "songmid" : "mid"));
                string title = PlainText(Value(row, legacy ? "songname" : "title"));
                if (!legacy && title.Length == 0) title = PlainText(Value(row, "name"));
                if (!ValidIdentifier(id) || title.Length == 0 || !seen.Add(id)) continue;
                int duration;
                if (!TryInteger(Value(row, "interval"), out duration) || duration < 0 || duration > 86400) duration = 0;
                Dictionary<string, object> file = Dictionary(Value(row, "file"));
                Dictionary<string, object> pay = Dictionary(Value(row, "pay"));
                int payDown, payAlbum;
                string mediaId = PlainText(legacy ? Value(row, "media_mid") : Value(file, "media_mid"));
                songs.Add(new OnlineSong
                {
                    Title = title, Id = id, Artist = SingerText(Value(row, "singer")),
                    Album = PlainText(legacy ? Value(row, "albumname") : Value(Dictionary(Value(row, "album")), "name")),
                    DurationSeconds = duration, Provider = "QQ音乐", SourceUrl = OfficialSongUrl(id),
                    MediaId = ValidIdentifier(mediaId) ? mediaId : "",
                    FreeDownload = TryInteger(Value(pay, legacy ? "paydownload" : "pay_down"), out payDown) && payDown == 0 &&
                        (!legacy || TryInteger(Value(pay, "payalbum"), out payAlbum) && payAlbum == 0)
                });
                if (songs.Count >= PageSize) break;
            }
            if (songs.Count == 0 && scanned > 0) throw ChangedFormat("QQ音乐搜索");
            return songs;
        }

        public static string ResolveAudioUrl(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            if (!ValidIdentifier(song.Id) || !ValidIdentifier(song.MediaId)) return null;
            QqAccountSession account = QqAccountStore.Current;
            if (account != null)
            {
                // Recheck the official current-account response; saved cookies
                // and public profile information alone never prove login.
                try { if (!ValidateAccount(account)) account = null; }
                catch (Exception error)
                {
                    if (!IsSearchServiceFailure(error)) throw;
                    account.Invalidate(); account = null;
                }
            }
            return ResolveAudioUrl(song, account, delegate(string url, string operation)
                { return RequestJson(url, operation, account); });
        }

        internal static string ResolveAudioUrl(OnlineSong song, QqAccountSession account, Func<string, string, string> request)
        {
            if (song == null) throw new ArgumentNullException("song");
            bool authorized = account != null && account.IsUsable;
            // A free-download flag alone is insufficient: the server must also
            // grant an ordinary MP3 URL for this exact version of the song.
            if ((!song.FreeDownload && !authorized) || !ValidIdentifier(song.Id) || !ValidIdentifier(song.MediaId)) return null;
            string filename = "M500" + song.MediaId + ".mp3";
            string guid = (DateTime.UtcNow.Ticks % 10000000000L).ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "comm", new Dictionary<string, object> { { "uin", 0 }, { "format", "json" }, { "ct", 24 }, { "cv", 0 } } },
                { "req_0", new Dictionary<string, object>
                    {
                        { "module", "vkey.GetVkeyServer" }, { "method", "CgiGetVkey" },
                        { "param", new Dictionary<string, object>
                            {
                                { "guid", guid }, { "songmid", new string[] { song.Id } }, { "songtype", new int[] { 0 } },
                                { "uin", "0" }, { "loginflag", 0 }, { "platform", "20" }, { "filename", new string[] { filename } }
                            }
                        }
                    }
                }
            };
            if (authorized)
            {
                var comm = (Dictionary<string, object>)payload["comm"];
                comm["uin"] = account.Uin; comm["authst"] = account.MusicKey; comm["g_tk"] = account.Gtk;
                var param = (Dictionary<string, object>)((Dictionary<string, object>)payload["req_0"])["param"];
                param["uin"] = account.Uin; param["loginflag"] = 1;
            }
            return ParseAudioResponse(request(JsonRequestUrl(payload), "QQ音乐音频"), song, authorized);
        }

        internal static string ParseAudioResponse(string json, OnlineSong song)
        { return ParseAudioResponse(json, song, false); }

        internal static string ParseAudioResponse(string json, OnlineSong song, bool authorized)
        {
            if (song == null) throw new ArgumentNullException("song");
            if ((!song.FreeDownload && !authorized) || !ValidIdentifier(song.Id) || !ValidIdentifier(song.MediaId)) return null;
            Dictionary<string, object> root = ParseJson(json);
            RequireSuccess(root, "QQ音乐音频");
            Dictionary<string, object> module = Dictionary(Value(root, "req_0"));
            if (module == null) throw ChangedFormat("QQ音乐音频");
            RequireSuccess(module, "QQ音乐音频");
            Dictionary<string, object> data = Dictionary(Value(module, "data"));
            IEnumerable entries = Value(data, "midurlinfo") as IEnumerable;
            IEnumerable servers = Value(data, "sip") as IEnumerable;
            if (entries == null || entries is string || entries is IDictionary || servers == null || servers is string || servers is IDictionary)
                throw ChangedFormat("QQ音乐音频");
            string expectedFilename = "M500" + song.MediaId + ".mp3";
            foreach (object item in entries)
            {
                Dictionary<string, object> entry = Dictionary(item);
                if (entry == null) continue;
                if (!String.Equals(Convert.ToString(Value(entry, "songmid"), CultureInfo.InvariantCulture), song.Id, StringComparison.Ordinal)) continue;
                int result;
                if (!TryInteger(Value(entry, "result"), out result)) throw ChangedFormat("QQ音乐音频");
                if (result != 0) return null;
                foreach (string preview in new string[] { "trial", "is_trial", "free_trial", "preview", "is_preview", "trial_begin", "trial_end" })
                {
                    object marker = Value(entry, preview);
                    if (marker != null && Convert.ToString(marker, CultureInfo.InvariantCulture) != "0" &&
                        !String.Equals(Convert.ToString(marker, CultureInfo.InvariantCulture), "false", StringComparison.OrdinalIgnoreCase)) return null;
                }
                string purl = Convert.ToString(Value(entry, "purl"), CultureInfo.InvariantCulture) ?? "";
                if (purl.Length == 0) return null;
                string returnedFilename = Convert.ToString(Value(entry, "filename"), CultureInfo.InvariantCulture);
                if (!String.Equals(returnedFilename, expectedFilename, StringComparison.Ordinal) || purl.Length > 8192)
                    throw ChangedFormat("QQ音乐音频");
                Uri relative;
                if (purl.StartsWith("/", StringComparison.Ordinal) || purl.Contains("\\") || purl.IndexOf('#') >= 0 ||
                    !Uri.TryCreate(purl, UriKind.Relative, out relative))
                    throw ChangedFormat("QQ音乐音频");
                foreach (object server in servers)
                {
                    Uri baseUri;
                    string serverUrl = Convert.ToString(server, CultureInfo.InvariantCulture) ?? "";
                    if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out baseUri) ||
                        (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
                        !baseUri.IsDefaultPort || !String.IsNullOrEmpty(baseUri.UserInfo) ||
                        !String.IsNullOrEmpty(baseUri.Query) || !String.IsNullOrEmpty(baseUri.Fragment) || !IsAudioHost(baseUri.Host)) continue;
                    UriBuilder secureBase = new UriBuilder(baseUri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
                    Uri finalUri = new Uri(secureBase.Uri, relative);
                    if (!IsOrdinaryAudioUri(finalUri) ||
                        !String.Equals(Path.GetFileName(finalUri.AbsolutePath), expectedFilename, StringComparison.Ordinal)) continue;
                    return finalUri.AbsoluteUri;
                }
                throw new InvalidDataException("QQ音乐未返回可验证的普通音频地址，请在官方客户端下载后导入。");
            }
            return null;
        }

        public static bool IsOrdinaryAudioUri(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment) && IsAudioHost(uri.Host) &&
                Regex.IsMatch(uri.AbsolutePath, @"/M500[A-Za-z0-9]{1,64}\.mp3\z", RegexOptions.CultureInvariant);
        }

        private static bool IsAudioHost(string host)
        {
            return String.Equals(host, "aqqmusic.tc.qq.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".stream.qqmusic.qq.com", StringComparison.OrdinalIgnoreCase);
        }

        public static string OfficialSearchUrl(string query)
        {
            if (String.IsNullOrWhiteSpace(query)) return OfficialLoginUrl;
            return "https://y.qq.com/portal/search.html#searchid=1&remoteplace=txt.yqq.top&w=" + Uri.EscapeDataString(ValidateQuery(query));
        }

        public static string OfficialSongUrl(string songMid)
        {
            if (!ValidIdentifier(songMid)) throw new ArgumentException("QQ音乐歌曲编号无效。", "songMid");
            return "https://y.qq.com/n/ryqq/songDetail/" + songMid;
        }

        private static string JsonRequestUrl(Dictionary<string, object> payload)
        {
            return Endpoint + "?data=" + Uri.EscapeDataString(new JavaScriptSerializer().Serialize(payload));
        }

        private static string RequestJson(string url, string operation)
        { return RequestJson(url, operation, null); }

        private static string RequestJson(string url, string operation, QqAccountSession account)
        {
            Uri destination;
            if (!Uri.TryCreate(url, UriKind.Absolute, out destination) || !QqAccountSession.IsAccountHost(destination))
                throw new InvalidOperationException("QQ音乐请求目标不是官方接口。");
            if (account != null)
            {
                if (!destination.Query.StartsWith("?g_tk=", StringComparison.Ordinal) && destination.Query.IndexOf("&g_tk=", StringComparison.Ordinal) < 0)
                    url += "&g_tk=" + account.Gtk.ToString(CultureInfo.InvariantCulture);
                if (!destination.Query.StartsWith("?loginUin=", StringComparison.Ordinal) && destination.Query.IndexOf("&loginUin=", StringComparison.Ordinal) < 0)
                    url += "&loginUin=" + account.Uin;
            }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            if (account != null) request.CookieContainer = account.CookiesFor(destination);
            request.Method = "GET"; request.UserAgent = "RhythmHarmonica (.NET Framework)";
            request.Referer = "https://y.qq.com/"; request.Accept = "application/json";
            request.Timeout = request.ReadWriteTimeout = TimeoutMilliseconds; request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(operation + "暂不可用（HTTP " + (int)response.StatusCode + "），可打开QQ音乐官网。");
                    if (response.ContentLength > MaximumResponseBytes) throw new InvalidDataException(operation + "返回的数据过大，请缩小搜索范围。");
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        if (stream == null) throw ChangedFormat(operation);
                        byte[] chunk = new byte[8192];
                        for (;;)
                        {
                            int remaining = TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                            if (remaining <= 0) { request.Abort(); throw new TimeoutException(operation + "超时，请重试或打开QQ音乐官网。"); }
                            if (stream.CanTimeout) stream.ReadTimeout = remaining;
                            int count = stream.Read(chunk, 0, chunk.Length);
                            if (count == 0) break;
                            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException(operation + "返回的数据过大，请缩小搜索范围。");
                            buffer.Write(chunk, 0, count);
                        }
                        return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\ufeff');
                    }
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                string status = response == null ? "" : "（HTTP " + (int)response.StatusCode + "）";
                if (error.Response != null) error.Response.Close();
                string message = error.Status == WebExceptionStatus.Timeout ? operation + "超时，请重试或打开QQ音乐官网。" :
                    "无法连接" + operation + status + "，请检查网络或打开QQ音乐官网。";
                throw new InvalidOperationException(message, error);
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException(operation + "返回的字符格式有变化，可打开QQ音乐官网。", error); }
        }

        internal static bool ValidateAccount(QqAccountSession account)
        {
            return ValidateAccount(account, delegate(string url, QqAccountSession session)
                { return RequestJson(url, "QQ音乐账号验证", session); });
        }
        internal static bool ValidateAccount(QqAccountSession account, Func<string, QqAccountSession, string> request)
        {
            if (account == null) return false;
            if (DateTime.UtcNow >= account.ExpiresUtc) { account.MarkValidationFailure("Expired"); return false; }
            if (request == null) throw new ArgumentNullException("request");
            account.Invalidate();
            try
            {
                string url = BuildAccountRequestUrl(account);
                string current = null;
                try
                {
                    current = request(url, account);
                    if (ParseAccountResponse(current, account)) return true;
                    if (account.ValidationFailureKind == "Identity") return false;
                }
                catch (Exception error) { if (!IsSearchServiceFailure(error)) throw; current = null; }
                string nickname;
                if (current != null && TryCurrentAccountInfo(current, out nickname))
                {
                    // authst in the request body is itself an authority. Remove
                    // it AND omit cookies; simply omitting cookies is insufficient.
                    var anonymous = ParseJson(request(BuildAccountControlRequestUrl(account), null));
                    int rootCode, code;
                    var block = Dictionary(Value(anonymous, "account"));
                    if (!TryInteger(Value(anonymous, "code"), out rootCode) || !TryInteger(Value(block, "code"), out code))
                    { account.MarkValidationFailure("Format"); return false; }
                    if (rootCode != 0 || !IsExplicitLoginRefusal(code))
                    { account.MarkValidationFailure("Permission"); return false; }
                    return account.AcceptVerifiedCurrentAccount(nickname);
                }
                // The current official WEB frontend requests userid=0 for the
                // session's own profile. It now exposes encrypt_uin, with uin=0.
                url = BuildWebAccountRequestUrl(account);
                var authenticated = ParseJson(request(url, account));
                int webCode;
                if (!TryInteger(Value(authenticated, "code"), out webCode))
                { account.MarkValidationFailure("Format"); return false; }
                if (webCode != 0)
                { account.MarkValidationFailure(IsExplicitLoginRefusal(webCode) ? "Expired" : "Permission"); return false; }
                var creator = Dictionary(Value(Dictionary(Value(authenticated, "data")), "creator"));
                string identity = ProfileIdentity(creator);
                if (identity.Length != 0 && identity != account.Uin)
                { account.MarkValidationFailure("Identity"); return false; }
                bool self = BooleanFlag(Value(creator, "ishost"));
                if (Value(creator, "ishost") != null && !self)
                { account.MarkValidationFailure("Identity"); return false; }
                string opaque = Value(creator, "encrypt_uin") as string;
                bool redactedSelf = self && !String.IsNullOrWhiteSpace(opaque) && opaque.Length <= 256 &&
                    !Regex.IsMatch(opaque, @"[\x00-\x20\x7f]");
                if (identity.Length == 0 && !redactedSelf)
                { account.MarkValidationFailure("Format"); return false; }
                var webAnonymous = ParseJson(request(url, null));
                if (!TryInteger(Value(webAnonymous, "code"), out webCode))
                { account.MarkValidationFailure("Format"); return false; }
                if (!IsExplicitLoginRefusal(webCode))
                { account.MarkValidationFailure("Permission"); return false; }
                nickname = PlainText(Value(creator, "nick"));
                return identity.Length != 0 ? account.AcceptIdentity(identity, nickname) : account.AcceptVerifiedCurrentAccount(nickname);
            }
            catch (Exception error)
            {
                if (error is InvalidDataException || error is FormatException) account.MarkValidationFailure("Format");
                else if (error is IOException || error is TimeoutException || error is WebException || error.InnerException is WebException)
                    account.MarkValidationFailure("Network");
                else account.MarkValidationFailure("Unknown");
                throw;
            }
        }
        private static bool IsExplicitLoginRefusal(int code) { return code == 1000 || code == 104400 || code == 104401; }
        private static bool BooleanFlag(object value)
        { int flag; return value is bool ? (bool)value : TryInteger(value, out flag) && flag == 1; }
        private static bool TryCurrentAccountInfo(string json, out string nickname)
        {
            nickname = "";
            var root = ParseJson(json); var block = Dictionary(Value(root, "account"));
            int rootCode, code;
            if (!TryInteger(Value(root, "code"), out rootCode) || rootCode != 0 ||
                !TryInteger(Value(block, "code"), out code) || code != 0) return false;
            var info = Dictionary(Value(Dictionary(Value(block, "data")), "info"));
            nickname = PlainText(Value(info, "nick"));
            if (nickname.Length == 0) nickname = PlainText(Value(info, "nickname"));
            string picture = PlainText(Value(info, "headpic"));
            if (picture.Length == 0) picture = PlainText(Value(info, "face"));
            return info != null && (nickname.Length != 0 || picture.Length != 0);
        }
        internal static string BuildWebAccountRequestUrl(QqAccountSession account)
        {
            if (account == null) throw new ArgumentNullException("account");
            return "https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?cid=205360838&ct=24&userid=0&reqtype=0" +
                "&reqfrom=1&format=json&loginUin=" + account.Uin + "&hostUin=0&g_tk=" + account.Gtk.ToString(CultureInfo.InvariantCulture) +
                "&g_tk_new_20200303=" + account.Gtk.ToString(CultureInfo.InvariantCulture) + "&inCharset=utf8&outCharset=utf-8&platform=yqq.json&needNewCode=0";
        }
        private static string ProfileIdentity(Dictionary<string, object> creator)
        {
            string identity = QqAccountSession.NormalizeUin(Convert.ToString(Value(creator, "str_musicid"), CultureInfo.InvariantCulture));
            if (identity.Length == 0) identity = QqAccountSession.NormalizeUin(Convert.ToString(Value(creator, "musicid"), CultureInfo.InvariantCulture));
            if (identity.Length == 0) identity = QqAccountSession.NormalizeUin(Convert.ToString(Value(creator, "uin"), CultureInfo.InvariantCulture));
            return identity;
        }
        internal static string BuildAccountRequestUrl(QqAccountSession account)
        { return BuildCurrentAccountRequestUrl(account, true); }
        internal static string BuildAccountControlRequestUrl(QqAccountSession account)
        { return BuildCurrentAccountRequestUrl(account, false); }
        private static string BuildCurrentAccountRequestUrl(QqAccountSession account, bool includeAuthority)
        {
            if (account == null) throw new ArgumentNullException("account");
            var comm = new Dictionary<string, object> { { "uin", account.Uin },
                { "g_tk", account.Gtk }, { "format", "json" }, { "ct", 24 }, { "cv", 0 } };
            if (includeAuthority) comm["authst"] = account.MusicKey;
            if (account.LoginType == "1" || account.LoginType == "2") comm["tmeLoginType"] = account.LoginType;
            return JsonRequestUrl(new Dictionary<string, object>
            {
                { "comm", comm },
                { "account", new Dictionary<string, object> { { "module", "music.UserInfo.userInfoServer" },
                    { "method", "GetLoginUserInfo" }, { "param", new Dictionary<string, object>() } } }
            }) + "&g_tk=" + account.Gtk.ToString(CultureInfo.InvariantCulture) + "&loginUin=" + account.Uin;
        }
        internal static bool ParseAccountResponse(string json, QqAccountSession account)
        {
            if (account == null) return false;
            account.Invalidate();
            Dictionary<string, object> root = ParseJson(json), block = Dictionary(Value(root, "account"));
            int rootCode, code;
            if (!TryInteger(Value(root, "code"), out rootCode) || !TryInteger(Value(block, "code"), out code))
            { account.MarkValidationFailure("Format"); return false; }
            if (rootCode != 0 || code != 0)
            { account.MarkValidationFailure(IsExplicitLoginRefusal(code) ? "Expired" : "Permission"); return false; }
            var data = Dictionary(Value(block, "data"));
            var identities = new List<Dictionary<string, object>> { data };
            foreach (string name in new string[] { "userInfo", "userinfo", "user", "info", "baseInfo" })
                identities.Add(Dictionary(Value(data, name)));
            string identity = "", qqIdentity = "", nickname = "";
            foreach (var row in identities)
            {
                foreach (string name in new string[] { "str_musicid", "musicid" })
                {
                    string candidate = QqAccountSession.NormalizeUin(Convert.ToString(Value(row, name), CultureInfo.InvariantCulture));
                    if (candidate.Length == 0) continue;
                    if (candidate != account.Uin) { account.MarkValidationFailure("Identity"); return false; }
                    identity = candidate;
                }
                if (qqIdentity.Length == 0) qqIdentity = QqAccountSession.NormalizeUin(Convert.ToString(Value(row, "uin"), CultureInfo.InvariantCulture));
                if (nickname.Length == 0) nickname = PlainText(Value(row, "nick"));
                if (nickname.Length == 0) nickname = PlainText(Value(row, "nickname"));
            }
            // The music account ID takes priority over the original QQ number.
            // Generic userid aliases and public-profile creator fields are not login evidence.
            if (identity.Length == 0) identity = qqIdentity;
            if (identity.Length == 0) { account.MarkValidationFailure("Format"); return false; }
            return identity.Length != 0 && account.AcceptIdentity(identity, nickname);
        }

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes) throw ChangedFormat("QQ音乐");
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 64 };
                Dictionary<string, object> root = Dictionary(serializer.DeserializeObject(json.TrimStart('\ufeff')));
                if (root == null) throw ChangedFormat("QQ音乐");
                return root;
            }
            catch (ArgumentException error) { throw new InvalidDataException("QQ音乐返回的数据格式有变化，可打开官网检索。", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("QQ音乐返回的数据格式有变化，可打开官网检索。", error); }
        }

        private static void RequireSuccess(Dictionary<string, object> dictionary, string operation)
        {
            int code;
            if (!TryInteger(Value(dictionary, "code"), out code)) throw ChangedFormat(operation);
            if (code != 0)
            {
                var error = new InvalidOperationException(operation + "暂不可用（错误码 " + code.ToString(CultureInfo.InvariantCulture) + "），请打开QQ音乐官网。");
                error.Data["QqServiceCode"] = code;
                throw error;
            }
        }

        private static string ValidateQuery(string query)
        {
            if (String.IsNullOrWhiteSpace(query)) throw new ArgumentException("请输入歌名或歌手。", "query");
            query = query.Trim();
            if (query.Length > MaximumQueryLength) throw new ArgumentException("搜索词最多输入 100 个字符。", "query");
            foreach (char character in query) if (Char.IsControl(character)) throw new ArgumentException("搜索词不能包含控制字符或换行。", "query");
            try { new UTF8Encoding(false, true).GetByteCount(query); }
            catch (EncoderFallbackException error) { throw new ArgumentException("搜索词包含无效字符，请重新输入。", "query", error); }
            return query;
        }

        private static bool ValidIdentifier(string value) { return value != null && Identifier.IsMatch(value); }
        private static Dictionary<string, object> Dictionary(object value) { return value as Dictionary<string, object>; }
        private static object Value(Dictionary<string, object> dictionary, string key) { object value; return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null; }
        private static bool TryInteger(object value, out int result) { return Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result); }
        private static InvalidDataException ChangedFormat(string operation) { return new InvalidDataException(operation + "返回的数据格式有变化，可打开QQ音乐官网。"); }
        private static string SingerText(object value)
        {
            IEnumerable singers = value as IEnumerable;
            if (singers == null || value is string || value is IDictionary) return PlainText(value);
            List<string> names = new List<string>();
            foreach (object item in singers)
            {
                string name = PlainText(Value(Dictionary(item), "name"));
                if (name.Length != 0) names.Add(name);
                if (names.Count == 20) break;
            }
            return PlainText(String.Join(" / ", names.ToArray()));
        }
        private static string PlainText(object value)
        {
            string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (raw.Length > 4096) raw = raw.Substring(0, 4096);
            raw = HtmlTags.Replace(WebUtility.HtmlDecode(raw), "");
            StringBuilder clean = new StringBuilder();
            foreach (char character in raw) { if (!Char.IsControl(character)) clean.Append(character); else if (Char.IsWhiteSpace(character)) clean.Append(' '); }
            string text = Whitespace.Replace(clean.ToString(), " ").Trim();
            return text.Length > 240 ? text.Substring(0, 240) : text;
        }
    }
}
