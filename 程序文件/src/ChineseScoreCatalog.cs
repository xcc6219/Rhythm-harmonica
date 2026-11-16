using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HarmonicaStudio
{
    public sealed class ChineseScore
    {
        public string Id, Title, Artist, SourceUrl, License, Attribution, MidiUrl;
        public override string ToString() { return Title + " · " + Artist; }
    }

    public static class ChineseScoreCatalog
    {
        public const string SourceUrl = "https://github.com/music-x-lab/POP909-Dataset";
        public const string IndexUrl = "https://raw.githubusercontent.com/music-x-lab/POP909-Dataset/master/POP909/index.xlsx";
        public const string LicenseUrl = SourceUrl + "/blob/master/LICENSE";
        public const string SourceLicenseLabel = "POP909 · 仓库标注 MIT";
        public const int MaximumMidiBytes = 16 * 1024 * 1024;
        private const int MaximumIndexBytes = 2 * 1024 * 1024;
        private const string MidiPrefix = "https://raw.githubusercontent.com/music-x-lab/POP909-Dataset/master/POP909/";
        private const string ApiPrefix = "https://api.github.com/repos/music-x-lab/POP909-Dataset/contents/POP909/";
        private static readonly Regex EditionSuffix = new Regex(
            @"\s*[\(（\[【](?<edition>[^\)）\]】]{1,80})[\)）\]】]\s*$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex EditionName = new Regex(
            @"^(?:live(?:\s+at\s+.{1,60})?|现场(?:版)?|演唱会(?:版)?|伴奏(?:版)?|纯音乐(?:版)?|翻唱(?:版)?|cover|remix|demo|acoustic|原声(?:版)?|钢琴(?:版)?|piano(?:\s+version)?|(?:国语|粤语|中文版|英文版)(?:版)?|(?:\d{4}\s*)?(?:live|现场)(?:版)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public static List<ChineseScore> Search(string query)
        {
            return Search(query, DefaultCatalogPath());
        }

        public static List<ChineseScore> Search(string query, string catalogPath)
        {
            return Filter(ParseIndex(ReadIndex(catalogPath)), ValidateQuery(query));
        }

        public static List<ChineseScore> SearchFrom(string json, string query)
        {
            return Filter(ParseIndex(json), ValidateQuery(query));
        }

        public static List<ChineseScore> FindExact(string title, string artist)
        {
            return Exact(ParseIndex(ReadIndex(DefaultCatalogPath())), title, artist);
        }

        public static List<ChineseScore> FindExactFrom(string json, string title, string artist)
        {
            return Exact(ParseIndex(json), title, artist);
        }

        public static byte[] Download(ChineseScore score)
        {
            return Download(score, CatalogDownloads.CacheDirectory, delegate(Uri uri, int limit)
            {
                return CatalogDownloads.ReadBytes(uri, limit, uri.Host == "api.github.com" ?
                    "application/vnd.github.raw+json" : "audio/midi, application/octet-stream;q=0.9");
            });
        }

        internal static byte[] Download(ChineseScore score, string cacheDirectory, Func<Uri, int, byte[]> fetch)
        {
            if (score == null) throw new ArgumentNullException("score", "请先选择中文乐谱。");
            Uri uri;
            if (!TryMidiUri(score.Id, score.MidiUrl, out uri))
                throw new ArgumentException("中文乐谱地址无效。", "score");
            if (fetch == null) throw new ArgumentNullException("fetch");
            string key = "pop909-" + score.Id + ".mid";
            byte[] cached = CatalogDownloads.ReadCache(cacheDirectory, key, ValidateMidi);
            if (cached != null) return cached;
            byte[] bytes;
            try
            {
                bytes = fetch(uri, MaximumMidiBytes);
                ValidateMidi(bytes);
            }
            catch (Exception primary)
            {
                if (!CatalogDownloads.CanTryAlternative(primary)) throw;
                // The official Contents API serves raw bytes on api.github.com;
                // github.com/raw merely redirects back to the original host.
                Uri backup = new Uri(ApiPrefix + score.Id + "/" + score.Id + ".mid?ref=master");
                try { bytes = fetch(backup, MaximumMidiBytes); ValidateMidi(bytes); }
                catch (Exception alternate)
                {
                    if (!CatalogDownloads.IsDownloadFailure(alternate)) throw;
                    throw new InvalidOperationException("中文乐谱下载失败：" + alternate.Message,
                        new AggregateException(primary, alternate));
                }
            }
            CatalogDownloads.WriteCache(cacheDirectory, key, bytes);
            return bytes;
        }

        internal static bool TryDownloadUri(string url, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrEmpty(url) || url.Length > 4096) return false;
            string prefix = url.StartsWith(MidiPrefix, StringComparison.Ordinal) ? MidiPrefix :
                url.StartsWith(ApiPrefix, StringComparison.Ordinal) ? ApiPrefix : null;
            if (prefix == null || url.Length < prefix.Length + 3) return false;
            string id = url.Substring(prefix.Length, 3);
            if (prefix == MidiPrefix) return TryMidiUri(id, url, out uri);
            return ValidId(id) && String.Equals(url, ApiPrefix + id + "/" + id + ".mid?ref=master", StringComparison.Ordinal) &&
                Uri.TryCreate(url, UriKind.Absolute, out uri);
        }

        internal static string NormalizeTitle(string title)
        {
            string text = (title ?? "").Trim().Normalize(NormalizationForm.FormKC);
            // Remove explicit version labels only. Song-name fragments such as
            // 小夜曲 and unknown parenthesized words are never fuzzy matches.
            for (int i = 0; i < 4; i++)
            {
                Match suffix = EditionSuffix.Match(text);
                if (!suffix.Success || !EditionName.IsMatch(suffix.Groups["edition"].Value.Trim())) break;
                text = text.Substring(0, suffix.Index).TrimEnd();
            }
            return NormalizeText(text);
        }

        internal static bool HasEditionSuffix(string title)
        {
            string text = (title ?? "").Trim().Normalize(NormalizationForm.FormKC);
            Match suffix = EditionSuffix.Match(text);
            return suffix.Success && EditionName.IsMatch(suffix.Groups["edition"].Value.Trim());
        }

        internal static bool TryMidiUri(string id, string url, out Uri uri)
        {
            uri = null;
            if (!ValidId(id) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            string expected = MidiPrefix + id + "/" + id + ".mid";
            return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "raw.githubusercontent.com" &&
                uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
                uri.Fragment.Length == 0 && String.Equals(url, expected, StringComparison.Ordinal);
        }

        internal static void ValidateMidi(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 14 || bytes.Length > MaximumMidiBytes ||
                bytes[0] != 'M' || bytes[1] != 'T' || bytes[2] != 'h' || bytes[3] != 'd')
                throw new InvalidDataException("下载内容不是有效 MIDI。");
            string temporaryPath = Path.Combine(Path.GetTempPath(), "rhythm-pop909-" + Guid.NewGuid().ToString("N") + ".mid");
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                SongScore score = ScoreIO.LoadMidi(temporaryPath);
                bool hasNotes = false;
                foreach (ScoreTrack track in score.Tracks)
                    if (track.Notes.Count != 0) { hasNotes = true; break; }
                if (!hasNotes) throw new InvalidDataException("MIDI 未包含可用音符。");
            }
            catch (FormatException error) { throw new InvalidDataException("MIDI 格式无法读取。", error); }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }

        private static string DefaultCatalogPath()
        {
            return Path.Combine(AppPaths.ContentDirectory, "data", "catalog", "pop909.json");
        }

        private static string ReadIndex(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("中文曲库路径无效。", "path");
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumIndexBytes) throw new InvalidDataException("中文曲库索引过大。");
                    using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        return reader.ReadToEnd();
                }
            }
            catch (FileNotFoundException error) { throw new InvalidOperationException("中文曲库索引缺失。", error); }
            catch (DirectoryNotFoundException error) { throw new InvalidOperationException("中文曲库索引缺失。", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("中文曲库编码无效。", error); }
        }

        private static List<ChineseScore> ParseIndex(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumIndexBytes)
                throw new InvalidDataException("中文曲库索引无效。");
            Dictionary<string, object> root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaximumIndexBytes;
                serializer.RecursionLimit = 16;
                root = serializer.DeserializeObject(json.TrimStart('\ufeff')) as Dictionary<string, object>;
            }
            catch (ArgumentException error) { throw new InvalidDataException("中文曲库索引格式错误。", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("中文曲库索引格式错误。", error); }
            if (root == null) throw new InvalidDataException("中文曲库索引格式错误。");
            object rowsObject;
            if (!root.TryGetValue("songs", out rowsObject) || !(rowsObject is IEnumerable) ||
                rowsObject is string || rowsObject is IDictionary)
                throw new InvalidDataException("中文曲库缺少歌曲列表。");
            List<ChineseScore> result = new List<ChineseScore>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object rowObject in (IEnumerable)rowsObject)
            {
                Dictionary<string, object> row = rowObject as Dictionary<string, object>;
                if (row == null || result.Count >= 909) throw new InvalidDataException("中文曲库条目无效。");
                string id = Field(row, "id"), title = Field(row, "title"), artist = Field(row, "artist");
                if (!ValidId(id) || !ids.Add(id) || String.IsNullOrWhiteSpace(title) ||
                    title.Length > 240 || artist.Length > 240 || ContainsControl(title) || ContainsControl(artist))
                    throw new InvalidDataException("中文曲库条目无效。");
                string source = SourceUrl + "/blob/master/POP909/" + id + "/" + id + ".mid";
                result.Add(new ChineseScore
                {
                    Id = id, Title = title, Artist = artist, SourceUrl = source,
                    License = SourceLicenseLabel,
                    Attribution = "Music X Lab (2020) · " + artist + " · " + source,
                    MidiUrl = MidiPrefix + id + "/" + id + ".mid"
                });
            }
            if (result.Count == 0) throw new InvalidDataException("中文曲库索引为空。");
            return result;
        }

        private static List<ChineseScore> Filter(List<ChineseScore> scores, string query)
        {
            string keyword = NormalizeText(query);
            if (keyword.Length == 0) return scores;
            List<ChineseScore> result = new List<ChineseScore>();
            foreach (ChineseScore score in scores)
                if (NormalizeText(score.Title).Contains(keyword) || NormalizeText(score.Artist).Contains(keyword) ||
                    (NormalizeText(score.Title) + NormalizeText(score.Artist)).Contains(keyword)) result.Add(score);
            return result;
        }

        private static List<ChineseScore> Exact(List<ChineseScore> scores, string title, string artist)
        {
            title = NormalizeTitle(ValidateQuery(title));
            artist = NormalizeText(ValidateQuery(artist));
            List<ChineseScore> result = new List<ChineseScore>();
            if (title.Length == 0) return result;
            foreach (ChineseScore score in scores)
                if (NormalizeTitle(score.Title) == title &&
                    (artist.Length == 0 || NormalizeText(score.Artist) == artist)) result.Add(score);
            return result;
        }

        private static string NormalizeText(string text)
        {
            StringBuilder normalized = new StringBuilder();
            foreach (char ch in (text ?? "").Normalize(NormalizationForm.FormKC))
            {
                char simplified = Simplify(ch);
                if (!Char.IsWhiteSpace(simplified) && !Char.IsPunctuation(simplified) && !Char.IsControl(simplified))
                    normalized.Append(Char.ToLowerInvariant(simplified));
            }
            return normalized.ToString();
        }

        private static char Simplify(char ch)
        {
            // A small, explicit alias table for common singers and titles. This
            // is not a claim to provide a full Traditional Chinese converter.
            const string traditional = "倫劉陳鄧麗張學許謝楊華蘇孫葉韋羅馬龍鳳愛國風雲夢時聲樂塵憶說謊會後來願當聽見讓過開關萬點線紅綠歡離遠傷誰這裏裡從個麼給寶貝該還飛與爲為歲臺灣黃蕭蘭齊賢彥斌濤傑傳奇東方雙鳥無盡體諒緣陽陰遙搖滾動戀燈遊兒擁對變淚驚覺斷遺淺藍滿聖誕鋼琴國語廣粵";
            const string simplified = "伦刘陈邓丽张学许谢杨华苏孙叶韦罗马龙凤爱国风云梦时声乐尘忆说谎会后来愿当听见让过开关万点线红绿欢离远伤谁这里里从个么给宝贝该还飞与为为岁台湾黄萧兰齐贤彦斌涛杰传奇东方双鸟无尽体谅缘阳阴遥摇滚动恋灯游儿拥对变泪惊觉断遗浅蓝满圣诞钢琴国语广粤";
            int index = traditional.IndexOf(ch);
            return index < 0 ? ch : simplified[index];
        }

        private static string ValidateQuery(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length > 100 || ContainsControl(query)) throw new ArgumentException("搜索词无效或超过100字。", "query");
            try { new UTF8Encoding(false, true).GetByteCount(query); }
            catch (EncoderFallbackException error) { throw new ArgumentException("搜索词包含无效字符。", "query", error); }
            return query;
        }

        private static bool ContainsControl(string text)
        {
            foreach (char ch in text) if (Char.IsControl(ch)) return true;
            return false;
        }

        private static bool ValidId(string id)
        {
            int value;
            return id != null && id.Length == 3 && id[0] >= '0' && id[0] <= '9' &&
                id[1] >= '0' && id[1] <= '9' && id[2] >= '0' && id[2] <= '9' &&
                Int32.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= 909;
        }

        private static string Field(Dictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) && value is string ? (string)value : "";
        }

    }

    internal sealed class CatalogDownloadException : InvalidOperationException
    {
        internal readonly bool Retryable;
        internal CatalogDownloadException(string message, bool retryable, Exception cause)
            : base(message, cause) { Retryable = retryable; }
    }

    internal static class CatalogDownloads
    {
        private const int TimeoutMilliseconds = 12000;
        private const long MaximumCacheBytes = 64L * 1024 * 1024;
        private static readonly object CacheGate = new object();
        internal static string CacheDirectory
        { get { return Path.Combine(AppPaths.ContentDirectory, "data", "cache", "online-midi"); } }

        internal static bool IsDownloadFailure(Exception error)
        {
            return error is CatalogDownloadException || error is WebException || error is IOException ||
                error is InvalidDataException || error is TimeoutException;
        }

        internal static bool CanTryAlternative(Exception error)
        {
            CatalogDownloadException known = error as CatalogDownloadException;
            WebException web = error as WebException;
            if (known == null && web != null) known = DescribeFailure(web);
            return known != null ? known.Retryable : IsDownloadFailure(error);
        }

        internal static byte[] ReadBytes(Uri uri, int limit, string accept)
        {
            Uri checkedUri;
            if (uri == null || !(ChineseScoreCatalog.TryDownloadUri(uri.AbsoluteUri, out checkedUri) ||
                FreeScoreCatalog.TryOfficialUri(uri.AbsoluteUri, out checkedUri)))
                throw new ArgumentException("曲库下载地址无效。", "uri");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.UserAgent = "RhythmHarmonica/1.3 (.NET Framework)";
            request.Accept = accept;
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
                        throw new CatalogDownloadException("谱源返回非成功响应。", false, null);
                    if (response.ContentLength > limit) throw new InvalidDataException("曲库文件超过大小限制。");
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream output = new MemoryStream())
                    {
                        if (stream == null) throw new InvalidDataException("谱源未返回内容。");
                        byte[] buffer = new byte[8192];
                        for (;;)
                        {
                            int remaining = TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                            if (remaining <= 0) { request.Abort(); throw new TimeoutException("谱源下载超时，请重试。"); }
                            if (stream.CanTimeout) stream.ReadTimeout = remaining;
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read == 0) break;
                            if (output.Length + read > limit) throw new InvalidDataException("曲库文件超过大小限制。");
                            output.Write(buffer, 0, read);
                        }
                        return output.ToArray();
                    }
                }
            }
            catch (WebException error)
            {
                CatalogDownloadException mapped = DescribeFailure(error);
                if (error.Response != null) error.Response.Close();
                throw mapped;
            }
            catch (IOException error) { throw new IOException("谱源读取中断，请重试。", error); }
        }

        internal static CatalogDownloadException DescribeFailure(WebException error)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            return DescribeFailure(error, response == null ? 0 : (int)response.StatusCode);
        }

        internal static CatalogDownloadException DescribeFailure(WebException error, int status)
        {
            if (status == 404) return new CatalogDownloadException("谱源文件不存在（404）。", false, error);
            if (status == 401 || status == 403 || status == 429)
                return new CatalogDownloadException("谱源限制访问，请稍后重试。", false, error);
            if (status == 408 || status >= 500)
                return new CatalogDownloadException("谱源暂时不可用，请重试。", true, error);
            if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
                return new CatalogDownloadException("无法安全连接谱源。", false, error);
            if (error.Status == WebExceptionStatus.Timeout)
                return new CatalogDownloadException("谱源下载超时，请重试。", true, error);
            if (error.Status == WebExceptionStatus.NameResolutionFailure || error.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                return new CatalogDownloadException("无法解析谱源域名。", true, error);
            if (error.Status == WebExceptionStatus.ConnectFailure || error.Status == WebExceptionStatus.ConnectionClosed ||
                error.Status == WebExceptionStatus.ReceiveFailure || error.Status == WebExceptionStatus.SendFailure)
                return new CatalogDownloadException("谱源连接中断，请重试。", true, error);
            return new CatalogDownloadException("谱源请求失败，请查看来源。", false, error);
        }

        internal static string CacheKey(string prefix, string url)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(url));
                StringBuilder name = new StringBuilder(prefix + "-");
                foreach (byte value in bytes) name.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return name + ".mid";
            }
        }

        internal static byte[] ReadCache(string directory, string key, Action<byte[]> validate)
        {
            if (String.IsNullOrEmpty(directory)) return null;
            string path = Path.Combine(directory, key);
            lock (CacheGate)
            {
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length > ChineseScoreCatalog.MaximumMidiBytes)
                            throw new InvalidDataException("缓存文件过大。");
                        using (MemoryStream copy = new MemoryStream())
                        { stream.CopyTo(copy); byte[] bytes = copy.ToArray(); validate(bytes); return bytes; }
                    }
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return null;
            }
        }

        internal static void WriteCache(string directory, string key, byte[] bytes)
        {
            if (String.IsNullOrEmpty(directory)) return;
            string temporary = null;
            lock (CacheGate)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string destination = Path.Combine(directory, key);
                    if (File.Exists(destination)) return;
                    // Keep the staging name short: a valid cached MIDI path can
                    // exceed Win32's path limit if another full hash is appended.
                    temporary = Path.Combine(directory, ".cache-" + Guid.NewGuid().ToString("N") + ".partial");
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, destination);
                    List<FileInfo> files = new List<FileInfo>();
                    long total = 0;
                    foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("*.mid"))
                    {
                        if (!file.Name.StartsWith("pop909-", StringComparison.Ordinal) &&
                            !file.Name.StartsWith("mutopia-", StringComparison.Ordinal)) continue;
                        files.Add(file); total += file.Length;
                    }
                    files.Sort(delegate(FileInfo left, FileInfo right) { return left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc); });
                    foreach (FileInfo file in files)
                    {
                        if (total <= MaximumCacheBytes) break;
                        long length = file.Length; file.Delete(); total -= length;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
