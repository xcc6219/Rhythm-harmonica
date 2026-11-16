using System;
using System.Collections.Generic;
using System.Linq;

namespace HarmonicaStudio
{
    public sealed class ScoreTailOptions
    {
        public bool Enabled { get; set; }
        public int BasePitch { get; set; }
        public bool IsAudioTranscription { get; set; }
        public bool PreserveExplicitRange { get; set; }
        public ScoreTailOptions() { BasePitch = 60; }
    }

    public sealed class ScoreTailDecision
    {
        public double SourceEnd { get; internal set; }
        public double SuggestedEnd { get; internal set; }
        public double EffectiveEnd { get; internal set; }
        public bool Applied { get; internal set; }
        public int RemovedNotes { get; internal set; }
        public bool HasPlayableNotes { get; internal set; }
        public string Reason { get; internal set; }
        public string Message { get; internal set; }
        internal void Offset(double start)
        { SourceEnd += start; SuggestedEnd += start; EffectiveEnd += start; }
    }

    // A conservative playback-only suggestion. MIDI velocity is a proxy for
    // transcription confidence; it cannot establish that an event is noise.
    public static class ScoreTailPolicy
    {
        private const double Epsilon = 0.0000001;

        public static ScoreTailDecision Analyze(IList<NoteEvent> melody, double end, ScoreTailOptions options)
        { return Analyze(melody, end, options, false); }

        internal static ScoreTailDecision Analyze(IList<NoteEvent> melody, double end, ScoreTailOptions options, bool preserveRange)
        {
            if (melody == null) throw new ArgumentNullException("melody");
            if (options == null) throw new ArgumentNullException("options");
            if (!Finite(end) || end <= 0 || end > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("end", "旋律结束时刻必须大于零且不超过 4 小时。");
            if (options.BasePitch < 0 || options.BasePitch > 127)
                throw new ArgumentOutOfRangeException("options", "基准音必须在 MIDI 音高 0 到 127 之间。");
            if (melody.Count > ScoreIO.MaximumNotes)
                throw new ArgumentException("旋律超过 200000 个音符。", "melody");
            var playable = new List<NoteEvent>();
            foreach (NoteEvent note in melody)
            {
                if (note == null || !Finite(note.Start) || !Finite(note.Duration) || note.Start < 0 || note.Duration <= 0 ||
                    !Finite(note.Start + note.Duration) || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds ||
                    note.Pitch < 0 || note.Pitch > 127 || note.Velocity < 1 || note.Velocity > 127)
                    throw new ArgumentException("旋律包含无效音符。", "melody");
                if (note.Start < end && HarmonicaMap.Map(note.Pitch, options.BasePitch).Playable) playable.Add(note);
            }
            var result = new ScoreTailDecision
            { SourceEnd = end, SuggestedEnd = end, EffectiveEnd = end, HasPlayableNotes = playable.Count > 0, Reason = "none", Message = "" };
            if (playable.Count == 0)
            {
                result.Reason = "no-playable-notes";
                result.Message = "当前声轨没有口琴音域内的音符，请调整声轨、移调或基准音高。";
                return result;
            }
            if (!options.Enabled) { result.Reason = "disabled"; return result; }
            playable.Sort(delegate(NoteEvent a, NoteEvent b) { return a.Start.CompareTo(b.Start); });
            double lastEnd = Math.Min(end, playable.Max(n => n.Start + n.Duration));
            result.SuggestedEnd = lastEnd;
            bool weakTail = false;
            if (options.IsAudioTranscription)
            {
                double suggested;
                if (TryIsolatedWeakTail(playable, out suggested))
                { result.SuggestedEnd = Math.Min(lastEnd, suggested); weakTail = true; }
            }
            if (options.PreserveExplicitRange || preserveRange)
            { result.Reason = "manual-range"; return result; }
            if (result.SuggestedEnd >= end - Epsilon) return result;
            result.Applied = true; result.EffectiveEnd = result.SuggestedEnd;
            result.RemovedNotes = melody.Count(n => n.Start >= result.EffectiveEnd - Epsilon && n.Start < end);
            if (weakTail)
            {
                result.Reason = "isolated-weak-tail";
                result.Message = "已跳过长静默后疑似尾部杂音；若这是原曲尾奏，可关闭“自动忽略无效尾音”恢复。";
            }
            else
            {
                result.Reason = melody.Any(n => n.Start < end && n.Start + n.Duration > lastEnd + Epsilon &&
                    !HarmonicaMap.Map(n.Pitch, options.BasePitch).Playable) ? "unplayable-tail" : "melody-end";
                result.Message = result.Reason == "unplayable-tail" ? "已按最后可吹奏音结束，跳过末尾超出口琴音域的音符。" : "已按当前旋律的最后可吹奏音结束。";
            }
            return result;
        }

        private static bool TryIsolatedWeakTail(List<NoteEvent> notes, out double cutoff)
        {
            cutoff = 0;
            for (int count = 2; count <= 4 && count <= notes.Count - 8; count++)
            {
                int first = notes.Count - count;
                List<NoteEvent> body = notes.Take(first).ToList();
                List<NoteEvent> recent = body.Skip(Math.Max(0, body.Count - 32)).ToList();
                double bodyEnd = body.Max(n => n.Start + n.Duration);
                double typicalVelocity = Median(recent.Select(n => (double)n.Velocity));
                if (bodyEnd - body[0].Start < 8 || typicalVelocity < 64 || body.Count(n => n.Velocity >= 64) < 8 ||
                    body[body.Count - 1].Velocity < typicalVelocity * 0.5) continue;
                List<NoteEvent> tail = notes.Skip(first).ToList();
                // Continuous transcription gates active support at 0.20 before
                // converting confidence to velocity; a ceiling of 20 excluded
                // its ordinary weak events. Both absolute and relative limits
                // still require a substantial drop from a strong phrase.
                if (tail.Any(n => n.Velocity > 40 || n.Velocity > typicalVelocity * 0.5 || n.Duration > 0.22) ||
                    tail.Sum(n => n.Duration) > 0.5 || tail.Max(n => n.Start + n.Duration) - tail[0].Start > 4) continue;
                bool isolated = true;
                for (int i = 1; i < tail.Count; i++)
                    if (tail[i].Start - (tail[i - 1].Start + tail[i - 1].Duration) < 0.75) isolated = false;
                if (!isolated) continue;
                var intervals = new List<double>();
                for (int i = 1; i < recent.Count; i++)
                { double step = recent[i].Start - recent[i - 1].Start; if (step > Epsilon) intervals.Add(step); }
                double silence = Math.Max(6, Median(intervals) * 8);
                if (tail[0].Start - bodyEnd < silence) continue;
                List<NoteEvent> phrase = body.Skip(Math.Max(0, body.Count - 8)).ToList();
                if (tail.Any(n => phrase.Any(previous => Math.Abs(n.Pitch - previous.Pitch) < 12))) continue;
                cutoff = bodyEnd;
                return true;
            }
            return false;
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] ordered = values.OrderBy(n => n).ToArray();
            if (ordered.Length == 0) return 0;
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
