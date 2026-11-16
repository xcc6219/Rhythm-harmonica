using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HarmonicaStudio
{
    public static class SongFileLibrary
    {
        public static string DirectoryFor(string dataDirectory)
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDirectory)), "曲目"); }

        public static string ResolveMidi(string dataDirectory, string filename)
        {
            if (!ValidName(filename)) throw new InvalidDataException("曲目文件名无效。");
            string current = Path.Combine(DirectoryFor(dataDirectory), filename);
            return File.Exists(current) ? current : Path.Combine(dataDirectory, "songs", filename);
        }

        public static string NewMidiFilename(string dataDirectory, string title, string audioPath)
        {
            if (!String.IsNullOrWhiteSpace(audioPath) && SongAudioDownload.IsSupported(audioPath) &&
                String.Equals(Path.GetDirectoryName(Path.GetFullPath(audioPath)), DirectoryFor(dataDirectory), StringComparison.OrdinalIgnoreCase))
            {
                string paired = Path.GetFileNameWithoutExtension(audioPath) + ".mid";
                if (!File.Exists(Path.Combine(DirectoryFor(dataDirectory), paired)) &&
                    !File.Exists(Path.Combine(dataDirectory, "songs", paired))) return paired;
            }
            return SafeTitle(title) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".mid";
        }

        public static string AttachAudio(string dataDirectory, string midiFilename, string audioPath)
        {
            if (!ValidName(midiFilename) || !SongAudioDownload.IsSupported(audioPath) || !File.Exists(audioPath))
                throw new InvalidDataException("无法保存歌曲的普通音频。");
            FileInfo audio = new FileInfo(audioPath);
            if (audio.Length < 16 || audio.Length > SongAudioDownload.MaximumDownloadBytes)
                throw new InvalidDataException("歌曲音频为空、不完整或超过 128 MB。");
            using (var input = File.OpenRead(audioPath))
            {
                byte[] header = new byte[32]; int count = input.Read(header, 0, header.Length); Array.Resize(ref header, count);
                if (!SongAudioDownload.ValidateAudioHeader(header)) throw new InvalidDataException("歌曲文件不是普通音频。");
            }
            string directory = DirectoryFor(dataDirectory);
            Directory.CreateDirectory(directory);
            string filename = Path.GetFileNameWithoutExtension(midiFilename) + Path.GetExtension(audioPath).ToLowerInvariant();
            string destination = Path.Combine(directory, filename);
            if (!String.Equals(Path.GetFullPath(audioPath), destination, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destination))
                { if (!SameFile(audioPath, destination)) throw new IOException("同名歌曲音频已存在，请保留原文件后重新导入。"); }
                else File.Copy(audioPath, destination, false);
            }
            return filename;
        }

        public static void Synchronize(string dataDirectory, IEnumerable<SongEntry> entries)
        {
            string directory = DirectoryFor(dataDirectory);
            Directory.CreateDirectory(directory);
            foreach (SongEntry entry in entries)
            {
                if (!String.IsNullOrEmpty(entry.FileName))
                {
                    if (!ValidName(entry.FileName)) continue;
                    string target = Path.Combine(directory, entry.FileName), old = Path.Combine(dataDirectory, "songs", entry.FileName);
                    if (!File.Exists(target) && File.Exists(old))
                    { ScoreIO.LoadMidi(old); File.Copy(old, target, false); }
                }
                else if (!String.IsNullOrWhiteSpace(entry.Jianpu))
                {
                    byte[] bytes = MidiWriter.ToBytes(ScoreIO.ParseJianpu(entry.Jianpu, entry.Bpm, 60).Notes, entry.Title);
                    string filename = GeneratedMidiName(entry, bytes);
                    string target = Path.Combine(directory, filename);
                    if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                }
            }
            string oldAudio = Path.Combine(dataDirectory, "audio-downloads");
            if (Directory.Exists(oldAudio))
                foreach (string source in Directory.EnumerateFiles(oldAudio).Take(1000))
                {
                    if (!SongAudioDownload.IsSupported(source)) continue;
                    string target = Path.Combine(directory, Path.GetFileName(source));
                    if (File.Exists(target)) continue;
                    using (var input = File.OpenRead(source))
                    {
                        if (input.Length < 16 || input.Length > SongAudioDownload.MaximumDownloadBytes) continue;
                        byte[] header = new byte[32]; int count = input.Read(header, 0, header.Length); Array.Resize(ref header, count);
                        if (!SongAudioDownload.ValidateAudioHeader(header)) continue;
                    }
                    File.Copy(source, target, false);
                }
        }

        internal static List<string> RemovalPaths(string dataDirectory, SongEntry song, IEnumerable<SongEntry> remaining)
        {
            string directory = Path.GetFullPath(DirectoryFor(dataDirectory));
            EnsureRegularPath(directory);
            var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SongEntry other in remaining) foreach (string name in EntryFiles(other)) shared.Add(name);
            var paths = new List<string>();
            foreach (string name in EntryFiles(song).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ValidName(name)) throw new InvalidDataException("曲目文件名无效，未删除文件。");
                if (shared.Contains(name)) continue;
                string path = Path.GetFullPath(Path.Combine(directory, name));
                if (!String.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("曲目路径超出软件文件夹，未删除文件。");
                EnsureRegularPath(path);
                if (File.Exists(path)) paths.Add(path);
            }
            return paths;
        }
        private static IEnumerable<string> EntryFiles(SongEntry entry)
        {
            if (!String.IsNullOrEmpty(entry.FileName)) yield return entry.FileName;
            else if (!String.IsNullOrWhiteSpace(entry.Jianpu))
            {
                byte[] bytes = MidiWriter.ToBytes(ScoreIO.ParseJianpu(entry.Jianpu, entry.Bpm, 60).Notes, entry.Title);
                yield return GeneratedMidiName(entry, bytes);
            }
            if (!String.IsNullOrEmpty(entry.AudioFileName)) yield return entry.AudioFileName;
        }
        private static string GeneratedMidiName(SongEntry entry, byte[] bytes)
        { return (entry.Builtin ? "内置-" : "简谱-") + SafeTitle(entry.Title) + "-" + Hash(bytes).Substring(0, 10) + ".mid"; }
        private static void EnsureRegularPath(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("曲目路径包含链接，未删除文件。");
        }

        private static bool ValidName(string name)
        { return !String.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0; }
        private static string SafeTitle(string title)
        {
            var text = new StringBuilder();
            foreach (char character in title ?? "")
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) < 0 && !Char.IsControl(character)) text.Append(character);
            string result = text.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length > 64) result = result.Substring(0, Char.IsHighSurrogate(result[63]) ? 63 : 64);
            return result.Length == 0 ? "未命名歌曲" : result;
        }
        private static string Hash(byte[] bytes)
        { using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", ""); }
        private static bool SameFile(string left, string right)
        {
            if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
            using (var algorithm = SHA256.Create())
            using (var first = File.OpenRead(left))
            using (var second = File.OpenRead(right))
                return algorithm.ComputeHash(first).SequenceEqual(algorithm.ComputeHash(second));
        }
    }
}
