using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HarmonicaStudio
{
    public static class MidiWriter
    {
        public const int Ppq = 480;
        public const int TicksPerSecond = 960;
        public const int MaximumTitleCharacters = 128;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static void Save(string path, IEnumerable<NoteEvent> notes, string title)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 MIDI 保存路径。", "path");
            // Validate and build the entire file before touching an existing file.
            byte[] bytes = ToBytes(notes, title);
            SaveCompleteFile(path, bytes);
        }

        public static void Save(string path, IEnumerable<NoteEvent> notes, string title, double duration)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 MIDI 保存路径。", "path");
            byte[] bytes = ToBytes(notes, title, duration);
            SaveCompleteFile(path, bytes);
        }

        private static void SaveCompleteFile(string path, byte[] bytes)
        {
            string destination = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(destination),
                ".harmonica-midi-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Never fall back to truncating the destination. A failed write
                // or replacement must leave the user's existing score intact.
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static byte[] ToBytes(IEnumerable<NoteEvent> notes, string title)
        {
            return ToBytesCore(notes, title, null);
        }

        public static byte[] ToBytes(IEnumerable<NoteEvent> notes, string title, double duration)
        {
            return ToBytesCore(notes, title, duration);
        }

        private static byte[] ToBytesCore(IEnumerable<NoteEvent> notes, string title, double? requestedDuration)
        {
            if (notes == null) throw new ArgumentNullException("notes");
            if (requestedDuration.HasValue && (!Finite(requestedDuration.Value) || requestedDuration.Value <= 0 ||
                requestedDuration.Value > ScoreIO.MaximumDurationSeconds))
                throw new ArgumentOutOfRangeException("duration", "MIDI 时长必须大于 0 且不超过 4 小时。");
            if (title == null) title = "旋律";
            if (title.Length > MaximumTitleCharacters)
                throw new ArgumentException("MIDI 乐谱名称不能超过 128 个字符。", "title");
            foreach (char character in title)
                if (Char.IsControl(character)) throw new ArgumentException("MIDI 乐谱名称不能包含控制字符。", "title");
            byte[] titleBytes;
            try { titleBytes = Utf8.GetBytes(title); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("MIDI 乐谱名称包含无效 Unicode 字符。", "title", ex); }

            List<TimedNote> ordered = new List<TimedNote>();
            double lastEndSeconds = 0;
            foreach (NoteEvent note in notes)
            {
                if (ordered.Count >= ScoreIO.MaximumNotes)
                    throw new FormatException("导出旋律超过 200000 个音符。");
                if (note == null || !Finite(note.Start) || !Finite(note.Duration) || note.Start < 0 || note.Duration <= 0 ||
                    !Finite(note.Start + note.Duration) || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds)
                    throw new FormatException("导出旋律包含无效时刻、时长，或超过 4 小时。");
                if (note.Pitch < 0 || note.Pitch > 127)
                    throw new FormatException("导出旋律音高必须在 MIDI 0 到 127 之间。");
                // Velocity zero means note-off, so it cannot encode an audible note.
                if (note.Velocity < 1 || note.Velocity > 127)
                    throw new FormatException("导出音符力度必须在 1 到 127 之间；0 表示音符结束。");
                int start = Tick(note.Start), end = Tick(note.Start + note.Duration);
                if (end <= start)
                    throw new FormatException("导出音符短于当前 MIDI 时间分辨率，不能无声丢弃；请延长该音符。");
                ordered.Add(new TimedNote { Start = start, End = end, Pitch = note.Pitch, Velocity = note.Velocity, Order = ordered.Count });
                lastEndSeconds = Math.Max(lastEndSeconds, note.Start + note.Duration);
            }
            if (ordered.Count == 0) throw new FormatException("没有可导出的音符。");
            if (requestedDuration.HasValue && requestedDuration.Value < lastEndSeconds - 0.0000001)
                throw new ArgumentOutOfRangeException("duration", "MIDI 时长必须覆盖全部音符；请先截取乐谱再导出。");
            ordered.Sort(delegate(TimedNote a, TimedNote b)
            {
                int result = a.Start.CompareTo(b.Start);
                return result != 0 ? result : a.Order.CompareTo(b.Order);
            });
            int[] lastEnd = new int[128];
            List<MidiEvent> events = new List<MidiEvent>(ordered.Count * 2);
            foreach (TimedNote note in ordered)
            {
                // A single MIDI channel cannot disambiguate overlapping notes of
                // the same pitch. Refuse that input rather than change its melody.
                if (lastEnd[note.Pitch] > note.Start)
                    throw new FormatException("同音高音符存在重叠，请先归约为单旋律再导出。");
                lastEnd[note.Pitch] = note.End;
                events.Add(new MidiEvent { Tick = note.Start, On = true, Pitch = note.Pitch, Velocity = note.Velocity, Order = note.Order });
                events.Add(new MidiEvent { Tick = note.End, On = false, Pitch = note.Pitch, Velocity = 0, Order = note.Order });
            }
            events.Sort(delegate(MidiEvent a, MidiEvent b)
            {
                int result = a.Tick.CompareTo(b.Tick);
                if (result != 0) return result;
                if (a.On != b.On) return a.On ? 1 : -1;
                result = a.Pitch.CompareTo(b.Pitch);
                return result != 0 ? result : a.Order.CompareTo(b.Order);
            });

            using (MemoryStream track = new MemoryStream())
            {
                Variable(track, 0); track.WriteByte(0xff); track.WriteByte(0x03);
                Variable(track, titleBytes.Length); track.Write(titleBytes, 0, titleBytes.Length);
                Variable(track, 0); track.WriteByte(0xff); track.WriteByte(0x51); track.WriteByte(3);
                track.WriteByte(0x07); track.WriteByte(0xa1); track.WriteByte(0x20);
                int previous = 0;
                foreach (MidiEvent item in events)
                {
                    Variable(track, item.Tick - previous);
                    track.WriteByte(item.On ? (byte)0x90 : (byte)0x80);
                    track.WriteByte((byte)item.Pitch); track.WriteByte((byte)item.Velocity);
                    previous = item.Tick;
                }
                int ending = requestedDuration.HasValue ? Tick(Math.Max(requestedDuration.Value, lastEndSeconds)) : previous;
                Variable(track, ending - previous); track.WriteByte(0xff); track.WriteByte(0x2f); track.WriteByte(0);
                if (track.Length + 22 > ScoreIO.MaximumMidiBytes)
                    throw new FormatException("导出的 MIDI 超过 16 MB 上限。");
                using (MemoryStream output = new MemoryStream((int)track.Length + 22))
                {
                    Tag(output, "MThd"); U32(output, 6); U16(output, 0); U16(output, 1); U16(output, Ppq);
                    Tag(output, "MTrk"); U32(output, (int)track.Length);
                    track.Position = 0; track.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private sealed class TimedNote { public int Start, End, Pitch, Velocity, Order; }
        private sealed class MidiEvent { public int Tick, Pitch, Velocity, Order; public bool On; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static int Tick(double seconds) { return checked((int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero)); }
        private static void Tag(Stream stream, string tag)
        {
            foreach (char character in tag) stream.WriteByte((byte)character);
        }
        private static void U16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }
        private static void U32(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }
        private static void Variable(Stream stream, int value)
        {
            if (value < 0 || value > 0x0fffffff) throw new FormatException("MIDI 事件间隔不能编码为标准可变长度整数。");
            int packed = value & 0x7f;
            while ((value >>= 7) > 0) packed = (packed << 8) | ((value & 0x7f) | 0x80);
            while (true)
            {
                stream.WriteByte((byte)packed);
                if ((packed & 0x80) == 0) break;
                packed >>= 8;
            }
        }
    }
}
