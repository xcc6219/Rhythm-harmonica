using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonicaStudio;

public static class SongFileLibraryTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        string workspace = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "song-files-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(workspace, "data"), directory = SongFileLibrary.DirectoryFor(data);
        Directory.CreateDirectory(Path.Combine(data, "songs")); Directory.CreateDirectory(Path.Combine(data, "audio-downloads"));
        byte[] midi = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 64, Start = 0, Duration = 1, Velocity = 90 } }, "旧曲库");
        byte[] wave = new byte[48]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0); Encoding.ASCII.GetBytes("WAVE").CopyTo(wave, 8);
        string oldMidi = Path.Combine(data, "songs", "old.mid"), oldAudio = Path.Combine(data, "audio-downloads", "旧歌曲.wav");
        File.WriteAllBytes(oldMidi, midi); File.WriteAllBytes(oldAudio, wave);
        File.WriteAllBytes(Path.Combine(data, "audio-downloads", "受保护.kgm"), wave);
        File.WriteAllText(Path.Combine(data, "audio-downloads", "错误网页.mp3"), "<html>登录后下载</html>");
        string index = Path.Combine(data, "library.json"), originalIndex = "[{\"Title\":\"旧曲库\",\"FileName\":\"old.mid\"}]";
        File.WriteAllText(index, originalIndex, new UTF8Encoding(false));
        var entries = new List<SongEntry> { new SongEntry { Title = "旧曲库", FileName = "old.mid" },
            new SongEntry { Title = "已有文字谱", Bpm = 90, Jianpu = "1 2 3", Builtin = false },
            new SongEntry { Title = "内置测试主题", Bpm = 120, Jianpu = "1 3 5", Builtin = true } };
        try
        {
            SongFileLibrary.Synchronize(data, entries);
            Assert(directory == Path.Combine(workspace, "曲目"), "曲目文件夹与data同属于程序文件根目录。");
            Assert(File.ReadAllBytes(Path.Combine(directory, "old.mid")).SequenceEqual(midi), "旧MIDI原样复制到统一目录。");
            Assert(File.ReadAllBytes(oldMidi).SequenceEqual(midi) && File.ReadAllBytes(oldAudio).SequenceEqual(wave), "整理不删除或改动旧曲目和音频。");
            Assert(File.ReadAllText(index, Encoding.UTF8) == originalIndex && entries[0].FileName == "old.mid", "旧索引无需改名即可兼容新目录。");
            Assert(SongFileLibrary.ResolveMidi(data, "old.mid") == Path.Combine(directory, "old.mid"), "已有曲库优先读取整理后的文件。");
            Assert(File.Exists(Path.Combine(directory, "旧歌曲.wav")), "已有普通下载音频也复制进统一曲目目录。");
            Assert(!File.Exists(Path.Combine(directory, "受保护.kgm")) && !File.Exists(Path.Combine(directory, "错误网页.mp3")), "受保护或假音频不会伪装为可转录曲目。");
            string builtin = Directory.GetFiles(directory, "内置-*.mid").Single(), notation = Directory.GetFiles(directory, "简谱-*.mid").Single();
            Assert(ScoreIO.LoadMidi(builtin).Tracks.SelectMany(t => t.Notes).Count() == 3, "内置已有主题生成实际可读取MIDI。");
            Assert(ScoreIO.LoadMidi(notation).Tracks.SelectMany(t => t.Notes).Count() == 3, "已有文字谱生成实际可读取MIDI。");
            byte[] edited = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 72, Start = 0, Duration = 1, Velocity = 90 } }, "用户修改");
            File.WriteAllBytes(builtin, edited); SongFileLibrary.Synchronize(data, entries);
            Assert(File.ReadAllBytes(builtin).SequenceEqual(edited), "重复整理不覆盖用户已修改的曲目副本。");
            File.Delete(Path.Combine(directory, "old.mid"));
            Assert(SongFileLibrary.ResolveMidi(data, "old.mid") == oldMidi, "整理副本缺失时仍兼容旧曲库。");
            string external = Path.Combine(workspace, "官方下载.wav"); File.WriteAllBytes(external, wave);
            string filename = SongFileLibrary.NewMidiFilename(data, "茉莉花 / 宋祖英", external);
            File.WriteAllBytes(Path.Combine(directory, filename), midi);
            string audioName = SongFileLibrary.AttachAudio(data, filename, external);
            Assert(Path.GetFileNameWithoutExtension(audioName) == Path.GetFileNameWithoutExtension(filename), "官方客户端音频与对应MIDI采用同一文件名前缀。");
            Assert(File.Exists(Path.Combine(directory, audioName)) && File.ReadAllBytes(external).SequenceEqual(wave), "音频复制进曲目目录，官方原下载文件保留。");
            Assert(SongFileLibrary.AttachAudio(data, filename, external) == audioName, "重复关联同一音频不新增副本。");
            string ownAudio = Path.Combine(directory, "歌曲-已下载.wav"); File.WriteAllBytes(ownAudio, wave);
            Assert(SongFileLibrary.NewMidiFilename(data, "歌曲", ownAudio) == "歌曲-已下载.mid", "软件下载的音频原文件名直接作为对应MIDI前缀。");
            string legacyPair = Path.Combine(data, "songs", "歌曲-已下载.mid");
            File.WriteAllBytes(legacyPair, edited);
            string nextFilename = SongFileLibrary.NewMidiFilename(data, "歌曲", ownAudio);
            Assert(nextFilename != "歌曲-已下载.mid", "旧目录已有同名MIDI但尚未整理时，新转录也必须避让。");
            File.WriteAllBytes(Path.Combine(directory, nextFilename), midi);
            Assert(SongFileLibrary.ResolveMidi(data, "歌曲-已下载.mid") == legacyPair && File.ReadAllBytes(legacyPair).SequenceEqual(edited),
                "新转录写入后，旧曲目仍解析到原MIDI，既不覆盖也不被新副本遮蔽。");
            Assert(File.ReadAllBytes(ownAudio).SequenceEqual(wave), "避让旧曲名时保留已下载的原音频。");
            string conflicting = Path.Combine(workspace, "其他版本.wav"); wave[47] = 1; File.WriteAllBytes(conflicting, wave);
            Throws<IOException>(delegate { SongFileLibrary.AttachAudio(data, filename, conflicting); }, "不同音频不可覆盖已关联版本。");
            Throws<InvalidDataException>(delegate { SongFileLibrary.AttachAudio(data, "../外部.mid", external); }, "曲目关联拒绝目录穿越。");
            Throws<InvalidDataException>(delegate { SongFileLibrary.ResolveMidi(data, "../外部.mid"); }, "MIDI读取拒绝目录穿越。");
            Console.WriteLine("曲目目录与旧曲库兼容测试通过，共 " + checks + " 项断言（隔离目录，不下载音乐）。");
            return checks;
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
