using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HarmonicaStudio
{
    internal sealed class QqMusicCookie
    {
        public string Name, Value, Domain;
        public DateTime ExpiresUtc;
        public bool IsSession;
    }

    // Only the music session created in this application's WebView profile is accepted.
    // Passwords, browser-wide QQ cookies and refresh tokens are never collected.
    internal sealed class QqAccountSession
    {
        private readonly Dictionary<string, string> cookies;
        internal readonly string Uin, MusicKey;
        internal readonly DateTime ExpiresUtc;
        internal string Nickname { get; private set; }
        internal bool Verified { get; private set; }
        internal string ValidationFailureKind { get; private set; }
        internal readonly string LoginType;
        internal bool IsUsable { get { return Verified && DateTime.UtcNow < ExpiresUtc; } }
        private static readonly HashSet<string> CookieNames = new HashSet<string>(StringComparer.Ordinal)
        { "uin", "wxuin", "qqmusic_uin", "login_type", "tmeLoginType", "qm_keyst", "qqmusic_key", "wxskey", "qqmusic_fromtag" };

        private QqAccountSession(Dictionary<string, string> values, string uin, string key, DateTime expires)
        { cookies = values; Uin = uin; MusicKey = key; ExpiresUtc = expires; LoginType = Get(values, "tmeLoginType"); ValidationFailureKind = "Unknown"; }

        internal static QqAccountSession FromBrowserCookies(IEnumerable<QqMusicCookie> browserCookies)
        {
            if (browserCookies == null) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            DateTime now = DateTime.UtcNow, expires = now.AddHours(12);
            foreach (QqMusicCookie cookie in browserCookies)
            {
                if (cookie == null || !CookieNames.Contains(cookie.Name ?? "") || !IsMusicCookieDomain(cookie.Domain) ||
                    String.IsNullOrEmpty(cookie.Value) || cookie.Value.Length > 8192 ||
                    Regex.IsMatch(cookie.Value, @"[\x00-\x20\x7f;,]")) continue;
                DateTime expiry = cookie.ExpiresUtc.Kind == DateTimeKind.Utc ? cookie.ExpiresUtc : cookie.ExpiresUtc.ToUniversalTime();
                if (!cookie.IsSession && expiry <= now) continue;
                int priority = (cookie.Domain ?? "").TrimStart('.').EndsWith("y.qq.com", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                int oldPriority;
                if (priorities.TryGetValue(cookie.Name, out oldPriority) && oldPriority > priority) continue;
                values[cookie.Name] = cookie.Value; priorities[cookie.Name] = priority;
                if (!cookie.IsSession && expiry < expires) expires = expiry;
            }
            string loginType = Get(values, "login_type");
            // The current official website uses tmeLoginType=1 for WeChat;
            // legacy pages use login_type=2. A 19-digit wxuin stays a string.
            bool wechat = loginType == "2" || Get(values, "tmeLoginType") == "1";
            string uin = NormalizeUin(wechat ? Get(values, "wxuin") : Get(values, "uin"));
            if (uin.Length == 0) uin = NormalizeUin(Get(values, "wxuin"));
            if (uin.Length == 0) uin = NormalizeUin(Get(values, "qqmusic_uin"));
            string key = Get(values, "qqmusic_key");
            if (key.Length == 0) key = Get(values, "qm_keyst");
            if (key.Length == 0 && (wechat || Get(values, "wxuin").Length != 0)) key = Get(values, "wxskey");
            return uin.Length == 0 || key.Length == 0 ? null : new QqAccountSession(values, uin, key, expires);
        }

        internal static string NormalizeUin(string value)
        {
            if (String.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\Ao?[0-9]{1,20}\z")) return "";
            string result = value.TrimStart('o').TrimStart('0');
            return result.Length == 0 ? "" : result;
        }
        private static bool IsMusicCookieDomain(string domain)
        {
            string host = (domain ?? "").TrimStart('.');
            return String.Equals(host, "qq.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "y.qq.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".y.qq.com", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsAccountHost(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "u.y.qq.com" || uri.Host == "c.y.qq.com" || uri.Host == "c6.y.qq.com");
        }
        internal CookieContainer CookiesFor(Uri uri)
        {
            if (!IsAccountHost(uri)) throw new InvalidOperationException("账号会话只能用于 QQ 音乐官方接口。");
            var container = new CookieContainer();
            foreach (var pair in cookies) container.Add(uri, new Cookie(pair.Key, pair.Value, "/") { Secure = true, HttpOnly = true });
            return container;
        }
        internal uint Gtk
        {
            get { uint hash = 5381; foreach (char c in MusicKey) hash = unchecked(hash + (hash << 5) + c); return hash & 0x7fffffff; }
        }
        internal bool AcceptIdentity(string returnedUin, string nickname)
        {
            if (DateTime.UtcNow >= ExpiresUtc) { MarkValidationFailure("Expired"); return false; }
            if (NormalizeUin(returnedUin) != Uin) { MarkValidationFailure("Identity"); return false; }
            Verified = true;
            ValidationFailureKind = "";
            Nickname = String.IsNullOrWhiteSpace(nickname) ? "QQ 音乐账号" : Regex.Replace(nickname, @"[\x00-\x1f\x7f]", " ").Trim();
            if (Nickname.Length > 32) Nickname = Nickname.Substring(0, 32);
            return true;
        }
        internal bool AcceptVerifiedCurrentAccount(string nickname)
        {
            // Caller must prove the official current-account response requires
            // this candidate's own authority. Modern responses redact numeric IDs.
            return AcceptIdentity(Uin, nickname);
        }
        internal void Invalidate() { MarkValidationFailure("Unknown"); }
        internal void MarkValidationFailure(string kind)
        {
            Verified = false;
            ValidationFailureKind = kind == "Expired" || kind == "Network" || kind == "Format" || kind == "Identity" || kind == "Permission" ? kind : "Unknown";
        }
        internal Dictionary<string, object> ToStorage()
        {
            return new Dictionary<string, object> { { "version", 1 }, { "cookies", new Dictionary<string, string>(cookies, StringComparer.Ordinal) },
                { "uin", Uin }, { "nickname", Nickname }, { "expires", ExpiresUtc.Ticks } };
        }
        internal static QqAccountSession FromStorage(Dictionary<string, object> value)
        {
            if (value == null || Convert.ToString(value["version"], CultureInfo.InvariantCulture) != "1") return null;
            var stored = value["cookies"] as Dictionary<string, object>;
            long ticks;
            if (stored == null || !Int64.TryParse(Convert.ToString(value["expires"], CultureInfo.InvariantCulture), out ticks) ||
                ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.UtcNow.AddHours(13).Ticks) return null;
            var records = new List<QqMusicCookie>();
            foreach (var pair in stored) records.Add(new QqMusicCookie { Name = pair.Key, Value = Convert.ToString(pair.Value),
                Domain = "y.qq.com", ExpiresUtc = new DateTime(ticks, DateTimeKind.Utc) });
            QqAccountSession session = FromBrowserCookies(records);
            // A persisted candidate must be revalidated against the official current-account API.
            if (session == null || session.Uin != NormalizeUin(Convert.ToString(value["uin"]))) return null;
            session.Nickname = Convert.ToString(value["nickname"]);
            return session;
        }
        private static string Get(Dictionary<string, string> values, string name)
        { string value; return values.TryGetValue(name, out value) ? value : ""; }
    }

    internal static class QqAccountStore
    {
        private static readonly object Gate = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HarmonicaStudio.QqMusic.Session.v1");
        private static QqAccountSession current;
        private static bool loaded;
        internal static string DataDirectory { get { return Path.Combine(AppPaths.ContentDirectory, "data"); } }
        internal static string ProfileDirectory { get { return Path.Combine(DataDirectory, "qq-webview"); } }
        internal static string LogoutMarker { get { return Path.Combine(DataDirectory, "qq-signout.pending"); } }
        internal static string SessionPath { get { return Path.Combine(DataDirectory, "qq-session.dat"); } }
        internal static QqAccountSession Current
        {
            get { lock (Gate) { if (!loaded) { current = File.Exists(LogoutMarker) ? null : Read(SessionPath); loaded = true; } return current; } }
        }
        internal static void Set(QqAccountSession session)
        {
            if (session == null || !session.IsUsable) throw new InvalidOperationException("QQ 音乐尚未确认登录账号。");
            if (File.Exists(LogoutMarker)) throw new InvalidOperationException("退出登录记录尚未清理，请先重新打开官方登录窗口。");
            Write(SessionPath, session);
            lock (Gate) { current = session; loaded = true; }
        }
        internal static void Forget()
        {
            lock (Gate) { current = null; loaded = true; }
            Directory.CreateDirectory(DataDirectory);
            // The dedicated profile is cleared before it can be used again, without deleting a live browser directory.
            File.WriteAllText(LogoutMarker, "clear", new UTF8Encoding(false));
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
        }
        internal static void Write(string filename, QqAccountSession session)
        {
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(session.ToStorage()));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            string temporary = filename + ".new";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(filename)) File.Replace(temporary, filename, null); else File.Move(temporary, filename);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static QqAccountSession Read(string filename)
        {
            try
            {
                if (!File.Exists(filename) || new FileInfo(filename).Length > 65536) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(filename), Entropy, DataProtectionScope.CurrentUser);
                try { return QqAccountSession.FromStorage(new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(plain)) as Dictionary<string, object>); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (Exception error)
            {
                if (error is IOException || error is UnauthorizedAccessException || error is CryptographicException ||
                    error is ArgumentException || error is InvalidOperationException || error is KeyNotFoundException || error is FormatException) return null;
                throw;
            }
        }
    }
}
