using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace HarmonicaStudio
{
    // The index is user data. Invalid or unexpectedly changed content is never
    // rewritten as a filtered/empty library by a later import or deletion.
    internal sealed class SongLibraryStore
    {
        internal const int MaximumIndexBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> Fields = new HashSet<string>(
            typeof(SongEntry).GetProperties().Select(property => property.Name), StringComparer.Ordinal);
        private readonly string dataDirectory, indexPath, backupPath;
        private bool loaded;
        private byte[] expectedHash;
        internal bool CanWrite { get; private set; }
        internal string Warning { get; private set; }

        internal SongLibraryStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("曲库目录无效。", "directory");
            dataDirectory = Path.GetFullPath(directory);
            indexPath = Path.Combine(dataDirectory, "library.json");
            backupPath = Path.Combine(dataDirectory, "library.json.bak");
            Warning = "";
        }

        internal List<SongEntry> Load()
        {
            loaded = true; CanWrite = false; expectedHash = null; Warning = "";
            IndexRead current = ReadIndex(indexPath);
            if (current.Exists && current.Valid)
            { Accept(current.Bytes); return current.Items; }
            IndexRead backup = ReadIndex(backupPath);
            if (!current.Exists && !backup.Exists)
            { CanWrite = true; return new List<SongEntry>(); }
            if (backup.Exists && backup.Valid)
            {
                try
                {
                    // A read failure or oversized file cannot be archived in
                    // bounded memory; keep it untouched and expose read-only data.
                    if (current.Exists && current.Bytes == null)
                        throw new IOException("原索引无法完整读取，不能确认恢复副本。");
                    if (current.Exists)
                    {
                        string recovery = Path.Combine(dataDirectory, "恢复记录");
                        Directory.CreateDirectory(recovery);
                        AtomicWrite(Path.Combine(recovery, "library-corrupt-" + Guid.NewGuid().ToString("N") + ".json"), current.Bytes);
                    }
                    VerifyUnchanged(current.Exists ? Hash(current.Bytes) : null);
                    AtomicWrite(indexPath, backup.Bytes);
                    Accept(backup.Bytes);
                    Warning = current.Exists ? "曲库索引损坏，已从上一版有效备份恢复；损坏原件保存在 data/恢复记录。" :
                        "曲库索引缺失，已从上一版有效备份恢复。";
                    return backup.Items;
                }
                catch (Exception error)
                {
                    if (!ExpectedFailure(error)) throw;
                    Warning = "曲库索引无法安全恢复，当前只读显示有效备份；原索引未被覆盖。请检查 data/library.json 与 data/恢复记录。";
                    return backup.Items;
                }
            }
            Warning = "曲库索引无法完整读取，且没有可用备份；已禁止写入，原索引保留在 data/library.json。";
            return current.Items;
        }

        internal void EnsureWritable()
        {
            if (!loaded) throw new InvalidOperationException("请先读取曲库索引，再保存曲目。");
            if (!CanWrite) throw new InvalidOperationException(Warning);
            try { VerifyUnchanged(expectedHash); }
            catch (Exception error)
            {
                if (!ExpectedFailure(error)) throw;
                if (!(error is IndexChangedException) && !(error is InvalidDataException))
                    throw new IOException("曲库索引暂时无法读取，原索引未覆盖。请等待文件占用结束后重试。", error);
                CanWrite = false;
                Warning = "曲库索引已被其他操作更改或暂时无法读取，已禁止覆盖。请重新打开软件确认曲库。";
                throw new InvalidOperationException(Warning, error);
            }
        }

        internal void Save(IEnumerable<SongEntry> entries)
        {
            EnsureWritable();
            if (entries == null) throw new ArgumentNullException("entries");
            var saved = new List<SongEntry>();
            foreach (SongEntry entry in entries)
            {
                if (entry == null) throw new InvalidDataException("曲库包含空记录，未保存或覆盖原索引。");
                if (entry.Builtin) continue;
                if (!ValidEntry(entry)) throw new InvalidDataException("曲库包含无效曲目，未保存或覆盖原索引。");
                saved.Add(entry);
            }
            byte[] bytes;
            try { bytes = Utf8.GetBytes(Serializer().Serialize(saved)); }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                throw new InvalidDataException("曲库数据无法保存，原索引保留。", error);
            }
            if (bytes.Length > MaximumIndexBytes) throw new InvalidDataException("曲库索引超过 16 MB，未保存或覆盖原索引。");
            EnsureWritable();
            // Commit a verified previous version before replacing the current
            // index. A failed current write still leaves a valid recovery copy.
            if (expectedHash != null)
            {
                byte[] previous = ReadBytes(indexPath);
                if (!Hash(previous).SequenceEqual(expectedHash))
                { EnsureWritable(); throw new IOException("曲库索引已更改，未覆盖。"); }
                AtomicWrite(backupPath, previous);
            }
            EnsureWritable();
            AtomicWrite(indexPath, bytes);
            Accept(bytes);
        }

        private void Accept(byte[] bytes)
        { expectedHash = Hash(bytes); CanWrite = true; }

        private void VerifyUnchanged(byte[] expected)
        {
            if (Directory.Exists(indexPath)) throw new IndexChangedException("曲库索引路径被目录占用。");
            bool exists = File.Exists(indexPath);
            if (expected == null)
            { if (exists) throw new IndexChangedException("曲库索引已由其他操作创建。"); return; }
            if (!exists || !Hash(ReadBytes(indexPath)).SequenceEqual(expected))
                throw new IndexChangedException("曲库索引已被其他操作更改。");
        }

        private sealed class IndexChangedException : IOException
        { internal IndexChangedException(string message) : base(message) { } }

        private sealed class IndexRead
        {
            internal bool Exists, Valid;
            internal byte[] Bytes;
            internal List<SongEntry> Items = new List<SongEntry>();
        }

        private static IndexRead ReadIndex(string path)
        {
            var result = new IndexRead { Exists = File.Exists(path) || Directory.Exists(path) };
            if (!result.Exists) return result;
            try
            {
                result.Bytes = ReadBytes(path);
                var serializer = Serializer();
                object decoded = serializer.DeserializeObject(Utf8.GetString(result.Bytes).TrimStart('\ufeff'));
                IEnumerable rows = decoded as IEnumerable;
                if (rows == null || decoded is string || decoded is IDictionary) return result;
                bool valid = true;
                foreach (object row in rows)
                {
                    var values = row as Dictionary<string, object>;
                    if (values == null) { valid = false; continue; }
                    if (values.Keys.Any(key => !Fields.Contains(key))) valid = false;
                    try
                    {
                        SongEntry entry = serializer.ConvertToType<SongEntry>(values);
                        if (ValidEntry(entry)) result.Items.Add(entry); else valid = false;
                    }
                    catch (Exception error)
                    {
                        if (!(error is ArgumentException) && !(error is InvalidOperationException) && !(error is FormatException) && !(error is OverflowException)) throw;
                        valid = false;
                    }
                }
                result.Valid = valid;
            }
            catch (Exception error) { if (!ExpectedFailure(error)) throw; }
            return result;
        }

        private static bool ValidEntry(SongEntry entry)
        {
            if (entry == null || String.IsNullOrWhiteSpace(entry.Title) || Double.IsNaN(entry.Bpm) || Double.IsInfinity(entry.Bpm)) return false;
            if (!String.IsNullOrEmpty(entry.FileName))
            {
                if (!ValidFilename(entry.FileName) || !(entry.FileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
                    entry.FileName.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))) return false;
            }
            else if (String.IsNullOrWhiteSpace(entry.Jianpu) || entry.Bpm <= 0) return false;
            return String.IsNullOrEmpty(entry.AudioFileName) || ValidFilename(entry.AudioFileName) && SongAudioDownload.IsSupported(entry.AudioFileName);
        }

        private static bool ValidFilename(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name == Path.GetFileName(name) &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith(".", StringComparison.Ordinal) && !name.EndsWith(" ", StringComparison.Ordinal);
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumIndexBytes, RecursionLimit = 32 }; }

        private static byte[] ReadBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new MemoryStream())
            {
                if (stream.Length > MaximumIndexBytes) throw new InvalidDataException("曲库索引超过 16 MB。");
                byte[] buffer = new byte[32768]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + read > MaximumIndexBytes) throw new InvalidDataException("曲库索引超过 16 MB。");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static byte[] Hash(byte[] bytes)
        { using (var algorithm = SHA256.Create()) return algorithm.ComputeHash(bytes); }

        private static bool ExpectedFailure(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is InvalidOperationException || error is FormatException || error is OverflowException ||
                error is System.Security.SecurityException;
        }
    }
}
