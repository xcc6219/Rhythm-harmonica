using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    internal static class EngineTests
    {
        private static int Checks;

        public static int Main()
        {
            string folder = Path.Combine(Path.GetTempPath(), "HarmonicaEngineTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                TestNotation();
                TestMelodyAndMap();
                TestMidi(folder);
                Console.WriteLine("音乐引擎测试通过，共 " + Checks + " 项断言。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("音乐引擎测试失败：" + ex.ToString());
                return 1;
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static void TestNotation()
        {
            ScoreTrack track = ScoreIO.ParseJianpu("0:1 | 1:2 #2:0.5 5':0.5 b7,", 120, 60);
            Check(track.Notes.Count == 4, "简谱休止符不产生音符");
            Near(track.Notes[0].Start, 0.5, "休止符保留时间");
            Near(track.Notes[0].Duration, 1, "两拍的时长");
            Check(track.Notes[1].Pitch == 63 && track.Notes[2].Pitch == 79 && track.Notes[3].Pitch == 58, "升降号和八度");
            Near(track.Duration, 2.5, "简谱总时长");
            string[] invalid = { "8", "#0", "0'", "1:0", "1::2", "1x", "1:NaN", "1',", "1:-1", "1:129" };
            foreach (string text in invalid)
                Throws<FormatException>(delegate { ScoreIO.ParseJianpu(text, 120, 60); }, "拒绝非法简谱 " + text);
            Throws<ArgumentOutOfRangeException>(delegate { ScoreIO.ParseJianpu("1", Double.NaN, 60); }, "拒绝无效速度");
            Throws<FormatException>(delegate { ScoreIO.ParseJianpu("1:128 1:128", 1, 60); }, "简谱四小时时长保护");
            List<LibrarySong> builtins = ScoreIO.Builtins();
            Check(builtins.Count >= 20, "内置曲库至少二十首");
            int chineseCount = 0;
            bool internationalStarted = false;
            HashSet<string> titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (LibrarySong song in builtins)
            {
                if (song.Chinese)
                {
                    chineseCount++;
                    Check(!internationalStarted, "中国原曲优先排列：" + song.Title);
                }
                else internationalStarted = true;
                Check(!String.IsNullOrWhiteSpace(song.Title) && titles.Add(song.Title), "内置歌曲标题唯一且非空：" + song.Title);
                Check(!String.IsNullOrWhiteSpace(song.Subtitle), "内置歌曲有版本与速度说明：" + song.Title);
                ScoreTrack builtin = ScoreIO.ParseJianpu(song.Jianpu, song.Bpm, 60);
                Check(builtin.Notes.Count >= 20 && builtin.Duration > 10, "内置歌曲可解析：" + song.Title);
                foreach (NoteEvent note in builtin.Notes)
                    Check(HarmonicaMap.Map(note.Pitch, 60).Playable, "内置歌曲适合默认口琴音域：" + song.Title);
            }
            Check(chineseCount >= 6, "至少六首显式标记的中国传统或公版原曲");
            foreach (LibrarySong song in builtins)
                if (song.Title == "两只老虎" || song.Title == "小星星")
                    Check(!song.Chinese, "外国旋律不能因中文标题误归中国原曲：" + song.Title);
            string[] retained = { "小星星", "欢乐颂", "两只老虎", "生日快乐" };
            foreach (string title in retained) Check(titles.Contains(title), "保留原有内置歌曲：" + title);
        }

        private static void TestMelodyAndMap()
        {
            ScoreTrack source = new ScoreTrack { Name = "和弦与重叠" };
            source.Notes.Add(new NoteEvent { Pitch = 64, Start = 2.5, Duration = 2, Velocity = 90 });
            source.Notes.Add(new NoteEvent { Pitch = 60, Start = 2, Duration = 3, Velocity = 80 });
            source.Notes.Add(new NoteEvent { Pitch = 72, Start = 2, Duration = 1, Velocity = 100 });
            List<NoteEvent> melody = ScoreIO.Melody(source, -12);
            Check(melody.Count == 2 && melody[0].Pitch == 60 && melody[1].Pitch == 52, "同刻选择最高音并移调");
            Near(melody[0].Start, 0, "移除开头空白");
            Near(melody[0].Duration, 0.5, "截断重叠音符");
            Near(melody[1].Start, 0.5, "保持相对时间");
            Near(source.Notes[2].Start, 2, "归约不修改原始音符");
            Throws<FormatException>(delegate { ScoreIO.Melody(source, 100); }, "移调范围保护");
            Check(HarmonicaMap.Map(65, 60).Key == "V" && HarmonicaMap.Map(65, 60).Modifier == "", "优先自然音指法");
            Check(HarmonicaMap.Map(72, 60).Key == "," && HarmonicaMap.Map(72, 60).Modifier == "", "高音 1 优先无修饰逗号键");
            Check(HarmonicaMap.Map(61, 60).Key == "Z" && HarmonicaMap.Map(61, 60).Modifier.Contains("中键"), "升半音指法");
            Check(HarmonicaMap.Map(48, 60).Modifier.Contains("左键") && HarmonicaMap.Map(84, 60).Modifier.Contains("右键"), "高低八度指法");
            Check(!HarmonicaMap.Map(47, 60).Playable && !HarmonicaMap.Map(86, 60).Playable, "音域范围保护");
        }

        private static void TestMidi(string folder)
        {
            List<byte> tempo = new List<byte>();
            Event(tempo, 0, 0xff, 0x51, 3, 7, 0xa1, 0x20);
            Event(tempo, 480, 0xff, 0x51, 3, 15, 0x42, 0x40);
            Event(tempo, 480, 0xff, 0x2f, 0);
            List<byte> notes = new List<byte>();
            byte[] name = Encoding.UTF8.GetBytes("主旋律");
            Delta(notes, 0); notes.Add(0xff); notes.Add(3); Delta(notes, name.Length); notes.AddRange(name);
            Event(notes, 0, 0x90, 60, 100);
            Event(notes, 0, 0x91, 64, 80);
            Event(notes, 0, 0x99, 35, 90);
            Event(notes, 480, 0x90, 60, 0);
            Event(notes, 0, 62, 90);
            Event(notes, 0, 0x89, 35, 0);
            Event(notes, 480, 0x80, 62, 0);
            Event(notes, 0, 0x81, 64, 0);
            Event(notes, 0, 0xff, 0x2f, 0);
            string path = Path.Combine(folder, "tempo.mid");
            File.WriteAllBytes(path, Midi(1, 480, tempo.ToArray(), notes.ToArray()));
            SongScore song = ScoreIO.LoadMidi(path);
            Check(song.Tracks.Count == 2, "轨道按通道拆分且排除打击乐");
            Check(song.Tracks[0].Name.Contains("主旋律") && song.Tracks[1].Name.Contains("通道 2"), "UTF-8 轨道名称与通道标签");
            Check(song.Tracks[0].Notes.Count == 2 && song.Tracks[1].Notes.Count == 1, "note-on 速度零与 running status");
            Near(song.Tracks[0].Notes[0].Duration, 0.5, "120 BPM 段");
            Near(song.Tracks[0].Notes[1].Start, 0.5, "速度变化开始位置");
            Near(song.Tracks[0].Notes[1].Duration, 1, "60 BPM 段");
            Near(song.Tracks[1].Notes[0].Duration, 1.5, "一个音符跨越速度变化");

            List<byte> single = new List<byte>();
            Event(single, 96, 0x90, 67, 90);
            Event(single, 96, 67, 0);
            Event(single, 0, 0xff, 0x2f, 0);
            File.WriteAllBytes(path, Midi(0, 96, single.ToArray()));
            ScoreTrack formatZero = ScoreIO.LoadMidi(path).Tracks[0];
            Near(formatZero.Notes[0].Start, 0.5, "格式 0 默认速度");
            Near(formatZero.Notes[0].Duration, 0.5, "格式 0 running status 结束音符");

            File.WriteAllBytes(path, Midi(2, 96, single.ToArray()));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "明确拒绝格式 2");
            File.WriteAllBytes(path, Midi(0, 0xe728, single.ToArray()));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "明确拒绝 SMPTE");
            File.WriteAllBytes(path, Midi(0, 96, new byte[] { 0x81, 0x80, 0x80, 0x80, 0x00 }));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "拒绝超长可变长度整数");
            File.WriteAllBytes(path, Midi(0, 96, new byte[] { 0, 0x90, 60 }));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "拒绝截断事件");

            List<byte> cancelled = new List<byte>();
            Event(cancelled, 0, 0x90, 60, 90);
            Event(cancelled, 0, 0xff, 1, 0);
            Event(cancelled, 96, 60, 0);
            Event(cancelled, 0, 0xff, 0x2f, 0);
            File.WriteAllBytes(path, Midi(0, 96, cancelled.ToArray()));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "元事件取消 running status");

            List<byte> tooLong = new List<byte>();
            Event(tooLong, 0, 0x90, 60, 90);
            Event(tooLong, 268435455, 0x80, 60, 0);
            Event(tooLong, 0, 0xff, 0x2f, 0);
            File.WriteAllBytes(path, Midi(0, 96, tooLong.ToArray()));
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "MIDI 四小时时长保护");

            using (FileStream stream = new FileStream(path, FileMode.Create)) stream.SetLength(ScoreIO.MaximumMidiBytes + 1L);
            Throws<FormatException>(delegate { ScoreIO.LoadMidi(path); }, "MIDI 16 MB 文件大小保护");
        }

        private static byte[] Midi(int format, int division, params byte[][] tracks)
        {
            List<byte> data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes("MThd"));
            U32(data, 6); U16(data, format); U16(data, tracks.Length); U16(data, division);
            foreach (byte[] track in tracks)
            {
                data.AddRange(Encoding.ASCII.GetBytes("MTrk")); U32(data, track.Length); data.AddRange(track);
            }
            return data.ToArray();
        }
        private static void U16(List<byte> bytes, int value) { bytes.Add((byte)(value >> 8)); bytes.Add((byte)value); }
        private static void U32(List<byte> bytes, int value)
        {
            bytes.Add((byte)(value >> 24)); bytes.Add((byte)(value >> 16)); bytes.Add((byte)(value >> 8)); bytes.Add((byte)value);
        }
        private static void Event(List<byte> bytes, int delta, params byte[] value) { Delta(bytes, delta); bytes.AddRange(value); }
        private static void Delta(List<byte> bytes, int value)
        {
            List<byte> reversed = new List<byte>(); reversed.Add((byte)(value & 127));
            while ((value >>= 7) > 0) reversed.Add((byte)((value & 127) | 128));
            for (int i = reversed.Count - 1; i >= 0; i--) bytes.Add(reversed[i]);
        }
        private static void Check(bool condition, string name)
        {
            Checks++;
            if (!condition) throw new Exception(name);
        }
        private static void Near(double actual, double expected, string name) { Check(Math.Abs(actual - expected) < 0.000001, name + "：" + actual + " / " + expected); }
        private static void Throws<T>(Action action, string name) where T : Exception
        {
            Checks++;
            try { action(); }
            catch (T) { return; }
            throw new Exception(name + "：应抛出 " + typeof(T).Name);
        }
    }
}
