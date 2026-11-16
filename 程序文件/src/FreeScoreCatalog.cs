using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HarmonicaStudio
{
    public sealed class FreeScore
    {
        public string Title, Composer, Instrument, License, MidiUrl, InfoUrl;
        public string SourceEdition, Attribution, LicenseUrl;
        public string AudioFilePath;
        public string Provider = "Mutopia";
        public bool Chinese;
        public override string ToString() { return Title + " · " + Composer; }
    }

    public sealed class FreeScorePage
    {
        public List<FreeScore> Items = new List<FreeScore>();
        public bool HasNext;
        public int SkippedArchives;
        public string AppliedQuery;
    }

    public static class FreeScoreCatalog
    {
        public const string SourceUrl = "https://www.mutopiaproject.org/";
        public const string RepositoryUrl = "https://github.com/MutopiaProject/MutopiaProject";
        public const int PageSize = 10;
        public const int MaximumMidiBytes = 16 * 1024 * 1024;
        private const int MaximumIndexBytes = 2 * 1024 * 1024;
        private const string ListingUrl = SourceUrl + "cgibin/make-table.cgi";
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        private static readonly Regex Blocks = new Regex(
            "<table\\b[^>]*class\\s*=\\s*[\"'][^\"']*\\bresult-table\\b[^\"']*[\"'][^>]*>(.*?)</table>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex Cells = new Regex("<td\\b[^>]*>(.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex Links = new Regex("href\\s*=\\s*[\"']([^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex Spaces = new Regex("\\s+", RegexOptions.CultureInvariant, RegexTimeout);

        public static FreeScorePage Search(string query, int page)
        {
            query = NormalizeQuery(query);
            if (page < 1 || page > 1000)
                throw new ArgumentOutOfRangeException("page", "搜索页码无效。");
            string url = ListingUrl + "?searchingfor=" + Uri.EscapeDataString(query) +
                "&startat=" + ((page - 1) * PageSize).ToString(CultureInfo.InvariantCulture);
            string html = DecodeUtf8(ReadBytes(new Uri(url), MaximumIndexBytes));
            FreeScorePage result;
            try { result = ParsePage(html, page); }
            catch (RegexMatchTimeoutException error) { throw new InvalidDataException("曲库解析超时，请看网页。", error); }
            result.AppliedQuery = query;
            return result;
        }

        public static byte[] Download(FreeScore score)
        {
            return Download(score, CatalogDownloads.CacheDirectory, ReadBytes);
        }

        internal static byte[] Download(FreeScore score, string cacheDirectory, Func<Uri, int, byte[]> fetch)
        {
            if (score == null) throw new ArgumentNullException("score", "请先选择乐谱。");
            Uri midiUri;
            if (!TryOfficialUri(score.MidiUrl, out midiUri) ||
                !midiUri.AbsolutePath.StartsWith("/ftp/", StringComparison.Ordinal) ||
                !midiUri.AbsolutePath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
                midiUri.Query.Length != 0 || midiUri.Fragment.Length != 0)
                throw new ArgumentException("乐谱下载地址无效。", "score");
            if (String.IsNullOrWhiteSpace(score.License))
                throw new ArgumentException("乐谱缺少许可信息。", "score");
            if (fetch == null) throw new ArgumentNullException("fetch");

            // Keep the official maintainer credit when the information page
            // provides it. No account, tracking cookie, or audio is requested.
            Uri infoUri;
            if (TryOfficialUri(score.InfoUrl, out infoUri) &&
                infoUri.AbsolutePath == "/cgibin/piece-info.cgi")
            {
                try { ApplyAttribution(score, DecodeUtf8(fetch(infoUri, MaximumIndexBytes))); }
                catch (Exception error)
                {
                    // The listing already supplies composer, license, edition
                    // and the official information URL. A temporary failure of
                    // this supplemental page must not prevent a valid MIDI.
                    if (!CatalogDownloads.IsDownloadFailure(error) && !(error is RegexMatchTimeoutException)) throw;
                }
            }
            string key = CatalogDownloads.CacheKey("mutopia", midiUri.AbsoluteUri);
            byte[] cached = CatalogDownloads.ReadCache(cacheDirectory, key, ValidateMidi);
            if (cached != null) return cached;
            byte[] bytes;
            try { bytes = fetch(midiUri, MaximumMidiBytes); }
            catch (Exception error)
            {
                if (!CatalogDownloads.CanTryAlternative(error)) throw;
                // One retry is sufficient for an interrupted read or a short
                // server outage. Authorization and TLS failures are not retried.
                bytes = fetch(midiUri, MaximumMidiBytes);
            }
            ValidateMidi(bytes);
            CatalogDownloads.WriteCache(cacheDirectory, key, bytes);
            return bytes;
        }

        internal static void ApplyAttribution(FreeScore score, string info)
        {
            Match credit = Regex.Match(info, "Maintainer\\s*:(.*?)(?:</td>|</p>|</div>|<br\\s*/?>)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            string maintainer = credit.Success ? PlainText(credit.Groups[1].Value) : "";
            score.Attribution = "Mutopia Project；" + score.Composer +
                (maintainer.Length > 0 ? "；Maintainer: " + maintainer : "") + "；" + score.InfoUrl;
        }

        internal static FreeScorePage ParsePage(string html, int page)
        {
            if (String.IsNullOrWhiteSpace(html) || html.Length > MaximumIndexBytes)
                throw new InvalidDataException("曲库内容为空或过大。");
            FreeScorePage result = new FreeScorePage();
            MatchCollection blocks = Blocks.Matches(html);
            if (blocks.Count == 0)
            {
                if (html.IndexOf("no matches were found", StringComparison.OrdinalIgnoreCase) >= 0)
                    return result;
                throw new InvalidDataException("曲库页面格式已变化。");
            }
            int examined = 0;
            foreach (Match block in blocks)
            {
                if (++examined > PageSize) break;
                MatchCollection cells = Cells.Matches(block.Groups[1].Value);
                if (cells.Count < 12) throw new InvalidDataException("乐谱信息格式已变化。");
                string title = PlainText(cells[0].Groups[1].Value);
                string composer = PlainText(cells[1].Groups[1].Value);
                string instrument = PlainText(cells[4].Groups[1].Value);
                string license = PlainText(cells[9].Groups[1].Value);
                if (title.Length == 0 || license.Length == 0)
                    throw new InvalidDataException("乐谱名称或许可缺失。");
                string midiUrl = "", infoUrl = "", licenseUrl = "";
                bool archive = false;
                foreach (Match link in Links.Matches(block.Groups[1].Value))
                {
                    Uri uri;
                    if (!TryOfficialUri(link.Groups[1].Value, out uri)) continue;
                    if (uri.AbsolutePath.StartsWith("/ftp/", StringComparison.Ordinal) &&
                        uri.AbsolutePath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                        midiUrl = uri.AbsoluteUri;
                    if (uri.AbsolutePath.StartsWith("/ftp/", StringComparison.Ordinal) &&
                        uri.AbsolutePath.EndsWith("-mids.zip", StringComparison.OrdinalIgnoreCase))
                        archive = true;
                    if (uri.AbsolutePath == "/cgibin/piece-info.cgi") infoUrl = uri.AbsoluteUri;
                    if (uri.AbsolutePath == "/legal.html") licenseUrl = uri.AbsoluteUri;
                }
                if (midiUrl.Length == 0)
                {
                    if (archive) result.SkippedArchives++;
                    continue;
                }
                if (infoUrl.Length == 0) throw new InvalidDataException("乐谱来源信息缺失。");
                result.Items.Add(new FreeScore
                {
                    Title = title, Composer = composer, Instrument = instrument,
                    License = license, MidiUrl = midiUrl, InfoUrl = infoUrl,
                    SourceEdition = PlainText(cells[8].Groups[1].Value),
                    LicenseUrl = licenseUrl,
                    Attribution = "Mutopia Project；" + composer + "；" + infoUrl
                });
            }
            // Read the source pagination instead of the filtered MIDI count:
            // ZIP-only entries must not prevent the next page from opening.
            foreach (Match link in Links.Matches(html))
            {
                Uri uri;
                if (!TryOfficialUri(link.Groups[1].Value, out uri) ||
                    uri.AbsolutePath != "/cgibin/make-table.cgi") continue;
                Match offset = Regex.Match(uri.Query, "(?:[?&])startat=([0-9]+)(?:&|$)",
                    RegexOptions.CultureInvariant, RegexTimeout);
                int start;
                if (offset.Success && Int32.TryParse(offset.Groups[1].Value, out start) &&
                    start > (page - 1) * PageSize) result.HasNext = true;
            }
            return result;
        }

        internal static void ValidateMidi(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 14 || bytes.Length > MaximumMidiBytes ||
                bytes[0] != 'M' || bytes[1] != 'T' || bytes[2] != 'h' || bytes[3] != 'd')
                throw new InvalidDataException("下载内容不是有效 MIDI。");
            string path = Path.Combine(Path.GetTempPath(), "HarmonicaMidi-" + Guid.NewGuid().ToString("N") + ".mid");
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    stream.Write(bytes, 0, bytes.Length);
                SongScore loaded = ScoreIO.LoadMidi(path);
                bool hasNotes = false;
                foreach (ScoreTrack track in loaded.Tracks) if (track.Notes.Count > 0) hasNotes = true;
                if (!hasNotes) throw new InvalidDataException("乐谱没有可播放的音符。");
            }
            catch (FormatException error) { throw new InvalidDataException("乐谱格式暂不支持。", error); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static string OfficialSearchUrl(string query)
        {
            return ListingUrl + "?searchingfor=" + Uri.EscapeDataString(NormalizeQuery(query));
        }

        private static string NormalizeQuery(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length > 100) throw new ArgumentException("搜索词最多 100 个字符。", "query");
            foreach (char character in query)
                if (Char.IsControl(character)) throw new ArgumentException("搜索词含有无效字符。", "query");
            try { new UTF8Encoding(false, true).GetByteCount(query); }
            catch (EncoderFallbackException error) { throw new ArgumentException("搜索词含有无效字符。", "query", error); }
            switch (query)
            {
                case "欢乐颂": return "Ode to Joy";
                case "小星星": return "Twinkle";
                case "绿袖子": return "Greensleeves";
                case "卡农": return "Pachelbel";
                case "献给爱丽丝": return "Elise";
                case "小步舞曲": return "Minuet";
                case "贝多芬": return "Beethoven";
                case "莫扎特": return "Mozart";
                case "巴赫": return "Bach";
                case "圣诞": return "Christmas";
                case "铃儿响叮当": return "Jingle Bells";
                default: return query;
            }
        }

        internal static bool TryOfficialUri(string value, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 4096) return false;
            if (!Uri.TryCreate(new Uri(ListingUrl), WebUtility.HtmlDecode(value), out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "www.mutopiaproject.org" &&
                uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo);
        }

        private static byte[] ReadBytes(Uri uri, int limit)
        {
            return CatalogDownloads.ReadBytes(uri, limit, limit == MaximumMidiBytes ?
                "audio/midi, application/octet-stream;q=0.9" : "text/html");
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("曲库内容格式已变化。", error); }
        }

        private static string PlainText(string value)
        {
            string decoded = WebUtility.HtmlDecode(Tags.Replace(value ?? "", " "));
            StringBuilder text = new StringBuilder();
            foreach (char character in decoded)
                if (!Char.IsControl(character)) text.Append(character);
                else if (Char.IsWhiteSpace(character)) text.Append(' ');
            string result = Spaces.Replace(text.ToString(), " ").Trim();
            return result.Length > 2048 ? result.Substring(0, 2048) : result;
        }
    }
}
