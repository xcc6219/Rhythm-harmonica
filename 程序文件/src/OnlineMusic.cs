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
    public sealed class OnlineSong
    {
        public string Title;
        public string Artist;
        public string Album;
        public string Id;
        public string Provider;
        public string SourceUrl;
        public string MediaId;
        public string FileHash;
        public string AlbumId;
        public bool FreeDownload;
        public int DurationSeconds;

        public override string ToString()
        {
            return Title + (String.IsNullOrEmpty(Artist) ? "" : " · " + Artist);
        }
    }

    public static class KugouCatalog
    {
        // Reference project only. This public website endpoint is not an
        // official developer API and may change or reject anonymous requests.
        public const string SourceUrl = "https://github.com/MakcRe/KuGouMusicApi";
        public const string OfficialDownloadUrl = "https://download.kugou.com/";
        public const int PageSize = 20;
        public const int MaximumQueryLength = 100;
        public const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int TimeoutMilliseconds = 12000;
        private const string Endpoint = "https://songsearch.kugou.com/song_search_v2";
        private const string AudioEndpoint = "https://m.kugou.com/app/i/getSongInfo.php";
        private static readonly Regex HashIdentifier = new Regex("\\A[A-Fa-f0-9]{32}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex NumericIdentifier = new Regex("\\A[0-9]{1,20}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex AudioPath = new Regex(@"\A/[0-9]{12}/[A-Fa-f0-9]{32}/v3/[A-Fa-f0-9]{32}/yp/full/[A-Za-z0-9_-]{1,180}\.mp3\z", RegexOptions.CultureInvariant);
        private static readonly Regex HtmlTags = new Regex("<[^>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex Whitespace = new Regex("\\s+", RegexOptions.CultureInvariant);

        public static List<OnlineSong> Search(string query, int page)
        {
            query = ValidateQuery(query);
            if (page < 1 || page > 1000)
                throw new ArgumentOutOfRangeException("page", "搜索页码应在 1 到 1000 之间。");
            string url = Endpoint + "?keyword=" + Uri.EscapeDataString(query) +
                "&page=" + page.ToString(CultureInfo.InvariantCulture) +
                "&pagesize=" + PageSize.ToString(CultureInfo.InvariantCulture) +
                "&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "RhythmHarmonica/1.1 (.NET Framework)";
            request.Accept = "application/json";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException("酷狗搜索接口暂不可用，可打开官方网页检索。");
                    if (response.ContentLength > MaximumResponseBytes)
                        throw new InvalidDataException("酷狗搜索返回的数据过大，请缩小搜索范围。");
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        if (stream == null)
                            throw new InvalidDataException("酷狗搜索未返回数据，可打开官方网页检索。");
                        byte[] chunk = new byte[8192];
                        for (;;)
                        {
                            int remaining = TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                            if (remaining <= 0)
                            {
                                request.Abort();
                                throw new TimeoutException("酷狗搜索超时，可重试或打开官方网页。");
                            }
                            if (stream.CanTimeout) stream.ReadTimeout = remaining;
                            int read = stream.Read(chunk, 0, chunk.Length);
                            if (read == 0) break;
                            if (buffer.Length + read > MaximumResponseBytes)
                                throw new InvalidDataException("酷狗搜索返回的数据过大，请缩小搜索范围。");
                            buffer.Write(chunk, 0, read);
                        }
                        string json = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                        return ParseResponse(json.TrimStart('\ufeff'));
                    }
                }
            }
            catch (WebException error)
            {
                if (error.Response != null) error.Response.Close();
                string message;
                if (error.Status == WebExceptionStatus.Timeout)
                    message = "酷狗搜索超时，可重试或打开官方网页。";
                else if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
                    message = "无法安全连接酷狗搜索，请检查系统时间或打开官方网页。";
                else
                    message = "无法连接酷狗搜索，可检查网络或打开官方网页。";
                throw new InvalidOperationException(message, error);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("酷狗搜索返回的数据格式有变化，可打开官方网页检索。", error);
            }
            catch (IOException error)
            {
                throw new IOException("读取酷狗搜索结果失败，可重试或打开官方网页。", error);
            }
        }

        internal static List<OnlineSong> ParseResponse(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes)
                throw new InvalidDataException("酷狗搜索返回的数据为空或过大，可打开官方网页检索。");
            Dictionary<string, object> root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaximumResponseBytes;
                serializer.RecursionLimit = 64;
                root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("酷狗搜索返回的数据格式有变化，可打开官方网页检索。", error);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidDataException("酷狗搜索返回的数据格式有变化，可打开官方网页检索。", error);
            }
            int status, errorCode;
            if (root == null || !TryInteger(Value(root, "status"), out status))
                throw new InvalidDataException("酷狗搜索返回的数据格式有变化，可打开官方网页检索。");
            object errorValue = Value(root, "error_code");
            errorCode = 0;
            if (errorValue != null && !TryInteger(errorValue, out errorCode))
                throw new InvalidDataException("酷狗搜索返回的状态格式有变化，可打开官方网页检索。");
            if (status != 1 || errorCode != 0)
            {
                if (errorCode == 152)
                    throw new InvalidOperationException("酷狗暂时拒绝匿名搜索，请打开官方网页检索。");
                throw new InvalidOperationException("酷狗搜索暂不可用（错误码 " +
                    errorCode.ToString(CultureInfo.InvariantCulture) + "），可打开官方网页检索。");
            }
            Dictionary<string, object> data = Value(root, "data") as Dictionary<string, object>;
            if (data == null)
                throw new InvalidDataException("酷狗搜索结果格式有变化，可打开官方网页检索。");
            IEnumerable rows = Value(data, "lists") as IEnumerable;
            if (rows == null || rows is string || rows is IDictionary)
                throw new InvalidDataException("酷狗搜索结果缺少歌曲列表，可打开官方网页检索。");
            List<OnlineSong> songs = new List<OnlineSong>();
            int scanned = 0;
            foreach (object value in rows)
            {
                if (++scanned > 200) break;
                Dictionary<string, object> row = value as Dictionary<string, object>;
                if (row == null) continue;
                string title = PlainText(Value(row, "SongName"));
                if (title.Length == 0) title = PlainText(Value(row, "OriSongName"));
                if (title.Length == 0) continue;
                int duration;
                if (!TryInteger(Value(row, "Duration"), out duration) || duration < 0 || duration > 86400)
                    duration = 0;
                string id = PlainText(Value(row, "MixSongID"));
                if (id.Length == 0) id = PlainText(Value(row, "ID"));
                string hash = PlainText(Value(row, "FileHash"));
                string albumId = PlainText(Value(row, "AlbumID"));
                if (!ValidAlbumId(albumId)) albumId = "";
                bool freeDownload = ValidHash(hash) && IsZero(row, "PayType") && IsZero(row, "Price") &&
                    IsZero(row, "Privilege") && IsZero(row, "FailProcess") &&
                    String.Equals(PlainText(Value(row, "ExtName")), "mp3", StringComparison.OrdinalIgnoreCase);
                songs.Add(new OnlineSong
                {
                    Title = title,
                    Artist = PlainText(Value(row, "SingerName")),
                    Album = PlainText(Value(row, "AlbumName")),
                    DurationSeconds = duration,
                    Id = id,
                    Provider = "酷狗音乐",
                    FileHash = ValidHash(hash) ? hash.ToUpperInvariant() : "",
                    AlbumId = albumId,
                    FreeDownload = freeDownload,
                    SourceUrl = ValidHash(hash) ? BuildOfficialSongUrl(hash, albumId) : OfficialSearchUrl(MetadataQuery(title, ""))
                });
                if (songs.Count == PageSize) break;
            }
            if (songs.Count == 0 && scanned > 0)
                throw new InvalidDataException("酷狗返回了歌曲列表，但内容格式有变化，可打开官方网页检索。");
            return songs;
        }

        public static string OfficialSearchUrl(string query)
        {
            if (String.IsNullOrWhiteSpace(query)) return "https://www.kugou.com/";
            return "https://www.kugou.com/yy/html/search.html#searchType=song&searchKeyWord=" +
                Uri.EscapeDataString(ValidateQuery(query));
        }

        public static string OfficialScoreUrl(string query)
        {
            // The official score website has no verified public query URL.
            return "https://pulu.kugou.com/";
        }

        public static string OfficialSongUrl(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            if (ValidHash(song.FileHash)) return BuildOfficialSongUrl(song.FileHash, song.AlbumId);
            return OfficialSearchUrl(MetadataQuery(song.Title, song.Artist));
        }

        private static string BuildOfficialSongUrl(string hash, string albumId)
        {
            string url = "https://www.kugou.com/song/#hash=" + hash.ToUpperInvariant();
            return ValidAlbumId(albumId) ? url + "&album_id=" + albumId : url;
        }

        public static string ResolveAudioUrl(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            // Search permission and an actual full-file response must both
            // agree. No account, device identity or authorization is invented.
            if (!song.FreeDownload || !ValidHash(song.FileHash)) return null;
            return ParseAudioResponse(RequestAudioJson(AudioEndpoint + "?cmd=playInfo&hash=" + song.FileHash.ToUpperInvariant()), song);
        }

        internal static string ParseAudioResponse(string json, OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            if (!song.FreeDownload || !ValidHash(song.FileHash)) return null;
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes)
                throw AudioFormatChanged();
            Dictionary<string, object> root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 64 };
                root = serializer.DeserializeObject(json.TrimStart('\ufeff')) as Dictionary<string, object>;
            }
            catch (ArgumentException error) { throw new InvalidDataException("酷狗音频返回的数据格式有变化，请在官方客户端下载后导入。", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("酷狗音频返回的数据格式有变化，请在官方客户端下载后导入。", error); }
            int status, errorCode;
            if (root == null || !TryInteger(Value(root, "status"), out status) || !TryInteger(Value(root, "errcode"), out errorCode))
                throw AudioFormatChanged();
            if (status == 0) return null;
            if (status != 1 || errorCode != 0)
                throw new InvalidOperationException("酷狗音频暂不可用（错误码 " + errorCode.ToString(CultureInfo.InvariantCulture) + "），请在官方客户端下载后导入。");
            string returnedHash = PlainText(Value(root, "hash"));
            string requestedHash = PlainText(Value(root, "req_hash"));
            if (!String.Equals(returnedHash, song.FileHash, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(requestedHash, song.FileHash, StringComparison.OrdinalIgnoreCase)) throw AudioFormatChanged();
            if (!IsZero(root, "pay_type") || !IsZero(root, "privilege") || !IsZero(root, "128privilege") || !IsZero(root, "fail_process"))
                return null;
            int duration;
            if (!TryInteger(Value(root, "timeLength"), out duration) || duration <= 0 || duration > 86400)
                throw AudioFormatChanged();
            if (song.DurationSeconds > 0 && duration < song.DurationSeconds - 1) return null;
            if (!String.Equals(PlainText(Value(root, "extName")), "mp3", StringComparison.OrdinalIgnoreCase)) return null;
            string rawUrl = Convert.ToString(Value(root, "url"), CultureInfo.InvariantCulture) ?? "";
            if (rawUrl.Length == 0) return null;
            Uri uri;
            if (rawUrl.Length > 8192 || !Uri.TryCreate(rawUrl, UriKind.Absolute, out uri) || !IsOrdinaryAudioUri(uri))
                throw new InvalidDataException("酷狗未返回可验证的普通完整音频地址，请在官方客户端下载后导入。");
            if (!String.Equals(uri.AbsolutePath.Split('/')[4], song.FileHash, StringComparison.OrdinalIgnoreCase)) throw AudioFormatChanged();
            return uri.AbsoluteUri;
        }

        public static bool IsOrdinaryAudioUri(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment) &&
                (String.Equals(uri.Host, "sharefs.kugou.com", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(uri.Host, "sharefs.tx.kugou.com", StringComparison.OrdinalIgnoreCase)) &&
                AudioPath.IsMatch(uri.AbsolutePath);
        }

        private static string RequestAudioJson(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.UserAgent = "RhythmHarmonica (.NET Framework)";
            request.Referer = "https://m.kugou.com/"; request.Accept = "application/json";
            request.Timeout = request.ReadWriteTimeout = TimeoutMilliseconds; request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("酷狗音频暂不可用（HTTP " + (int)response.StatusCode + "），可打开酷狗官网。");
                    if (response.ContentLength > MaximumResponseBytes) throw AudioFormatChanged();
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        if (stream == null) throw AudioFormatChanged();
                        byte[] chunk = new byte[8192];
                        for (;;)
                        {
                            int remaining = TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                            if (remaining <= 0) { request.Abort(); throw new TimeoutException("酷狗音频查询超时，请重试或打开酷狗官网。"); }
                            if (stream.CanTimeout) stream.ReadTimeout = remaining;
                            int count = stream.Read(chunk, 0, chunk.Length);
                            if (count == 0) break;
                            if (buffer.Length + count > MaximumResponseBytes) throw AudioFormatChanged();
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
                throw new InvalidOperationException(error.Status == WebExceptionStatus.Timeout ?
                    "酷狗音频查询超时，请重试或打开酷狗官网。" : "无法连接酷狗音频" + status + "，请检查网络或打开酷狗官网。", error);
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("酷狗音频返回的字符格式有变化，请在官方客户端下载后导入。", error); }
        }

        private static bool ValidHash(string value) { return value != null && HashIdentifier.IsMatch(value); }
        private static bool ValidAlbumId(string value) { return value != null && NumericIdentifier.IsMatch(value); }
        private static bool IsZero(Dictionary<string, object> row, string key) { int value; return TryInteger(Value(row, key), out value) && value == 0; }
        private static InvalidDataException AudioFormatChanged() { return new InvalidDataException("酷狗音频返回的数据格式有变化，请在官方客户端下载后导入。"); }
        private static string MetadataQuery(string title, string artist)
        {
            string query = ((title ?? "") + " " + (artist ?? "")).Trim();
            if (query.Length <= MaximumQueryLength) return query;
            int length = Char.IsHighSurrogate(query[MaximumQueryLength - 1]) ? MaximumQueryLength - 1 : MaximumQueryLength;
            return query.Substring(0, length);
        }

        private static string ValidateQuery(string query)
        {
            if (String.IsNullOrWhiteSpace(query))
                throw new ArgumentException("请输入歌名或歌手。", "query");
            query = query.Trim();
            if (query.Length > MaximumQueryLength)
                throw new ArgumentException("搜索词最多输入 100 个字符。", "query");
            foreach (char character in query)
                if (Char.IsControl(character))
                    throw new ArgumentException("搜索词不能包含控制字符或换行。", "query");
            try { new UTF8Encoding(false, true).GetByteCount(query); }
            catch (EncoderFallbackException error)
            {
                throw new ArgumentException("搜索词包含无效字符，请重新输入。", "query", error);
            }
            return query;
        }

        private static object Value(Dictionary<string, object> dictionary, string name)
        {
            object value;
            return dictionary.TryGetValue(name, out value) ? value : null;
        }

        private static bool TryInteger(object value, out int result)
        {
            return Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static string PlainText(object value)
        {
            string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (raw.Length > 4096) raw = raw.Substring(0, 4096);
            raw = HtmlTags.Replace(WebUtility.HtmlDecode(raw), "");
            StringBuilder clean = new StringBuilder();
            foreach (char character in raw)
            {
                if (!Char.IsControl(character)) clean.Append(character);
                else if (Char.IsWhiteSpace(character)) clean.Append(' ');
            }
            string text = Whitespace.Replace(clean.ToString(), " ").Trim();
            return text.Length > 240 ? text.Substring(0, 240) : text;
        }
    }
}
