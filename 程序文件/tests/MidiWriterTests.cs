using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    internal static class MidiWriterTests
    {
        private static int Checks;

        public static int Main()
        {
            string folder = Path.Combine(Path.GetTempPath(), "HarmonicaMidiWriterTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                RoundTrip(folder);
                Limits(folder);
                ExplicitDuration(folder);
                AtomicSave(folder);
                HarmonicaProgramAndTiming(folder);
                HarmonicaAtomicSave(folder);
                Console.WriteLine("MIDI 写出测试通过，共 " + Checks + " 项断言。");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("MIDI 写出测试失败：" + ex); return 1; }
            finally { Directory.Delete(folder, true); }
        }

        private static void RoundTrip(string folder)
        {
            string path = Path.Combine(folder, "中文旋律.mid");
            NoteEvent[] notes = {
                N(127, 4, 2.5, 127), N(60, 1.25, 1.5, 90),
                N(60, 2.75, 1.25, 80), N(0, 4, 0.5, 1)
            };
            byte[] bytes = MidiWriter.ToBytes(notes, "茉莉花 · 旋律测试🎵");
            Check(Encoding.ASCII.GetString(bytes, 0, 4) == "MThd", "标准 MIDI 文件头");
            Check(bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 1, "格式 0、单轨");
            Check(bytes[12] == 1 && bytes[13] == 0xe0, "PPQ 480");
            Check(Encoding.ASCII.GetString(bytes, 14, 4) == "MTrk", "标准轨道头");
            Check(U32(bytes, 18) == bytes.Length - 22, "MTrk 长度没有截断");
            Check(bytes[bytes.Length - 4] == 0 && bytes[bytes.Length - 3] == 0xff && bytes[bytes.Length - 2] == 0x2f && bytes[bytes.Length - 1] == 0, "标准轨道结束事件");
            MidiWriter.Save(path, notes, "茉莉花 · 旋律测试🎵");
            ScoreTrack track = ScoreIO.LoadMidi(path).Tracks[0];
            Check(track.Name.Contains("茉莉花 · 旋律测试🎵"), "Unicode 轨道名称往返保留");
            Check(track.Notes.Count == 4, "未排序输入与同时音符完整往返");
            Check(track.Notes[0].Pitch == 60 && track.Notes[1].Pitch == 60 && track.Notes[2].Pitch == 127 && track.Notes[3].Pitch == 0, "音高上下边界与同音续奏");
            Near(track.Notes[0].Start, 1.25, "保留初始休止");
            Near(track.Notes[0].Duration, 1.5, "第一长音");
            Near(track.Notes[1].Start, 2.75, "同 tick 先 off 后 on");
            Near(track.Notes[1].Duration, 1.25, "重复同音的音符长度不串联");
            Near(track.Notes[2].Duration, 2.5, "不同音高的重叠长音");
            Near(track.Notes[3].Duration, 0.5, "同时低音时长");
            Check(track.Notes[2].Velocity == 127 && track.Notes[3].Velocity == 1, "有效力度上下边界保留");
            Near(notes[0].Start, 4, "写出不修改输入音符");

            MidiWriter.Save(path, new[] { N(64, 0.0015625, 0.0046875, 70) }, "量化");
            NoteEvent quantized = ScoreIO.LoadMidi(path).Tracks[0].Notes[0];
            Near(quantized.Start, 2.0 / MidiWriter.TicksPerSecond, "中点按远离零量化");
            Near(quantized.Duration, 4.0 / MidiWriter.TicksPerSecond, "两个绝对端点量化后音符不丢失");

            MidiWriter.Save(path, new[] { N(72, ScoreIO.MaximumDurationSeconds - 1, 1, 90) }, new String('曲', 128));
            ScoreTrack maximumTime = ScoreIO.LoadMidi(path).Tracks[0];
            Check(maximumTime.Name.StartsWith(new String('曲', 128), StringComparison.Ordinal), "名称最大长度不截断");
            Near(maximumTime.Notes[0].Start, ScoreIO.MaximumDurationSeconds - 1, "长空白 VLQ 往返");
            Near(maximumTime.Duration, ScoreIO.MaximumDurationSeconds, "四小时边界完整保留");

            MidiWriter.Save(path, ManyNotes(ScoreIO.MaximumNotes), "最大音符数");
            ScoreTrack large = ScoreIO.LoadMidi(path).Tracks[0];
            Check(large.Notes.Count == ScoreIO.MaximumNotes, "二十万音符边界完整往返");
            Check(new FileInfo(path).Length <= ScoreIO.MaximumMidiBytes, "最大数量仍在文件上限内");
        }

        private static void Limits(string folder)
        {
            NoteEvent[] valid = { N(60, 0, 1, 90) };
            Throws<ArgumentNullException>(delegate { MidiWriter.ToBytes(null, "无效"); }, "拒绝空集合引用");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new NoteEvent[0], "空"); }, "拒绝没有音符");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new NoteEvent[] { null }, "空项"); }, "拒绝空音符");
            Throws<ArgumentException>(delegate { MidiWriter.ToBytes(valid, new String('名', 129)); }, "超长名称明确拒绝");
            Throws<ArgumentException>(delegate { MidiWriter.ToBytes(valid, "标题\n换行"); }, "拒绝名称控制字符");
            Throws<ArgumentException>(delegate { MidiWriter.ToBytes(valid, new String((char)0xd800, 1)); }, "拒绝无效 Unicode");
            foreach (int pitch in new[] { -1, 128 })
                Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(pitch, 0, 1, 90) }, "无效"); }, "拒绝无效音高 " + pitch);
            foreach (int velocity in new[] { -1, 0, 128 })
                Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, 0, 1, velocity) }, "无效"); }, "拒绝无效力度 " + velocity);
            foreach (double start in new[] { -1, Double.NaN, Double.PositiveInfinity })
                Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, start, 1, 90) }, "无效"); }, "拒绝无效起始时间");
            foreach (double duration in new[] { -1, 0, Double.NaN, Double.PositiveInfinity })
                Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, 0, duration, 90) }, "无效"); }, "拒绝无效时长");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, 0, Double.MaxValue, 90) }, "过长"); }, "拒绝巨大时长");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, ScoreIO.MaximumDurationSeconds, 1, 90) }, "过长"); }, "拒绝四小时以上");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, 0, 0.0001, 90) }, "过短"); }, "短于 tick 的音符明确拒绝而非丢弃");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(new[] { N(60, 0, 2, 90), N(60, 1, 1, 80) }, "重叠"); }, "同音重叠明确拒绝");
            Throws<FormatException>(delegate { MidiWriter.ToBytes(ManyNotes(ScoreIO.MaximumNotes + 1), "过多"); }, "拒绝二十万以上音符");
            string existing = Path.Combine(folder, "保留文件.mid");
            File.WriteAllBytes(existing, new byte[] { 1, 2, 3 });
            Throws<FormatException>(delegate { MidiWriter.Save(existing, new[] { N(128, 0, 1, 90) }, "无效"); }, "先校验再写文件");
            byte[] preserved = File.ReadAllBytes(existing);
            Check(preserved.Length == 3 && preserved[0] == 1 && preserved[2] == 3, "校验失败不覆盖已有文件");
            Throws<ArgumentException>(delegate { MidiWriter.Save(" ", valid, "有效"); }, "拒绝空白路径");
            Check(MidiWriter.ToBytes(valid, null).Length > 22, "允许默认轨道名称");
        }

        private static IEnumerable<NoteEvent> ManyNotes(int count)
        {
            for (int i = 0; i < count; i++) yield return N(60, i / 100.0, 0.005, 90);
        }

        private static void AtomicSave(string folder)
        {
            string path = Path.Combine(folder, "原子导出.mid");
            NoteEvent[] first = { N(60, 0, 1, 90) }, next = { N(67, 0.5, 2, 100) };
            MidiWriter.Save(path, first, "原始旋律");
            byte[] original = File.ReadAllBytes(path);
            int fileCount = Directory.GetFiles(folder).Length;
            // Permit ordinary writes but deny replacement: an atomic writer must
            // fail with the original bytes intact instead of truncating in place.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Throws<IOException>(delegate { MidiWriter.Save(path, next, "新旋律"); }, "目标不允许原子替换时明确拒绝导出");
                Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(original),
                    "替换失败保留完整原曲而非原地覆盖");
                Check(Directory.GetFiles(folder).Length == fileCount, "失败导出不遗留临时 MIDI");
            }
            MidiWriter.Save(path, next, "新旋律", 4);
            Check(ScoreIO.LoadMidi(path).Tracks[0].Notes[0].Pitch == 67 && ReadEndTick(File.ReadAllBytes(path)) == 3840,
                "解除占用后完整替换为新旋律并保留指定时长");
            Check(Directory.GetFiles(folder).Length == fileCount, "成功替换不遗留临时 MIDI");
        }
        private static void ExplicitDuration(string folder)
        {
            NoteEvent[] notes = { N(60, 0.5, 0.5, 90) };
            byte[] natural = MidiWriter.ToBytes(notes, "原始");
            byte[] planned = MidiWriter.ToBytes(notes, "片段", 3.5);
            Check(ReadEndTick(natural) == 960, "旧写出接口仍在末音结束");
            Check(ReadEndTick(planned) == 3360, "实际解析 MIDI EOT，计划尾静音完整写入");
            string path = Path.Combine(folder, "循环计划.mid");
            MidiWriter.Save(path, notes, "片段", 3.5);
            ScoreTrack read = ScoreIO.LoadMidi(path).Tracks[0];
            Check(read.Notes.Count == 1 && read.Notes[0].Pitch == 60, "尾静音不增加假音符");
            Near(read.Notes[0].Start, 0.5, "写出前导静音保留");
            Near(read.Notes[0].Duration, 0.5, "尾静音不拉长实际音符");
            Check(ReadEndTick(File.ReadAllBytes(path)) == 3360, "Save时长重载与ToBytes使用同一EOT");
            foreach (double duration in new[] { 0.0, 0.9, Double.NaN, Double.PositiveInfinity, ScoreIO.MaximumDurationSeconds + 1 })
                Throws<ArgumentOutOfRangeException>(delegate { MidiWriter.ToBytes(notes, "无效", duration); }, "显式MIDI时长必须覆盖音符并在范围内");
            byte[] before = File.ReadAllBytes(path);
            Throws<ArgumentOutOfRangeException>(delegate { MidiWriter.Save(path, notes, "过短", 0.5); }, "无效计划时长写出前拒绝");
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(path)), "无效时长不破坏原文件");
        }

        private static Action<string, IEnumerable<NoteEvent>, string, double> HarmonicaWriter()
        {
            var method = typeof(MidiWriter).GetMethod("SaveHarmonica", new[] {
                typeof(string), typeof(IEnumerable<NoteEvent>), typeof(string), typeof(double) });
            Check(method != null && method.IsStatic && method.ReturnType == typeof(void), "提供独立的公共口琴 MIDI 导出入口");
            return (Action<string, IEnumerable<NoteEvent>, string, double>)Delegate.CreateDelegate(
                typeof(Action<string, IEnumerable<NoteEvent>, string, double>), method);
        }

        private static void HarmonicaProgramAndTiming(string folder)
        {
            var save = HarmonicaWriter();
            foreach (double leadingRest in new[] { 0.0, 0.5 })
            {
                string path = Path.Combine(folder, "口琴音色.mid"), title = "口琴 · 重复音与休止🎵";
                NoteEvent[] notes = { N(64, leadingRest + 1.25, 0.25, 72),
                    N(60, leadingRest, 0.5, 90), N(60, leadingRest + 0.5, 0.5, 80) };
                byte[] generic = MidiWriter.ToBytes(notes, title, 3.5);
                save(path, notes, title, 3.5);
                byte[] harmonica = File.ReadAllBytes(path);
                int programOffset = CheckHarmonicaProgram(harmonica);
                Check(harmonica.Length == generic.Length + 3 && U32(harmonica, 18) == U32(generic, 18) + 3,
                    "口琴导出仅增加三字节 program_change，轨道长度同步更新");
                byte[] withoutProgram = new byte[harmonica.Length - 3];
                Array.Copy(harmonica, 0, withoutProgram, 0, programOffset);
                Array.Copy(harmonica, programOffset + 3, withoutProgram, programOffset, harmonica.Length - programOffset - 3);
                Array.Copy(generic, 18, withoutProgram, 18, 4);
                Check(Convert.ToBase64String(withoutProgram) == Convert.ToBase64String(generic),
                    "去掉口琴乐器事件后与通用导出逐字节相同：速度、UTF8标题、事件次序和EOT均不变");
                Check(ReadEndTick(harmonica) == 3360, "口琴音色事件不移动计划终点或尾部休止");
                SongScore loaded = ScoreIO.LoadMidi(path);
                Check(loaded.Tracks.Count == 1 && loaded.Tracks[0].Notes.Count == 3, "口琴导出实际回读为单轨三个音符");
                ScoreTrack track = loaded.Tracks[0];
                Check(track.Name.Contains(title), "口琴导出的中文与Unicode标题保留");
                int[] pitches = { 60, 60, 64 }, velocities = { 90, 80, 72 };
                double[] starts = { leadingRest, leadingRest + 0.5, leadingRest + 1.25 }, durations = { 0.5, 0.5, 0.25 };
                for (int i = 0; i < 3; i++)
                {
                    Check(track.Notes[i].Pitch == pitches[i] && track.Notes[i].Velocity == velocities[i], "口琴音色不改变音高和力度");
                    Near(track.Notes[i].Start, starts[i], "口琴回读起音时刻保留");
                    Near(track.Notes[i].Duration, durations[i], "口琴回读音长与同音off/on次序保留");
                }
                Check(notes[0].Start == leadingRest + 1.25 && notes[0].Duration == 0.25 && notes[0].Pitch == 64,
                    "口琴导出不重排或改写源音符");
            }
        }

        private static void HarmonicaAtomicSave(string folder)
        {
            var save = HarmonicaWriter();
            string path = Path.Combine(folder, "口琴原子保存.mid");
            NoteEvent[] notes = { N(67, 0.5, 1, 100) };
            MidiWriter.Save(path, new[] { N(60, 0, 1, 90) }, "原始");
            byte[] original = File.ReadAllBytes(path);
            int fileCount = Directory.GetFiles(folder).Length;
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Throws<IOException>(delegate { save(path, notes, "口琴", 3); }, "口琴原子替换被占用时明确失败");
                Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(original), "口琴替换失败保留原文件完整字节");
                Check(Directory.GetFiles(folder).Length == fileCount, "口琴替换失败不遗留临时文件");
            }
            Throws<FormatException>(delegate { save(path, new[] { N(128, 0, 1, 90) }, "无效", 3); }, "口琴入口也先校验音符再写文件");
            Throws<ArgumentOutOfRangeException>(delegate { save(path, notes, "过短", 1); }, "口琴入口拒绝未覆盖音符的终点");
            Throws<ArgumentException>(delegate { save(" ", notes, "空白路径", 3); }, "口琴入口拒绝空白路径");
            Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(original), "口琴参数校验失败不覆盖原文件");
            save(path, notes, "口琴", 3);
            CheckHarmonicaProgram(File.ReadAllBytes(path));
            Check(ScoreIO.LoadMidi(path).Tracks[0].Notes[0].Pitch == 67 && ReadEndTick(File.ReadAllBytes(path)) == 2880,
                "解除占用后口琴原子替换保留新音符与完整时长");
            Check(Directory.GetFiles(folder).Length == fileCount, "口琴替换成功不遗留临时文件");
        }

        private static int CheckHarmonicaProgram(byte[] bytes)
        {
            int position = 22, tick = 0, programs = 0, programOffset = -1;
            bool noteSeen = false, endSeen = false;
            while (position < bytes.Length)
            {
                int eventOffset = position, delta = ReadVariable(bytes, ref position);
                tick += delta;
                int status = bytes[position++];
                if (status == 0xff)
                {
                    int type = bytes[position++], length = ReadVariable(bytes, ref position);
                    position += length;
                    if (type == 0x2f) { endSeen = true; break; }
                }
                else if (status == 0xc0)
                {
                    programs++; programOffset = eventOffset;
                    Check(bytes[position++] == 22 && delta == 0 && tick == 0 && !noteSeen,
                        "实际MIDI事件在首音前以delta0设置通道0、零基编号22的Harmonica");
                    Check(position - eventOffset == 3 && bytes[eventOffset] == 0, "口琴乐器事件编码为00 C0 16");
                }
                else if (status == 0x80 || status == 0x90) { noteSeen = true; position += 2; }
                else throw new Exception("口琴导出包含非预期MIDI事件或通道。");
            }
            Check(programs == 1 && endSeen && position == bytes.Length, "口琴文件仅有一次乐器选择且完整到达EOT");
            return programOffset;
        }

        private static int ReadEndTick(byte[] bytes)
        {
            int position = 22, tick = 0;
            while (position < bytes.Length)
            {
                tick += ReadVariable(bytes, ref position);
                int status = bytes[position++];
                if (status == 0xff)
                {
                    int type = bytes[position++];
                    int length = ReadVariable(bytes, ref position);
                    if (type == 0x2f) return tick;
                    position += length;
                }
                else if (status == 0x80 || status == 0x90) position += 2;
                else if (status == 0xc0) position++;
                else throw new Exception("写出文件含非预期 MIDI 事件。");
            }
            throw new Exception("写出文件没有 EOT。");
        }
        private static int ReadVariable(byte[] bytes, ref int position)
        {
            int value = 0, part;
            do { part = bytes[position++]; value = (value << 7) | (part & 0x7f); } while ((part & 0x80) != 0);
            return value;
        }
        private static NoteEvent N(int pitch, double start, double duration, int velocity)
        {
            return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = velocity };
        }
        private static int U32(byte[] bytes, int position)
        {
            return (bytes[position] << 24) | (bytes[position + 1] << 16) | (bytes[position + 2] << 8) | bytes[position + 3];
        }
        private static void Check(bool okay, string name) { Checks++; if (!okay) throw new Exception(name); }
        private static void Near(double actual, double expected, string name) { Check(Math.Abs(actual - expected) < 0.0000001, name + "：" + actual + " / " + expected); }
        private static void Throws<T>(Action action, string name) where T : Exception
        {
            Checks++;
            try { action(); } catch (T) { return; }
            throw new Exception(name + "：应抛出 " + typeof(T).Name);
        }
    }
}
