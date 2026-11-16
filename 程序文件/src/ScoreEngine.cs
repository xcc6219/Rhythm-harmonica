using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HarmonicaStudio
{
    public sealed class NoteEvent
    {
        public int Pitch;
        public double Start, Duration;
        public int Velocity;
    }

    public sealed class ScoreTrack
    {
        public string Name;
        public int Index;
        public List<NoteEvent> Notes = new List<NoteEvent>();
        public double Duration
        {
            get
            {
                double end = 0;
                foreach (NoteEvent note in Notes)
                    end = Math.Max(end, note.Start + note.Duration);
                return end;
            }
        }
        public override string ToString()
        {
            return (Name ?? "未命名旋律") + "（" + Notes.Count.ToString(CultureInfo.InvariantCulture) + " 个音符）";
        }
    }

    public sealed class SongScore
    {
        public string Title;
        public List<ScoreTrack> Tracks = new List<ScoreTrack>();
    }

    public sealed class LibrarySong
    {
        public string Title, Subtitle, Jianpu;
        public double Bpm;
        public bool Chinese;
    }

    public sealed class Fingering
    {
        public string Key, Modifier, Number;
        public bool Playable;
    }

    public static class HarmonicaMap
    {
        private static readonly int[] Degrees = { 0, 2, 4, 5, 7, 9, 11, 12 };
        private static readonly string[] Keys = { "Z", "X", "C", "V", "B", "N", "M", "," };

        public static Fingering Map(int pitch, int basePitch)
        {
            if (pitch >= 0 && pitch <= 127 && basePitch >= 0 && basePitch <= 127)
            {
                int relative = pitch - basePitch;
                int[] octaves = { 0, -1, 1 };
                // Prefer a natural note, then the key without an octave modifier.
                for (int sharp = 0; sharp <= 1; sharp++)
                    foreach (int octave in octaves)
                        for (int degree = 0; degree < Degrees.Length; degree++)
                            if (Degrees[degree] + octave * 12 + sharp == relative)
                            {
                                int writtenOctave = octave + (degree == 7 ? 1 : 0);
                                string number = (sharp == 1 ? "#" : "") + (degree == 7 ? "1" : (degree + 1).ToString(CultureInfo.InvariantCulture));
                                if (writtenOctave > 0) number += new string('\'', writtenOctave);
                                if (writtenOctave < 0) number += new string(',', -writtenOctave);
                                string modifier = octave < 0 ? "低八度 · 左键" : octave > 0 ? "高八度 · 右键" : "";
                                if (sharp == 1) modifier += (modifier.Length > 0 ? "；" : "") + "升半音 · 中键";
                                return new Fingering { Key = Keys[degree], Modifier = modifier, Number = number, Playable = true };
                            }
            }
            return new Fingering { Key = "—", Modifier = "超出口琴音域", Number = "—", Playable = false };
        }
    }

    public static class ScoreIO
    {
        public const int MaximumMidiBytes = 16 * 1024 * 1024;
        public const int MaximumNotes = 200000;
        public const double MaximumDurationSeconds = 4 * 60 * 60;
        private static readonly int[] Scale = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly Regex JianpuToken = new Regex("^([#b]?)([0-7])([',]*)(?::([0-9]+(?:\\.[0-9]+)?))?$", RegexOptions.CultureInvariant);

        public static ScoreTrack ParseJianpu(string text, double bpm, int basePitch)
        {
            if (Double.IsNaN(bpm) || Double.IsInfinity(bpm) || bpm < 1 || bpm > 500)
                throw new ArgumentOutOfRangeException("bpm", "速度必须在 1 到 500 BPM 之间。");
            if (basePitch < 0 || basePitch > 127)
                throw new ArgumentOutOfRangeException("basePitch", "基准音必须在 MIDI 音高 0 到 127 之间。");
            if (String.IsNullOrWhiteSpace(text)) throw new FormatException("简谱为空，请输入音符。");
            if (text.Length > 1024 * 1024) throw new FormatException("简谱文本过长，最大为 1 MB 字符。");
            string[] tokens = text.Replace('|', ' ').Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) throw new FormatException("简谱为空，请输入音符。");
            if (tokens.Length > MaximumNotes) throw new FormatException("简谱超过 200000 个音符或休止符。");
            ScoreTrack track = new ScoreTrack { Name = "简谱旋律", Index = 0 };
            double start = 0, secondsPerBeat = 60 / bpm;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                Match match = JianpuToken.Match(token);
                if (!match.Success) throw InvalidToken(i, token, "请使用 1 到 7、0，以及可选的 # / b、八度符号和 :拍数");
                int degree = match.Groups[2].Value[0] - '0';
                string accidental = match.Groups[1].Value;
                string octaveText = match.Groups[3].Value;
                if (octaveText.IndexOf('\'') >= 0 && octaveText.IndexOf(',') >= 0)
                    throw InvalidToken(i, token, "同一个音符不能混用高八度和低八度符号");
                if (degree == 0 && (accidental.Length > 0 || octaveText.Length > 0))
                    throw InvalidToken(i, token, "休止符 0 不能带升降号或八度符号");
                double beats = 1;
                if (match.Groups[4].Success && (!Double.TryParse(match.Groups[4].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out beats) || beats <= 0 || beats > 128))
                    throw InvalidToken(i, token, "拍数必须大于 0 且不超过 128");
                double duration = beats * secondsPerBeat;
                if (start + duration > MaximumDurationSeconds)
                    throw new FormatException("简谱时长超过 4 小时，请缩短乐谱或提高速度。");
                if (degree != 0)
                {
                    int octave = octaveText.Length * (octaveText.IndexOf(',') >= 0 ? -1 : 1);
                    int pitch = basePitch + Scale[degree - 1] + octave * 12 + (accidental == "#" ? 1 : accidental == "b" ? -1 : 0);
                    if (pitch < 0 || pitch > 127) throw InvalidToken(i, token, "音高超出 MIDI 的 0 到 127 范围");
                    track.Notes.Add(new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 90 });
                }
                start += duration;
            }
            return track;
        }

        private static FormatException InvalidToken(int index, string token, string reason)
        {
            string display = token.Length > 40 ? token.Substring(0, 40) + "…" : token;
            return new FormatException("简谱第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 项“" + display + "”无效：" + reason + "。");
        }

        public static List<NoteEvent> Melody(ScoreTrack track, int transpose)
        {
            if (track == null || track.Notes == null) throw new ArgumentException("请选择有效的旋律轨。", "track");
            if (track.Notes.Count > MaximumNotes) throw new FormatException("旋律超过 200000 个音符。");
            List<NoteEvent> ordered = new List<NoteEvent>();
            foreach (NoteEvent note in track.Notes)
            {
                if (note == null || !Finite(note.Start) || !Finite(note.Duration) || note.Start < 0 || note.Duration <= 0 || note.Start + note.Duration > MaximumDurationSeconds || note.Pitch < 0 || note.Pitch > 127)
                    throw new FormatException("旋律包含无效音符或超过 4 小时的时长。");
                long pitch = (long)note.Pitch + transpose;
                if (pitch < 0 || pitch > 127) throw new FormatException("移调后音高超出 MIDI 的 0 到 127 范围，请调整移调数值。");
                ordered.Add(new NoteEvent { Pitch = (int)pitch, Start = note.Start, Duration = note.Duration, Velocity = note.Velocity });
            }
            ordered.Sort(CompareNotes);
            List<NoteEvent> melody = new List<NoteEvent>();
            for (int i = 0; i < ordered.Count;)
            {
                NoteEvent chosen = ordered[i];
                double groupStart = chosen.Start;
                int j = i + 1;
                while (j < ordered.Count && Math.Abs(ordered[j].Start - groupStart) <= 0.0000001)
                {
                    if (ordered[j].Pitch > chosen.Pitch || (ordered[j].Pitch == chosen.Pitch && ordered[j].Velocity > chosen.Velocity)) chosen = ordered[j];
                    j++;
                }
                chosen.Start = groupStart;
                melody.Add(chosen);
                i = j;
            }
            if (melody.Count > 0)
            {
                double firstStart = melody[0].Start;
                for (int i = 0; i < melody.Count; i++)
                {
                    if (i + 1 < melody.Count) melody[i].Duration = Math.Min(melody[i].Duration, melody[i + 1].Start - melody[i].Start);
                    melody[i].Start -= firstStart;
                }
            }
            return melody;
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static int CompareNotes(NoteEvent a, NoteEvent b)
        {
            int time = a.Start.CompareTo(b.Start);
            if (time != 0) return time;
            int pitch = b.Pitch.CompareTo(a.Pitch);
            return pitch != 0 ? pitch : b.Velocity.CompareTo(a.Velocity);
        }

        public static List<LibrarySong> Builtins()
        {
            return new List<LibrarySong>
            {
                // Chinese originals are marked explicitly; Chinese translations
                // of foreign titles remain in the international collection below.
                new LibrarySong { Title = "茉莉花", Subtitle = "中文传统 · 江苏民歌 · 单旋律片段 · 96 BPM", Bpm = 96, Chinese = true,
                    Jianpu = "3 3:0.5 5:0.5 6:0.5 1':0.5 1':0.5 6:0.5 | 5 5:0.5 6:0.5 5:2 | 3 3:0.5 5:0.5 6:0.5 1':0.5 1':0.5 6:0.5 | 5 5:0.5 6:0.5 5:2 | 5 5 5 3:0.5 5:0.5 | 6 6 5:2 | 3 2:0.5 3:0.5 5 3:0.5 2:0.5 | 1 1:0.5 2:0.5 1:2" },
                new LibrarySong { Title = "凤阳花鼓", Subtitle = "中文传统 · 安徽民歌 · 开篇片段 · 90 BPM", Bpm = 90, Chinese = true,
                    Jianpu = "5:0.5 3:0.5 2:0.5 3:0.5 5:2 | 3:0.5 5:0.5 6:0.5 1':0.5 5:2 | 5:0.5 5 1':0.5 6 5:0.5 3:0.5 | 2:0.5 5:0.5 3:0.5 2:0.5 1:2" },
                new LibrarySong { Title = "小白菜", Subtitle = "中文传统 · 河北民歌 · 单旋律主题两遍 · 72 BPM", Bpm = 72, Chinese = true,
                    Jianpu = "5 3 3 | 2:3 | 5 3 3:0.5 2:0.5 | 1:3 | 1 3 2 | 6,:3 | 2 1 1:0.5 6,:0.5 | 5,:3 | 5 3 3 | 2:3 | 5 3 3:0.5 2:0.5 | 1:3 | 1 3 2 | 6,:3 | 2 1 1:0.5 6,:0.5 | 5,:3" },
                new LibrarySong { Title = "孟姜女", Subtitle = "中文传统 · 江苏民歌 · 单旋律主题 · 80 BPM", Bpm = 80, Chinese = true,
                    Jianpu = "1:0.5 1 2:0.5 3:0.75 2:0.25 3 | 5:0.5 6:0.5 5:0.5 3:0.5 2:2 | 2:0.5 5:0.5 3:0.5 2:0.5 1:0.5 2:0.5 3 | 2:0.5 1:0.5 6,:0.5 1:0.5 5,:2 | 5,:0.75 6,:0.25 1 2 3:0.75 5:0.25 | 2:0.5 1:0.5 1:0.5 5,:0.5 6,:2 | 6,:0.5 2:0.5 1:0.5 6,:0.5 5,:0.75 6,:0.25 1:0.5 1:0.5 | 2:0.5 1:0.5 6,:0.5 1:0.5 5,:2" },
                new LibrarySong { Title = "西风的话", Subtitle = "中国公版原曲 · 黄自 · 单旋律片段 · 88 BPM", Bpm = 88, Chinese = true,
                    Jianpu = "5, 5, 5,:0.5 1:0.5 3:0.5 5:0.5 | 5:2 4:2 | 3 3 3:0.5 2:0.5 1:0.5 7,:0.5 | 6,:3 0 | 6, 6, 6,:0.5 7,:0.5 1:0.5 2:0.5 | 2:2 5,:2 | 5, 5, 6,:0.5 7,:0.5 1:0.5 2:0.5 | 3:3 0" },
                new LibrarySong { Title = "春游", Subtitle = "中国公版原曲 · 李叔同 · 顶声部主题 · 96 BPM", Bpm = 96, Chinese = true,
                    Jianpu = "5 5:0.5 1' 5:0.5 | 3 4:0.5 5:1.5 | 1' 1':0.5 5 5:0.5 | 4 2:0.5 3:1.5 | 5 5:0.5 1' 5:0.5 | 3 4:0.5 5:1.5 | 1' 1':0.5 5 5:0.5 | 4 2:0.5 1:1.5 | 2 2:0.5 2 5:0.5 | 4 4:0.5 3:1.5 | 5 5:0.5 7 6:0.5 | 5 #4:0.5 5:1.5 | 5 5:0.5 1' 5:0.5 | 3 4:0.5 5:1.5 | 1' 1':0.5 5 5:0.5 | 4 2:0.5 1:1.5" },
                new LibrarySong { Title = "小星星", Subtitle = "传统旋律 · 建议 100 BPM", Bpm = 100,
                    Jianpu = "1 1 5 5 6 6 5:2 | 4 4 3 3 2 2 1:2 | 5 5 4 4 3 3 2:2 | 5 5 4 4 3 3 2:2 | 1 1 5 5 6 6 5:2 | 4 4 3 3 2 2 1:2" },
                new LibrarySong { Title = "欢乐颂", Subtitle = "贝多芬传统主题 · 建议 108 BPM", Bpm = 108,
                    Jianpu = "3 3 4 5 | 5 4 3 2 | 1 1 2 3 | 3:1.5 2:0.5 2:2 | 3 3 4 5 | 5 4 3 2 | 1 1 2 3 | 2:1.5 1:0.5 1:2 | 2 2 3 1 | 2 3:0.5 4:0.5 3 1 | 2 3:0.5 4:0.5 3 2 | 1 2 5,:2 | 3 3 4 5 | 5 4 3 2 | 1 1 2 3 | 2:1.5 1:0.5 1:2" },
                new LibrarySong { Title = "两只老虎", Subtitle = "传统轮唱旋律 · 建议 112 BPM", Bpm = 112,
                    Jianpu = "1 2 3 1 | 1 2 3 1 | 3 4 5:2 | 3 4 5:2 | 5:0.5 6:0.5 5:0.5 4:0.5 3 1 | 5:0.5 6:0.5 5:0.5 4:0.5 3 1 | 1 5, 1:2 | 1 5, 1:2" },
                new LibrarySong { Title = "生日快乐", Subtitle = "传统生日旋律 · 建议 96 BPM", Bpm = 96,
                    Jianpu = "5:0.5 5:0.5 6 5 1' 7:2 | 5:0.5 5:0.5 6 5 2' 1':2 | 5:0.5 5:0.5 5' 3' 1' 7 6 | 4':0.5 4':0.5 3' 1' 2' 1':2" },
                // Monophonic transcriptions of public-domain melodies. These are
                // theme excerpts, not modern recordings or copyrighted arrangements.
                new LibrarySong { Title = "铃儿响叮当", Subtitle = "公版主题 · Jingle Bells · 片段 · 116 BPM", Bpm = 116,
                    Jianpu = "3 3 3:2 | 3 3 3:2 | 3 5 1:1.5 2:0.5 | 3:4 | 4 4 4:1.5 4:0.5 | 4 3 3 3:0.5 3:0.5 | 3 2 2 3 | 2:2 5:2 | 3 3 3:2 | 3 3 3:2 | 3 5 1:1.5 2:0.5 | 3:4 | 4 4 4:1.5 4:0.5 | 4 3 3 3:0.5 3:0.5 | 5 5 4 2 | 1:4" },
                new LibrarySong { Title = "友谊地久天长", Subtitle = "公版主题 · Auld Lang Syne · 片段 · 92 BPM", Bpm = 92,
                    Jianpu = "5, | 1:1.5 1:0.5 1 3 | 2:1.5 1:0.5 2 3 | 1:1.5 1:0.5 3 5 | 6:3 6 | 5:1.5 3:0.5 3 1 | 2:1.5 1:0.5 2 3 | 1:1.5 6,:0.5 6, 5, | 1:3 6 | 5:1.5 3:0.5 3 1 | 2:1.5 1:0.5 2 6 | 5:1.5 3:0.5 3 5 | 6:3 1' | 5:1.5 3:0.5 3 1 | 2:1.5 1:0.5 2 3 | 1:1.5 6,:0.5 6, 5, | 1:3" },
                new LibrarySong { Title = "伦敦桥", Subtitle = "公版主题 · London Bridge · 片段 · 108 BPM", Bpm = 108,
                    Jianpu = "5:1.5 6:0.5 5 4 | 3 4 5:2 | 2 3 4:2 | 3 4 5:2 | 5:1.5 6:0.5 5 4 | 3 4 5:2 | 2:2 5:2 | 3 1:3" },
                new LibrarySong { Title = "玛丽的小羊", Subtitle = "公版主题 · Mary Had a Little Lamb · 片段 · 104 BPM", Bpm = 104,
                    Jianpu = "3 2 1 2 | 3 3 3:2 | 2 2 2:2 | 3 5 5:2 | 3 2 1 2 | 3 3 3 3 | 2 2 3 2 | 1:4" },
                new LibrarySong { Title = "哦，苏珊娜", Subtitle = "福斯特公版主题 · Oh Susanna · 片段 · 112 BPM", Bpm = 112,
                    Jianpu = "1:0.5 2:0.5 | 3 5 5:1.5 6:0.5 | 5 3 1:1.5 2:0.5 | 3 3 2 1 | 2:3 1:0.5 2:0.5 | 3 5 5:1.5 6:0.5 | 5 3 1:1.5 2:0.5 | 3 3 2 2 | 1:3 0 | 4:2 4 6 | 6:2 5:2 | 3 1 1:0.5 2:0.5 3 | 3 2 1 2 | 1:0.5 2:0.5 3 5 5 | 6 5 3 1 | 3 3 2 2 | 1:4" },
                new LibrarySong { Title = "圣徒进行曲", Subtitle = "公版主题 · When the Saints Go Marching In · 片段 · 112 BPM", Bpm = 112,
                    Jianpu = "1 3 4 | 5:3 0:2 1 3 4 | 5:3 0:2 1 3 4 | 5:2 3:2 | 1:2 3:2 | 2:3 0:2 3 3 2 | 1:3 0:2 1 3 5 | 5 4:3 | 4 3 4 | 5:2 3:2 | 2:2 2:2 | 1:4" },
                new LibrarySong { Title = "平安夜", Subtitle = "格鲁伯公版主题 · Silent Night · 片段 · 80 BPM", Bpm = 80,
                    Jianpu = "5:1.5 6:0.5 5 3:3 | 5:1.5 6:0.5 5 3:3 | 2':2 2' 7:3 | 1':2 1' 5:3 | 6:2 6 1':1.5 7:0.5 6 | 5:1.5 6:0.5 5 3:3 | 6:2 6 1':1.5 7:0.5 6 | 5:1.5 6:0.5 5 3:3 | 2':2 2' 4':1.5 2':0.5 7 | 1':3 3':3 | 1' 5 3 5:1.5 4:0.5 2 | 1:6" },
                new LibrarySong { Title = "圣诞大厅", Subtitle = "公版主题 · Deck the Halls · 片段 · 116 BPM", Bpm = 116,
                    Jianpu = "5:1.5 4:0.5 3 2 | 1 2 3 1 | 2:0.5 3:0.5 4:0.5 2:0.5 3:1.5 2:0.5 | 1 7, 1:2 | 5:1.5 4:0.5 3 2 | 1 2 3 1 | 2:0.5 3:0.5 4:0.5 2:0.5 3:1.5 2:0.5 | 1 7, 1:2" },
                new LibrarySong { Title = "扬基歌", Subtitle = "公版主题 · Yankee Doodle · 片段 · 112 BPM", Bpm = 112,
                    Jianpu = "1 1 2 3 | 1 3 2 5, | 1 1 2 3 | 1:2 7,:2 | 1 1 2 3 | 4 3 2 1 | 7, 5, 6, 7, | 1:2 1:2" },
                new LibrarySong { Title = "很久很久以前", Subtitle = "贝利公版主题 · Long Long Ago · 片段 · 96 BPM", Bpm = 96,
                    Jianpu = "1 1 2 3 | 3 4 5:2 | 6:2 5:2 | 3:2 5:2 | 4 3 2:2 | 3 2 1:2 | 2:2 2:2 | 5:4 | 1 1 2 3 | 3 4 5:2 | 6:2 5:2 | 3:2 5:2 | 4 3 2:2 | 3 2 1:2 | 2:2 2:2 | 1:4" },
                new LibrarySong { Title = "奇异恩典", Subtitle = "公版主题 · Amazing Grace · 片段 · 88 BPM", Bpm = 88,
                    Jianpu = "5, | 1:2 3:0.5 1:0.5 | 3:2 2 | 1:2 6, | 5,:2 5, | 1:2 3:0.5 1:0.5 | 3:2 2:0.5 3:0.5 | 5:3 | 0:2 3 | 5:2 3:0.5 1:0.5 | 3:2 2 | 1:2 6, | 5,:2 5, | 1:2 3:0.5 1:0.5 | 3:2 2 | 1:3" },
                new LibrarySong { Title = "小步舞曲", Subtitle = "佩措尔德公版主题 · Minuet BWV Anh.114 · 片段 · 96 BPM", Bpm = 96,
                    Jianpu = "5 1:0.5 2:0.5 3:0.5 4:0.5 | 5 1 1 | 6 4:0.5 5:0.5 6:0.5 7:0.5 | 1' 1 1 | 4 5:0.5 4:0.5 3:0.5 2:0.5 | 3 4:0.5 3:0.5 2:0.5 1:0.5 | 2 3:0.5 2:0.5 1:0.5 7,:0.5 | 1:3" },
                new LibrarySong { Title = "致爱丽丝", Subtitle = "贝多芬公版主题 · Fur Elise · 单旋律片段 · 90 BPM", Bpm = 90,
                    Jianpu = "3':0.5 #2':0.5 3':0.5 #2':0.5 3':0.5 7:0.5 2':0.5 1':0.5 | 6:1.5 1:0.5 3:0.5 6:0.5 | 7:1.5 3:0.5 #5:0.5 7:0.5 | 1':1.5 3':0.5 #2':0.5 3':0.5 #2':0.5 3':0.5 7:0.5 2':0.5 1':0.5 | 6:1.5 1:0.5 3:0.5 6:0.5 | 7:1.5 3:0.5 1':0.5 7:0.5 | 6:3" },
                new LibrarySong { Title = "卡农", Subtitle = "帕赫贝尔公版主题 · Canon in D · 移至 C 调片段 · 88 BPM", Bpm = 88,
                    Jianpu = "3:2 2:2 | 1:2 7,:2 | 6,:2 5,:2 | 6,:2 7,:2 | 1:2 7,:2 | 6,:2 5,:2 | 4,:2 3,:2 | 4,:2 2,:2 | 1 3 5 4 | 3 1 3 2 | 1 6, 1 5, | 6, 1 7, 5, | 1 3 5 4 | 3 1 3 2 | 1 6, 1 5, | 4, 3, 4, 2," },
                new LibrarySong { Title = "月光下", Subtitle = "公版主题 · Au Clair de la Lune · 片段 · 100 BPM", Bpm = 100,
                    Jianpu = "1 1 1 2 | 3:2 2:2 | 1 3 2 2 | 1:4 | 1 1 1 2 | 3:2 2:2 | 1 3 2 2 | 1:4 | 2 2 2 2 | 6,:2 6,:2 | 2 1 7, 6, | 5,:4 | 1 1 1 2 | 3:2 2:2 | 1 3 2 2 | 1:4" },
                new LibrarySong { Title = "麦克唐纳农场", Subtitle = "公版主题 · Old MacDonald Had a Farm · 片段 · 108 BPM", Bpm = 108,
                    Jianpu = "1 1 1 5, | 6, 6, 5,:2 | 3 3 2 2 | 1:3 5, | 1 1 1 5, | 6, 6, 5,:2 | 3 3 2 2 | 1:3 5, | 1 1 1:2 | 1 1 1:2 | 1 1 1 1 | 1 1 1 1 | 1 1 1 5, | 6, 6, 5,:2 | 3 3 2 2 | 1:4" }
            };
        }

        public static SongScore LoadMidi(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 MIDI 文件。", "path");
            byte[] data;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumMidiBytes) throw MidiError("文件超过 16 MB 上限");
                    if (stream.Length < 14) throw MidiError("文件太短，缺少标准 MIDI 头");
                    data = new byte[(int)stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int count = stream.Read(data, read, data.Length - read);
                        if (count == 0) throw MidiError("读取期间文件被截断");
                        read += count;
                    }
                    if (stream.ReadByte() != -1) throw MidiError("读取期间文件发生变化，请重新导入");
                }
            }
            catch (UnauthorizedAccessException) { throw new IOException("无法读取 MIDI 文件，请检查文件访问权限。"); }
            catch (IOException ex) { throw new IOException("无法读取 MIDI 文件：" + ex.Message, ex); }

            Cursor file = new Cursor(data, 0, data.Length);
            if (file.Tag() != "MThd") throw MidiError("缺少 MThd 文件头；仅支持标准 .mid / .midi 文件");
            int headerLength = file.LengthValue();
            if (headerLength < 6 || headerLength > file.Remaining) throw MidiError("MThd 文件头长度无效");
            int headerEnd = file.Position + headerLength;
            int format = file.U16(), trackCount = file.U16(), division = file.U16();
            if (format == 2) throw MidiError("暂不支持格式 2；请导出为 MIDI 格式 0 或 1");
            if (format != 0 && format != 1) throw MidiError("未知的 MIDI 格式编号");
            if (trackCount < 1 || trackCount > 1024 || (format == 0 && trackCount != 1)) throw MidiError("轨道数量无效或超过 1024 轨上限");
            if ((division & 0x8000) != 0) throw MidiError("暂不支持 SMPTE 时间格式；请导出为 PPQ 时间格式");
            if (division == 0) throw MidiError("PPQ 时间分辨率不能为 0");
            file.Position = headerEnd;
            List<RawTrack> rawTracks = new List<RawTrack>();
            List<TempoChange> changes = new List<TempoChange>();
            int noteCount = 0, eventCount = 0;
            long maxTick = 0;
            for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
            {
                if (file.Tag() != "MTrk") throw MidiError("轨道 " + (trackIndex + 1).ToString(CultureInfo.InvariantCulture) + " 缺少 MTrk 标记");
                int length = file.LengthValue();
                if (length > file.Remaining) throw MidiError("轨道数据被截断");
                int end = file.Position + length;
                RawTrack track = ReadTrack(new Cursor(data, file.Position, end), trackIndex, changes, ref noteCount, ref eventCount);
                rawTracks.Add(track);
                maxTick = Math.Max(maxTick, track.EndTick);
                file.Position = end;
            }
            if (file.Remaining != 0) throw MidiError("文件声明的轨道数量与尾部数据不匹配");
            TempoMap tempo = new TempoMap(changes, division);
            if (tempo.Seconds(maxTick) > MaximumDurationSeconds) throw MidiError("总时长超过 4 小时上限");
            SongScore song = new SongScore { Title = Path.GetFileNameWithoutExtension(path) };
            foreach (RawTrack raw in rawTracks)
            {
                for (int channel = 0; channel < 16; channel++)
                {
                    if (channel == 9) continue;
                    ScoreTrack track = new ScoreTrack
                    {
                        Name = (String.IsNullOrWhiteSpace(raw.Name) ? "轨道 " + (raw.Index + 1).ToString(CultureInfo.InvariantCulture) : raw.Name) + " · 通道 " + (channel + 1).ToString(CultureInfo.InvariantCulture),
                        Index = song.Tracks.Count
                    };
                    foreach (RawNote note in raw.Notes)
                    {
                        if (note.Channel != channel || note.End <= note.Start) continue;
                        double start = tempo.Seconds(note.Start);
                        double duration = tempo.Seconds(note.End) - start;
                        if (duration > 0) track.Notes.Add(new NoteEvent { Pitch = note.Pitch, Start = start, Duration = duration, Velocity = note.Velocity });
                    }
                    if (track.Notes.Count > 0)
                    {
                        track.Notes.Sort(CompareNotes);
                        song.Tracks.Add(track);
                    }
                }
            }
            if (song.Tracks.Count == 0) throw MidiError("未找到可用旋律音符；第 10 通道的打击乐已排除");
            return song;
        }

        private sealed class Cursor
        {
            private readonly byte[] Data;
            private readonly int End;
            public int Position;
            public int Remaining { get { return End - Position; } }
            public Cursor(byte[] data, int start, int end) { Data = data; Position = start; End = end; }
            public int Byte()
            {
                if (Position >= End) throw MidiError("事件或轨道数据被截断");
                return Data[Position++];
            }
            public int DataByte()
            {
                int value = Byte();
                if (value >= 128) throw MidiError("通道事件的数据字节不能包含状态位");
                return value;
            }
            public int U16() { return (Byte() << 8) | Byte(); }
            public int LengthValue()
            {
                long value = (long)Byte() * 16777216L + (long)Byte() * 65536L + (long)Byte() * 256L + Byte();
                if (value > MaximumMidiBytes) throw MidiError("数据块长度超过 16 MB 上限");
                return (int)value;
            }
            public int Variable()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    int next = Byte();
                    value = (value << 7) | (next & 127);
                    if ((next & 128) == 0) return value;
                }
                throw MidiError("可变长度整数超过 4 字节");
            }
            public string Tag() { return new string(new[] { (char)Byte(), (char)Byte(), (char)Byte(), (char)Byte() }); }
            public void Skip(int count)
            {
                if (count < 0 || count > Remaining) throw MidiError("事件负载数据被截断");
                Position += count;
            }
            public string Text(int count)
            {
                if (count > Remaining) throw MidiError("轨道名称被截断");
                string name;
                try { name = new UTF8Encoding(false, true).GetString(Data, Position, count); }
                catch (DecoderFallbackException) { name = Encoding.Default.GetString(Data, Position, count); }
                Position += count;
                name = Regex.Replace(name, "[\\x00-\\x1f\\x7f]", " ").Trim();
                return name.Length > 128 ? name.Substring(0, 128) : name;
            }
        }

        private sealed class RawNote { public long Start, End; public int Channel, Pitch, Velocity; }
        private sealed class RawTrack
        {
            public string Name;
            public int Index;
            public long EndTick;
            public List<RawNote> Notes = new List<RawNote>();
        }
        private sealed class TempoChange { public long Tick; public int Microseconds, Order; }
        private sealed class TempoPoint { public long Tick; public int Microseconds; public double Time; }

        private static RawTrack ReadTrack(Cursor cursor, int index, List<TempoChange> tempos, ref int noteCount, ref int eventCount)
        {
            RawTrack track = new RawTrack { Index = index };
            Dictionary<int, Queue<RawNote>> active = new Dictionary<int, Queue<RawNote>>();
            int runningStatus = 0;
            long tick = 0;
            bool ended = false;
            while (cursor.Remaining > 0)
            {
                if (++eventCount > 2000000) throw MidiError("事件数量超过 2000000 上限");
                tick += cursor.Variable();
                int next = cursor.Byte(), status, firstData = -1;
                if (next < 128)
                {
                    if (runningStatus == 0) throw MidiError("使用 running status 前缺少有效通道状态");
                    status = runningStatus;
                    firstData = next;
                }
                else status = next;
                if (status == 0xff)
                {
                    runningStatus = 0;
                    int type = cursor.Byte();
                    if (type >= 128) throw MidiError("元事件类型无效");
                    int length = cursor.Variable();
                    if (length > cursor.Remaining) throw MidiError("元事件数据被截断");
                    if (type == 0x2f)
                    {
                        if (length != 0) throw MidiError("轨道结束事件的长度必须为 0");
                        if (cursor.Remaining != 0) throw MidiError("轨道结束事件后仍有数据");
                        ended = true;
                        break;
                    }
                    if (type == 0x51)
                    {
                        if (length != 3) throw MidiError("速度事件的长度必须为 3");
                        int microseconds = (cursor.Byte() << 16) | (cursor.Byte() << 8) | cursor.Byte();
                        if (microseconds == 0) throw MidiError("速度事件不能为 0");
                        tempos.Add(new TempoChange { Tick = tick, Microseconds = microseconds, Order = tempos.Count });
                    }
                    else if (type == 0x03) track.Name = cursor.Text(length);
                    else cursor.Skip(length);
                }
                else if (status == 0xf0 || status == 0xf7)
                {
                    runningStatus = 0;
                    cursor.Skip(cursor.Variable());
                }
                else if (status >= 0x80 && status <= 0xef)
                {
                    runningStatus = status;
                    int type = status >> 4, channel = status & 15;
                    int a = firstData >= 0 ? firstData : cursor.DataByte();
                    int b = type == 0xc || type == 0xd ? 0 : cursor.DataByte();
                    if (type == 0x9 && b > 0)
                    {
                        if (++noteCount > MaximumNotes) throw MidiError("音符数量超过 200000 上限");
                        if (channel != 9)
                        {
                            int key = channel * 128 + a;
                            Queue<RawNote> notes;
                            if (!active.TryGetValue(key, out notes)) { notes = new Queue<RawNote>(); active.Add(key, notes); }
                            RawNote note = new RawNote { Start = tick, Channel = channel, Pitch = a, Velocity = b };
                            notes.Enqueue(note);
                            track.Notes.Add(note);
                        }
                    }
                    else if (type == 0x8 || (type == 0x9 && b == 0))
                    {
                        Queue<RawNote> notes;
                        if (active.TryGetValue(channel * 128 + a, out notes) && notes.Count > 0) notes.Dequeue().End = tick;
                    }
                    else if (type == 0xb && (a == 120 || a == 123))
                    {
                        foreach (KeyValuePair<int, Queue<RawNote>> pair in active)
                            if (pair.Key / 128 == channel) while (pair.Value.Count > 0) pair.Value.Dequeue().End = tick;
                    }
                }
                else throw MidiError("SMF 包含不支持的系统状态 0x" + status.ToString("X2", CultureInfo.InvariantCulture));
            }
            if (!ended) throw MidiError("轨道 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 缺少结束事件");
            foreach (Queue<RawNote> notes in active.Values)
                while (notes.Count > 0) notes.Dequeue().End = tick;
            track.EndTick = tick;
            return track;
        }

        private sealed class TempoMap
        {
            private readonly List<TempoPoint> Points = new List<TempoPoint>();
            private readonly int Ppq;
            public TempoMap(List<TempoChange> changes, int ppq)
            {
                Ppq = ppq;
                Points.Add(new TempoPoint { Tick = 0, Microseconds = 500000, Time = 0 });
                changes.Sort(delegate(TempoChange a, TempoChange b)
                {
                    int time = a.Tick.CompareTo(b.Tick);
                    return time != 0 ? time : a.Order.CompareTo(b.Order);
                });
                foreach (TempoChange change in changes)
                {
                    TempoPoint previous = Points[Points.Count - 1];
                    if (change.Tick == previous.Tick) previous.Microseconds = change.Microseconds;
                    else
                    {
                        double time = previous.Time + (change.Tick - previous.Tick) * (double)previous.Microseconds / (Ppq * 1000000.0);
                        if (time > MaximumDurationSeconds) throw MidiError("速度图对应的时长超过 4 小时上限");
                        Points.Add(new TempoPoint { Tick = change.Tick, Microseconds = change.Microseconds, Time = time });
                    }
                }
            }
            public double Seconds(long tick)
            {
                int low = 0, high = Points.Count - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (Points[middle].Tick <= tick) low = middle;
                    else high = middle - 1;
                }
                TempoPoint point = Points[low];
                return point.Time + (tick - point.Tick) * (double)point.Microseconds / (Ppq * 1000000.0);
            }
        }

        private static FormatException MidiError(string reason) { return new FormatException("MIDI 文件无效：" + reason + "。"); }
    }
}
