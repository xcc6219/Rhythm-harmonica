using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarmonicaStudio
{
    internal sealed class AudioDownloadResponse : IDisposable
    {
        public Stream Body;
        public int StatusCode;
        public long ContentLength = -1;
        public string ContentType, Location;
        public Action Close;
        public void Dispose()
        {
            try { if (Body != null) Body.Dispose(); }
            finally { if (Close != null) { Action close = Close; Close = null; close(); } }
        }
    }

    public static class SongAudioDownload
    {
        public const int MaximumDownloadBytes = 128 * 1024 * 1024;
        public const int TimeoutMilliseconds = 120000;
        public const int MaximumScannedFiles = 1000;
        public const int MaximumCandidates = 20;

        public static Task<string> DownloadAsync(string url, string title, Action<string> progress, CancellationToken token)
        {
            return Task.Run(delegate
            {
                return Download(url, title, SongFileLibrary.DirectoryFor(Path.Combine(AppPaths.ContentDirectory, "data")), progress,
                    token, OpenResponse, MaximumDownloadBytes, TimeoutMilliseconds);
            }, token);
        }

        internal static string Download(string url, string title, string downloadRoot, Action<string> progress,
            CancellationToken token, Func<Uri, int, CancellationToken, AudioDownloadResponse> open,
            int maximumBytes, int timeoutMilliseconds)
        {
            Uri initial;
            if (!TryPublicHttpsUri(url, out initial)) throw new ArgumentException("请输入普通音频的公开 HTTPS 下载地址。", "url");
            if (open == null) throw new ArgumentNullException("open");
            if (maximumBytes < 16 || maximumBytes > MaximumDownloadBytes || timeoutMilliseconds <= 0 || timeoutMilliseconds > TimeoutMilliseconds)
                throw new ArgumentOutOfRangeException("maximumBytes");
            token.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(downloadRoot);
            Directory.CreateDirectory(root);
            string stem = SafeFilename(title) + "-" + Guid.NewGuid().ToString("N");
            string partial = Path.Combine(root, stem + ".partial");
            string completed = null;
            Stopwatch clock = Stopwatch.StartNew();
            bool qqAudio = QqMusicCatalog.IsOrdinaryAudioUri(initial);
            bool kugouAudio = KugouCatalog.IsOrdinaryAudioUri(initial);
            Uri current = initial;
            try
            {
                for (int redirects = 0; redirects <= 5; redirects++)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = Remaining(clock, timeoutMilliseconds);
                    using (AudioDownloadResponse response = open(current, remaining, token))
                    {
                        if (response == null) throw new InvalidDataException("音频下载未返回数据，请重试或在官方客户端下载。");
                        if (response.StatusCode == 301 || response.StatusCode == 302 || response.StatusCode == 303 || response.StatusCode == 307 || response.StatusCode == 308)
                        {
                            Uri redirected;
                            if (redirects == 5 || String.IsNullOrEmpty(response.Location) || !Uri.TryCreate(current, response.Location, out redirected) ||
                                !TryPublicHttpsUri(redirected.AbsoluteUri, out redirected) || (qqAudio && !QqMusicCatalog.IsOrdinaryAudioUri(redirected)) ||
                                (kugouAudio && !KugouCatalog.IsOrdinaryAudioUri(redirected)))
                                throw new InvalidDataException("音频下载发生了无法验证的跳转，请改用官方客户端下载。");
                            current = redirected;
                            continue;
                        }
                        if (response.StatusCode != 200) throw new InvalidOperationException("音频下载失败（HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + "），请检查歌曲下载权限。");
                        if (response.ContentLength > maximumBytes) throw new InvalidDataException("音频超过 128 MB，请截取需要的片段或选择较低音质。");
                        if (response.Body == null) throw new InvalidDataException("音频下载内容为空。");
                        string contentType = (response.ContentType ?? "").Split(';')[0].Trim();
                        if (contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                            contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("下载地址返回了网页或提示信息，并非普通音频，请在官方客户端下载后导入。");
                        if (progress != null) progress("正在下载歌曲音频…");
                        long received = 0;
                        byte[] chunk = new byte[32768];
                        byte[] prefix = new byte[32];
                        int prefixCount = 0;
                        string extension = null;
                        Stopwatch progressClock = Stopwatch.StartNew();
                        using (FileStream output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            for (;;)
                            {
                                token.ThrowIfCancellationRequested();
                                remaining = Remaining(clock, timeoutMilliseconds);
                                if (response.Body.CanTimeout) response.Body.ReadTimeout = remaining;
                                int count = response.Body.Read(chunk, 0, chunk.Length);
                                token.ThrowIfCancellationRequested();
                                Remaining(clock, timeoutMilliseconds);
                                if (count == 0) break;
                                if (received + count > maximumBytes) throw new InvalidDataException("音频超过 128 MB，下载已停止。");
                                if (prefixCount < prefix.Length)
                                {
                                    int copy = Math.Min(count, prefix.Length - prefixCount);
                                    Array.Copy(chunk, 0, prefix, prefixCount, copy); prefixCount += copy;
                                    if (prefixCount >= 16 && extension == null)
                                    {
                                        extension = AudioExtension(prefix);
                                        if (extension == null) throw new InvalidDataException("下载内容不是支持的普通音频；网页、JSON及受保护音乐文件无法转录。");
                                    }
                                }
                                output.Write(chunk, 0, count); received += count;
                                if (progress != null && progressClock.ElapsedMilliseconds >= 400)
                                {
                                    progress("正在下载歌曲音频 · " + (received / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
                                    progressClock.Restart();
                                }
                            }
                            output.Flush();
                        }
                        if (received < 16 || (response.ContentLength >= 0 && received != response.ContentLength))
                            throw new InvalidDataException("歌曲音频下载不完整，请重试。");
                        if (extension == null) extension = AudioExtension(prefix);
                        if (extension == null || !HasAudioHeader(partial)) throw new InvalidDataException("下载文件不是可用的普通音频。");
                        token.ThrowIfCancellationRequested(); Remaining(clock, timeoutMilliseconds);
                        completed = Path.Combine(root, stem + extension);
                        File.Move(partial, completed);
                        if (progress != null) progress("歌曲音频下载完成，正在准备转成 MIDI…");
                        return completed;
                    }
                }
                throw new InvalidDataException("音频下载跳转过多，请改用官方客户端下载。");
            }
            catch (WebException error)
            {
                token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= timeoutMilliseconds || error.Status == WebExceptionStatus.Timeout)
                    throw new TimeoutException("音频下载超过 120 秒，请重试或在官方客户端下载。", error);
                throw new InvalidOperationException("无法下载歌曲音频，请检查网络或在官方客户端下载。", error);
            }
            catch (IOException)
            {
                token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= timeoutMilliseconds) throw new TimeoutException("音频下载超过 120 秒，请重试或在官方客户端下载。");
                throw;
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        private static AudioDownloadResponse OpenResponse(Uri uri, int timeout, CancellationToken token)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET"; request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = timeout;
            request.UserAgent = "RhythmHarmonica (.NET Framework)";
            request.Accept = "audio/mpeg, audio/wav, audio/flac, audio/ogg, application/octet-stream;q=0.8";
            if (QqMusicCatalog.IsOrdinaryAudioUri(uri)) request.Referer = "https://y.qq.com/";
            else if (KugouCatalog.IsOrdinaryAudioUri(uri)) request.Referer = "https://www.kugou.com/";
            CancellationTokenRegistration registration = token.Register(request.Abort);
            try
            {
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException error)
                {
                    response = error.Response as HttpWebResponse;
                    if (response == null) throw;
                }
                return new AudioDownloadResponse
                {
                    Body = response.GetResponseStream(), StatusCode = (int)response.StatusCode,
                    ContentLength = response.ContentLength, ContentType = response.ContentType, Location = response.Headers["Location"],
                    Close = delegate { try { response.Close(); } finally { registration.Dispose(); request.Abort(); } }
                };
            }
            catch { registration.Dispose(); request.Abort(); throw; }
        }

        private static int Remaining(Stopwatch clock, int limit)
        {
            int remaining = limit - (int)Math.Min(Int32.MaxValue, clock.ElapsedMilliseconds);
            if (remaining <= 0) throw new TimeoutException("音频下载超过 120 秒，请重试或在官方客户端下载。");
            return remaining;
        }

        internal static bool TryPublicHttpsUri(string url, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(url) || url.Length > 8192 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback)
                return false;
            string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension == ".qmc0" || extension == ".qmc3" || extension == ".qmcflac" || extension == ".qmcogg" ||
                extension == ".mflac" || extension == ".mgg" || extension == ".ncm" || extension == ".kgm" || extension == ".kgma") return false;
            IPAddress address;
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out address))
            {
                byte[] bytes = address.GetAddressBytes();
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                        (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168)) return false;
                }
                else if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || bytes[0] == 0xfc || bytes[0] == 0xfd || address.IsIPv4MappedToIPv6)
                    return false;
            }
            return !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSupported(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string extension;
            try { extension = Path.GetExtension(path); }
            catch (ArgumentException) { return false; }
            return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ValidateAudioHeader(byte[] bytes) { return AudioExtension(bytes) != null; }
        private static string AudioExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return null;
            if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E') return ".wav";
            if (bytes[0] == 'f' && bytes[1] == 'L' && bytes[2] == 'a' && bytes[3] == 'C') return ".flac";
            if (bytes.Length >= 5 && bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S' && bytes[4] == 0) return ".ogg";
            if (bytes.Length >= 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3' && bytes[3] >= 2 && bytes[3] <= 4 &&
                bytes[6] < 128 && bytes[7] < 128 && bytes[8] < 128 && bytes[9] < 128) return ".mp3";
            if (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 0x18) != 0x08 && (bytes[1] & 0x06) != 0 &&
                (bytes[2] & 0xf0) != 0 && (bytes[2] & 0xf0) != 0xf0 && (bytes[2] & 0x0c) != 0x0c) return ".mp3";
            return null;
        }

        private static bool HasAudioHeader(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 16 || stream.Length > MaximumDownloadBytes) return false;
                    byte[] header = new byte[64]; int count = stream.Read(header, 0, header.Length);
                    if (count < 16) return false;
                    return ValidateAudioHeader(header);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static List<string> FindMatchingFiles(string folder, OnlineSong song)
        {
            List<KeyValuePair<string, DateTime>> found = new List<KeyValuePair<string, DateTime>>();
            if (song == null || String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new List<string>();
            string title = NormalizeMatchText(song.Title), artist = NormalizeMatchText(song.Artist);
            if (title.Length == 0) return new List<string>();
            int scanned = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (++scanned > MaximumScannedFiles) break;
                    if (!IsSupported(file) || !MatchesFilename(Path.GetFileNameWithoutExtension(file), title, artist)) continue;
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !HasAudioHeader(file)) continue;
                        found.Add(new KeyValuePair<string, DateTime>(Path.GetFullPath(file), File.GetLastWriteTimeUtc(file)));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            found.Sort(delegate(KeyValuePair<string, DateTime> a, KeyValuePair<string, DateTime> b)
            { int date = b.Value.CompareTo(a.Value); return date != 0 ? date : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key); });
            List<string> candidates = new List<string>();
            foreach (KeyValuePair<string, DateTime> item in found) { candidates.Add(item.Key); if (candidates.Count >= MaximumCandidates) break; }
            return candidates;
        }

        internal static bool MatchesFilename(string filename, string normalizedTitle, string normalizedArtist)
        {
            if (normalizedArtist.Length == 0) return NormalizeMatchText(filename) == normalizedTitle;
            for (int i = 1; i < filename.Length - 1; i++)
            {
                char separator = filename[i];
                if (separator != '-' && separator != '–' && separator != '—' && separator != '_') continue;
                string left = NormalizeMatchText(filename.Substring(0, i)), right = NormalizeMatchText(filename.Substring(i + 1));
                if ((left == normalizedArtist && right == normalizedTitle) || (left == normalizedTitle && right == normalizedArtist)) return true;
            }
            return false;
        }

        internal static string NormalizeMatchText(string text)
        {
            StringBuilder normalized = new StringBuilder();
            foreach (char character in (text ?? "").Normalize(NormalizationForm.FormKC))
                if (Char.IsLetterOrDigit(character)) normalized.Append(Char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        private static string SafeFilename(string title)
        {
            StringBuilder filename = new StringBuilder();
            foreach (char character in title ?? "歌曲音频")
            {
                if (!Char.IsControl(character) && Array.IndexOf(Path.GetInvalidFileNameChars(), character) < 0) filename.Append(character);
                if (filename.Length >= 80) break;
            }
            string safe = filename.ToString().Trim().TrimEnd('.', ' ');
            return safe.Length == 0 ? "歌曲音频" : safe;
        }
    }
}
