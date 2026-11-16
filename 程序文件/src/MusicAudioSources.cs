using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HarmonicaStudio
{
    public static class MusicAudioSources
    {
        public const string QqProvider = "QQ音乐";
        public const string KugouProvider = "酷狗音乐";
        private static readonly Regex QqIdentifier = new Regex("\\A[A-Za-z0-9]{1,64}\\z", RegexOptions.CultureInvariant);

        public static List<OnlineSong> Search(string provider, string query, int page)
        {
            return Normalize(provider) == KugouProvider ? KugouCatalog.Search(query, page) : QqMusicCatalog.Search(query, page);
        }

        public static string ResolveAudioUrl(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            return DisplayName(song) == KugouProvider ? KugouCatalog.ResolveAudioUrl(song) : QqMusicCatalog.ResolveAudioUrl(song);
        }

        public static string OfficialSongUrl(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            if (DisplayName(song) == KugouProvider) return KugouCatalog.OfficialSongUrl(song);
            if (song.Id != null && QqIdentifier.IsMatch(song.Id)) return QqMusicCatalog.OfficialSongUrl(song.Id);
            return OfficialSearchUrl(QqProvider, SongQuery(song));
        }

        public static string OfficialSearchUrl(string provider, string query)
        {
            return Normalize(provider) == KugouProvider ? KugouCatalog.OfficialSearchUrl(query) : QqMusicCatalog.OfficialSearchUrl(query);
        }

        public static string OfficialDownloadUrl(string provider)
        {
            return Normalize(provider) == KugouProvider ? KugouCatalog.OfficialDownloadUrl : QqMusicCatalog.OfficialDownloadUrl;
        }

        public static string DisplayName(OnlineSong song)
        {
            if (song == null) throw new ArgumentNullException("song");
            return Normalize(song.Provider);
        }

        private static string SongQuery(OnlineSong song)
        {
            string query = ((song.Title ?? "") + " " + (song.Artist ?? "")).Trim();
            // Website fallbacks also cover incomplete external metadata.
            if (query.Length <= 100) return query;
            return query.Substring(0, Char.IsHighSurrogate(query[99]) ? 99 : 100);
        }

        private static string Normalize(string provider)
        {
            // Older persisted QQ selections have no Provider field.
            if (String.IsNullOrWhiteSpace(provider)) return QqProvider;
            provider = provider.Trim();
            if (provider == QqProvider || provider == KugouProvider) return provider;
            throw new ArgumentException("请选择QQ音乐或酷狗音乐。", "provider");
        }
    }
}
