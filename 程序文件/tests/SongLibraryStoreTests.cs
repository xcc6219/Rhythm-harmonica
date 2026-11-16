using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using HarmonicaStudio;

public static class SongLibraryStoreTests
{
    private static int checks;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static int Run()
    {
        checks = 0;
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(Path.Combine(parent, "HarmonicaStudio-library-store-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            TestInitialAndPreviousVersion(Path.Combine(root, "initial"));
            TestLargeLibrary(Path.Combine(root, "large"));
            TestInvalidRecords(Path.Combine(root, "invalid"));
            TestRecovery(Path.Combine(root, "recover"));
            TestFailedRecovery(Path.Combine(root, "failed-recover"));
            TestWriteLock(Path.Combine(root, "locked"));
            TestExternalChange(Path.Combine(root, "changed"));
            TestBoundaries(Path.Combine(root, "bounded"));
            Console.WriteLine("曲库索引原子保存与恢复测试通过，共 " + checks + " 项断言（隔离数据，不读取实际曲库）。");
            return checks;
        }
        finally
        {
            if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("HarmonicaStudio-library-store-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试清理范围无效。");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TestInitialAndPreviousVersion(string root)
    {
        var store = new SongLibraryStore(root);
        Throws<InvalidOperationException>(delegate { store.Save(new SongEntry[0]); }, "没有读取现有索引前不得保存。");
        Assert(store.Load().Count == 0 && store.CanWrite && store.Warning == "", "缺少索引和备份时可以初始化空库。");
        Assert(!Directory.Exists(root), "仅加载空库不创建多余文件夹。");
        SongEntry first = Entry("第一首"), second = Entry("第二首");
        first.Artist = "指定歌手"; first.Attribution = "编配作者"; first.SourceUrl = "https://example.org/score";
        store.Save(new[] { first, new SongEntry { Builtin = true, Title = "内置主题" } });
        byte[] previous = File.ReadAllBytes(Index(root));
        Assert(!File.Exists(Backup(root)), "首次建库没有伪造上一版备份。");
        File.WriteAllText(Path.Combine(root, "library.json.tmp"), "other operation", Utf8);
        store.Save(new[] { first, second });
        Assert(File.ReadAllBytes(Backup(root)).SequenceEqual(previous), "第二次保存精确保留上一版有效字节。");
        var loaded = new SongLibraryStore(root).Load();
        Assert(loaded.Count == 2 && loaded[0].Artist == first.Artist && loaded[0].Attribution == first.Attribution && loaded[0].SourceUrl == first.SourceUrl,
            "保存独立歌手、署名和来源字段，自动主题不重复持久化。");
        Assert(File.ReadAllText(Path.Combine(root, "library.json.tmp"), Utf8) == "other operation", "保存使用独立临时文件，不覆盖或清理其他操作的同名tmp。");
        Assert(Directory.GetFiles(root, "*.tmp").Length == 1, "成功提交不遗留本次临时文件。");
    }

    private static void TestLargeLibrary(string root)
    {
        var store = new SongLibraryStore(root); store.Load();
        var entries = new List<SongEntry>();
        for (int i = 0; i < 900; i++)
        { SongEntry entry = Entry("歌曲" + i); entry.Subtitle = new string('a', 2800); entries.Add(entry); }
        store.Save(entries);
        Assert(new FileInfo(Index(root)).Length > 2 * 1024 * 1024, "测试曲库确实超过旧序列化器默认2MB限制。");
        var reloaded = new SongLibraryStore(root);
        List<SongEntry> result = reloaded.Load();
        Assert(reloaded.CanWrite && result.Count == entries.Count && result[899].Subtitle == entries[899].Subtitle,
            "2MB以上的合法曲库可完整重启加载，不误当成空库或只读损坏。");
        reloaded.Save(result);
        Assert(new SongLibraryStore(root).Load().Count == entries.Count && NoTemporaryFiles(root), "大库再保存和备份不丢记录，不遗留tmp。");
    }

    private static void TestInvalidRecords(string root)
    {
        Directory.CreateDirectory(root);
        string valid = Json(new[] { Entry("保留条目") });
        string row = valid.Substring(1, valid.Length - 2);
        string[] invalid = { "[" + row + ",null]", "[" + row + ",{\"Title\":\"坏路径\",\"FileName\":\"../outside.mid\"}]",
            "[" + row + ",{\"Title\":\"没有乐谱\"}]", "[" + row + ",{\"Title\":\"坏音频路径\",\"FileName\":\"valid.mid\",\"AudioFileName\":\"../outside.wav\"}]",
            "null", "{}", "{ broken", "[" + row.TrimEnd('}') + ",\"FutureMetadata\":\"不可默默删除\"}]" };
        foreach (string text in invalid)
        {
            File.WriteAllText(Index(root), text, Utf8);
            byte[] original = File.ReadAllBytes(Index(root));
            var store = new SongLibraryStore(root); List<SongEntry> read = store.Load();
            Assert(!store.CanWrite && !String.IsNullOrWhiteSpace(store.Warning), "损坏、空根、空条目、路径穿越和未知扩展字段均提示只读。");
            if (text.StartsWith("[" + row + ",", StringComparison.Ordinal))
                Assert(read.Count == 1 && read[0].Title == "保留条目", "可读取有效条目供查看，但不静默过滤后写回。");
            Throws<InvalidOperationException>(delegate { store.Save(new[] { Entry("新歌曲") }); }, "损坏索引不能被后续导入覆盖。");
            Assert(File.ReadAllBytes(Index(root)).SequenceEqual(original) && !File.Exists(Backup(root)) && NoTemporaryFiles(root), "只读失败保留原始索引和恢复机会。");
        }
    }

    private static void TestRecovery(string root)
    {
        var store = new SongLibraryStore(root); store.Load(); store.Save(new[] { Entry("上一版") });
        byte[] good = File.ReadAllBytes(Index(root));
        store.Save(new[] { Entry("当前版") });
        byte[] corrupt = new byte[] { 0xef, 0xbb, 0xbf, (byte)'[', (byte)'{', 0xff };
        File.WriteAllBytes(Index(root), corrupt);
        var recovered = new SongLibraryStore(root); List<SongEntry> restored = recovered.Load();
        Assert(recovered.CanWrite && restored.Count == 1 && restored[0].Title == "上一版" && recovered.Warning.Contains("恢复"), "主索引损坏时使用有效上一版恢复，并明确显示状态。");
        Assert(File.ReadAllBytes(Index(root)).SequenceEqual(good) && File.ReadAllBytes(Backup(root)).SequenceEqual(good), "恢复只采用已验证备份的真实字节，不破坏备份。");
        string archive = Directory.GetFiles(Path.Combine(root, "恢复记录")).Single();
        Assert(File.ReadAllBytes(archive).SequenceEqual(corrupt), "损坏原始字节包括BOM和非法编码均原样保留后才恢复。");
        recovered.Save(new[] { Entry("恢复后导入") });
        Assert(new SongLibraryStore(root).Load()[0].Title == "恢复后导入" && NoTemporaryFiles(root), "成功恢复后允许正常导入，提交无临时残留。");
        File.Delete(Index(root));
        var missing = new SongLibraryStore(root);
        Assert(missing.Load()[0].Title == "上一版" && missing.CanWrite && missing.Warning.Contains("缺失"), "主索引缺失时也能恢复有效备份，不误初始化空库。");
    }

    private static void TestFailedRecovery(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Index(root), "broken-original", Utf8);
        File.WriteAllText(Backup(root), Json(new[] { Entry("可查看的备份") }), Utf8);
        File.WriteAllText(Path.Combine(root, "恢复记录"), "existing-user-file", Utf8);
        var store = new SongLibraryStore(root); List<SongEntry> restored = store.Load();
        Assert(!store.CanWrite && restored.Count == 1 && restored[0].Title == "可查看的备份" && store.Warning.Contains("只读"), "无法存档坏原件时只读展示备份，不强行恢复。");
        Assert(File.ReadAllText(Index(root), Utf8) == "broken-original" && File.ReadAllText(Path.Combine(root, "恢复记录"), Utf8) == "existing-user-file", "恢复失败不覆盖坏原件或已存在用户文件。");
        Throws<InvalidOperationException>(delegate { store.Save(new SongEntry[0]); }, "恢复失败后保持只读。");
        Assert(NoTemporaryFiles(root), "恢复失败不遗留临时文件。");
    }

    private static void TestWriteLock(string root)
    {
        var store = new SongLibraryStore(root); store.Load(); store.Save(new[] { Entry("锁定原曲") });
        byte[] original = File.ReadAllBytes(Index(root));
        using (var locked = new FileStream(Index(root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Throws<IOException>(delegate { store.Save(new[] { Entry("暂未保存") }); }, "无法原子替换被占用的索引时明确失败。");
            Assert(File.ReadAllBytes(Index(root)).SequenceEqual(original) && NoTemporaryFiles(root), "替换失败时原曲仍在且本次临时文件清理。");
            Assert(store.CanWrite, "短暂占用不能永久将本实例锁成只读。");
        }
        using (var locked = new FileStream(Index(root), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Throws<IOException>(delegate { store.EnsureWritable(); }, "读检查暂时被占用时允许上层提示重试。");
            Assert(store.CanWrite, "读锁释放后同一实例仍可重试。");
        }
        store.Save(new[] { Entry("占用结束后保存") });
        Assert(new SongLibraryStore(root).Load()[0].Title == "占用结束后保存" && File.ReadAllBytes(Backup(root)).SequenceEqual(original), "释放占用后同一实例保存成功且上一版仍可找回。");
        byte[] current = File.ReadAllBytes(Index(root));
        Throws<InvalidDataException>(delegate { store.Save(new SongEntry[] { null }); }, "保存入口也拒绝内存中的null条目。");
        Assert(File.ReadAllBytes(Index(root)).SequenceEqual(current) && NoTemporaryFiles(root), "无效保存保留原索引，不生成临时文件。");
    }

    private static void TestExternalChange(string root)
    {
        var store = new SongLibraryStore(root); store.Load(); store.Save(new[] { Entry("原曲") });
        File.WriteAllText(Index(root), Json(new[] { Entry("其他操作新曲") }), Utf8);
        byte[] changed = File.ReadAllBytes(Index(root));
        Throws<InvalidOperationException>(delegate { store.Save(new[] { Entry("过期快照") }); }, "加载后外部改变有效索引也不能被旧内存快照覆盖。");
        Assert(!store.CanWrite && File.ReadAllBytes(Index(root)).SequenceEqual(changed) && NoTemporaryFiles(root), "外部改变保留，提示重新载入。");
        Assert(store.Load()[0].Title == "其他操作新曲" && store.CanWrite, "明确重新读取后可以继续维护最新曲库。");
        File.WriteAllText(Index(root), "broken-after-load", Utf8);
        Throws<InvalidOperationException>(delegate { store.Save(new SongEntry[0]); }, "加载后损坏也不得在保存时被抹掉。");
        Assert(File.ReadAllText(Index(root), Utf8) == "broken-after-load", "保存前重新核对索引，保留新产生的损坏证据。");
    }

    private static void TestBoundaries(string root)
    {
        var store = new SongLibraryStore(root); store.Load(); store.Save(new[] { Entry("边界原曲") });
        byte[] original = File.ReadAllBytes(Index(root));
        SongEntry huge = Entry("超限条目"); huge.Subtitle = new string('x', SongLibraryStore.MaximumIndexBytes);
        Throws<InvalidDataException>(delegate { store.Save(new[] { huge }); }, "保存明确限制16MB，超过范围不开始替换。");
        Assert(File.ReadAllBytes(Index(root)).SequenceEqual(original) && NoTemporaryFiles(root), "超限写入不破坏现有曲库。");
        using (var stream = new FileStream(Index(root), FileMode.Create, FileAccess.Write)) stream.SetLength(SongLibraryStore.MaximumIndexBytes + 1L);
        var bounded = new SongLibraryStore(root);
        Assert(bounded.Load().Count == 0 && !bounded.CanWrite, "超过16MB的输入按只读处理，不无限制读取或覆盖。");
        Throws<InvalidOperationException>(delegate { bounded.Save(new SongEntry[0]); }, "超限原件不会被保存成空库。");
        Assert(new FileInfo(Index(root)).Length == SongLibraryStore.MaximumIndexBytes + 1L && NoTemporaryFiles(root), "超限原件完整保留。");
    }

    private static SongEntry Entry(string title) { return new SongEntry { Title = title, FileName = title + ".mid", Bpm = 120 }; }
    private static string Json(IEnumerable<SongEntry> entries) { return new JavaScriptSerializer().Serialize(entries); }
    private static string Index(string root) { return Path.Combine(root, "library.json"); }
    private static string Backup(string root) { return Path.Combine(root, "library.json.bak"); }
    private static bool NoTemporaryFiles(string root) { return Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0; }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
}
