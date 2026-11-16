using System;
using System.Collections.Generic;
using System.Linq;

namespace HarmonicaStudio
{
    /// <summary>A shared score-time snapshot for scrolling, playback, preview and export.</summary>
    public sealed class PlaybackPlan
    {
        public List<NoteEvent> Notes { get; private set; }
        public List<ScoreTrack> BackgroundTracks { get; private set; }
        public double Duration { get; private set; }
        public ScoreTailDecision TailDecision { get; private set; }

        private PlaybackPlan() { }

        // Prepared score notes remain available to the canvas and transposition.
        // Output omits unplayable notes without moving later notes or the end time.
        internal static PlaybackPlan PreparePlayableOutput(IList<NoteEvent> notes, double duration, int basePitch)
        {
            if (notes == null) throw new ArgumentNullException("notes");
            if (!Finite(duration) || duration < 0 || duration > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("duration");
            if (basePitch < 0 || basePitch > 127) throw new ArgumentOutOfRangeException("basePitch");
            var output = new List<NoteEvent>();
            foreach (NoteEvent note in notes)
            {
                if (note == null) throw new ArgumentException("演奏计划包含空音符。", "notes");
                if (!HarmonicaMap.Map(note.Pitch, basePitch).Playable) continue;
                output.Add(new NoteEvent { Pitch = note.Pitch, Velocity = note.Velocity,
                    Start = note.Start, Duration = note.Duration });
            }
            return new PlaybackPlan { Notes = output, Duration = duration };
        }

        // start/end use the source score clock. targetSeconds is wall time at speed;
        // zero means one natural pass. A cycle includes the segment's actual rests.
        public static PlaybackPlan Build(IList<NoteEvent> melody, IList<ScoreTrack> backgrounds,
            double start, double end, double targetSeconds, bool repeat, double speed)
        { return Build(melody, backgrounds, start, end, targetSeconds, repeat, speed, null); }

        public static PlaybackPlan Build(IList<NoteEvent> melody, IList<ScoreTrack> backgrounds,
            double start, double end, double targetSeconds, bool repeat, double speed, ScoreTailOptions tailOptions)
        {
            if (melody == null) throw new ArgumentNullException("melody");
            if (!Finite(start) || !Finite(end) || start < 0 || end <= start || end > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("start", "播放区间必须从非负时刻开始，结束晚于开始，且不超过 4 小时。");
            if (!Finite(speed) || speed < 0.5 || speed > 1.5)
                throw new ArgumentOutOfRangeException("speed", "播放速度应为 0.5 至 1.5 倍。");
            if (!Finite(targetSeconds) || targetSeconds < 0 || targetSeconds > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("targetSeconds", "指定时长应为 0 到 4 小时；0 表示自然播放一遍。");
            List<NoteEvent> selected = Clip(melody, start, end, false);
            if (selected.Count == 0) throw new ArgumentException("选取区间内没有旋律音符，请调整开始或结束位置。", "start");
            ScoreTailDecision tailDecision = null;
            if (tailOptions != null)
            {
                tailDecision = ScoreTailPolicy.Analyze(selected, end - start, tailOptions, targetSeconds > 0 || repeat || start > 0);
                tailDecision.Offset(start);
                if (tailDecision.Applied)
                {
                    end = tailDecision.EffectiveEnd;
                    selected = Clip(selected, 0, end - start, false);
                }
            }
            double cycle = end - start;
            double duration = targetSeconds == 0 ? cycle : targetSeconds * speed;
            if (!Finite(duration) || duration > ScoreIO.MaximumDurationSeconds || duration / speed > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("targetSeconds", "此速度下的谱面或实际播放时长超过 4 小时，请缩短时长。");
            if (!repeat && duration > cycle + 0.0000001)
                throw new ArgumentException("指定时长超过选取片段，请勾选循环，或缩短时长。", "targetSeconds");
            if (!repeat) duration = Math.Min(duration, cycle);

            CheckExpandedCount(selected, cycle, duration, ScoreIO.MaximumNotes);
            List<NoteEvent> planned = Expand(selected, cycle, duration);
            if (planned.Count == 0) throw new ArgumentException("指定时长内只有静音，请延长时长或调整开始位置。", "targetSeconds");

            List<ScoreTrack> display = null;
            if (backgrounds != null)
            {
                display = new List<ScoreTrack>(backgrounds.Count);
                int remaining = ScoreIO.MaximumNotes;
                foreach (ScoreTrack track in backgrounds)
                {
                    if (track == null || track.Notes == null)
                        throw new ArgumentException("背景声轨无效。", "backgrounds");
                    List<NoteEvent> segment = Clip(track.Notes, start, end, true);
                    int count = CheckExpandedCount(segment, cycle, duration, remaining);
                    remaining -= count;
                    display.Add(new ScoreTrack { Name = track.Name, Index = track.Index,
                        Notes = Expand(segment, cycle, duration) });
                }
            }
            return new PlaybackPlan { Notes = planned, BackgroundTracks = display, Duration = duration, TailDecision = tailDecision };
        }

        private static List<NoteEvent> Clip(IList<NoteEvent> input, double start, double end, bool background)
        {
            if (input.Count > ScoreIO.MaximumNotes)
                throw new ArgumentException("声轨超过 200000 个音符，请先缩短乐谱。");
            List<NoteEvent> notes = new List<NoteEvent>();
            foreach (NoteEvent note in input)
            {
                if (note == null || !Finite(note.Start) || !Finite(note.Duration) || note.Duration <= 0 ||
                    !Finite(note.Start + note.Duration) || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds ||
                    note.Start < (background ? -ScoreIO.MaximumDurationSeconds : 0))
                    throw new ArgumentException("声轨包含无效时刻或时长。");
                // Transposed background pitches outside MIDI range remain visible as
                // hidden-domain counts in the canvas; they never become input notes.
                if (!background && (note.Pitch < 0 || note.Pitch > 127 || note.Velocity < 1 || note.Velocity > 127))
                    throw new ArgumentException("旋律包含无效音高或力度。");
                double clippedStart = Math.Max(start, note.Start);
                double clippedEnd = Math.Min(end, note.Start + note.Duration);
                if (clippedEnd <= clippedStart) continue;
                notes.Add(new NoteEvent { Pitch = note.Pitch, Velocity = note.Velocity,
                    Start = clippedStart - start, Duration = clippedEnd - clippedStart });
            }
            return notes.OrderBy(n => n.Start).ToList();
        }

        private static int CheckExpandedCount(List<NoteEvent> notes, double cycle, double duration, int maximum)
        {
            double count = 0;
            foreach (NoteEvent note in notes)
            {
                if (ReachesEnd(note.Start, duration)) continue;
                double repetitions = Math.Ceiling((duration - note.Start) / cycle);
                if (ReachesEnd((repetitions - 1) * cycle + note.Start, duration)) repetitions--;
                count += Math.Max(0, repetitions);
                if (count > maximum)
                    throw new ArgumentException("循环后超过 200000 个旋律或背景音符，请缩短时长或扩大循环片段。");
            }
            return (int)count;
        }

        private static List<NoteEvent> Expand(List<NoteEvent> segment, double cycle, double duration)
        {
            List<NoteEvent> output = new List<NoteEvent>();
            if (segment.Count == 0) return output;
            int repetitions = (int)Math.Ceiling(duration / cycle);
            for (int pass = 0; pass < repetitions; pass++)
            {
                double origin = pass * cycle;
                foreach (NoteEvent note in segment)
                {
                    double begin = origin + note.Start;
                    if (ReachesEnd(begin, duration)) break;
                    double finish = Math.Min(duration, origin + note.Start + note.Duration);
                    if (finish > begin)
                        output.Add(new NoteEvent { Pitch = note.Pitch, Velocity = note.Velocity,
                            Start = begin, Duration = finish - begin });
                }
            }
            return output;
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        private static bool ReachesEnd(double start, double end)
        {
            // A mathematically exact cycle can land a few ULPs before its end.
            // Only absorb arithmetic noise; real short partial notes must remain.
            double tolerance = Math.Max(1.0, Math.Abs(end)) * 3.552713678800501e-15;
            return start >= end || end - start <= tolerance;
        }
    }
}
