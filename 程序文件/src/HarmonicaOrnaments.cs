using System;
using System.Collections.Generic;

namespace HarmonicaStudio
{
    internal sealed class HarmonicaOrnamentResult
    {
        internal List<NoteEvent> Notes;
        internal int RemovedCount;
    }

    internal static class HarmonicaOrnaments
    {
        private const double ContinuityTolerance = 0.0000001;
        private const double DurationTolerance = 0.0000000001;

        // A conservative neighboring-return heuristic, not a beat, accent or
        // singer classifier. Every decision uses the original note snapshot.
        internal static HarmonicaOrnamentResult Simplify(IList<NoteEvent> source, int basePitch, double requestedSpeed, bool enabled)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (basePitch < 0 || basePitch > 127) throw new ArgumentOutOfRangeException("basePitch");
            if (!Finite(requestedSpeed) || requestedSpeed < .5 || requestedSpeed > 1.5)
                throw new ArgumentOutOfRangeException("requestedSpeed", "请求速度应为 0.5 至 1.5 倍。");
            if (source.Count > ScoreIO.MaximumNotes) throw new ArgumentException("旋律超过音符数量上限。", "source");
            var original = new List<NoteEvent>(source.Count);
            double previousStart = 0, previousEnd = 0;
            foreach (NoteEvent note in source)
            {
                if (note == null || note.Pitch < 0 || note.Pitch > 127 || note.Velocity < 1 || note.Velocity > 127 ||
                    !Finite(note.Start) || note.Start < 0 || !Finite(note.Duration) || note.Duration <= 0 ||
                    !Finite(note.Start + note.Duration) || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds ||
                    (original.Count > 0 && (note.Start < previousStart || note.Start < previousEnd - ContinuityTolerance)))
                    throw new ArgumentException("装饰音简化需要有效、按时间排序且没有重叠的单旋律。", "source");
                original.Add(new NoteEvent { Pitch = note.Pitch, Start = note.Start, Duration = note.Duration, Velocity = note.Velocity });
                previousStart = note.Start; previousEnd = note.Start + note.Duration;
            }
            var result = new HarmonicaOrnamentResult { Notes = original };
            if (!enabled || original.Count < 3) return result;

            var removed = new bool[original.Count];
            var protectedAnchor = new bool[original.Count];
            for (int i = 1; i + 1 < original.Count; i++)
            {
                if (protectedAnchor[i] || removed[i - 1]) continue;
                NoteEvent before = original[i - 1], current = original[i], after = original[i + 1];
                int interval = Math.Abs(current.Pitch - before.Pitch);
                if (before.Pitch != after.Pitch || interval < 1 || interval > 2 ||
                    current.Duration / requestedSpeed > .09 + DurationTolerance ||
                    before.Duration / requestedSpeed < .12 - DurationTolerance || after.Duration / requestedSpeed < .12 - DurationTolerance ||
                    before.Duration < 3 * current.Duration - DurationTolerance * requestedSpeed ||
                    after.Duration < 3 * current.Duration - DurationTolerance * requestedSpeed ||
                    current.Velocity > before.Velocity || current.Velocity > after.Velocity ||
                    Math.Abs(current.Start - before.Start - before.Duration) > ContinuityTolerance ||
                    Math.Abs(after.Start - current.Start - current.Duration) > ContinuityTolerance)
                    continue;
                if (!HarmonicaMap.Map(before.Pitch, basePitch).Playable || !HarmonicaMap.Map(current.Pitch, basePitch).Playable ||
                    !HarmonicaMap.Map(after.Pitch, basePitch).Playable) continue;

                // Reuse the arrangement's 44/20/34-tick limits and rounding
                // reserve. A candidate that already fits this speed stays intact.
                HarmonicaTimingAnalysis timing = HarmonicaArrangement.Analyze(new[] { before, current, after },
                    after.Start + after.Duration, false, basePitch, requestedSpeed);
                if (timing.CanPerform && timing.EffectiveSpeed == requestedSpeed) continue;
                removed[i] = true;
                protectedAnchor[i - 1] = protectedAnchor[i + 1] = true;
                result.RemovedCount++;
            }
            if (result.RemovedCount == 0) return result;
            result.Notes = new List<NoteEvent>(original.Count - result.RemovedCount);
            for (int i = 0; i < original.Count; i++)
            {
                if (removed[i]) continue;
                NoteEvent note = original[i];
                if (i + 1 < original.Count && removed[i + 1]) note.Duration = original[i + 2].Start - note.Start;
                result.Notes.Add(note);
            }
            return result;
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
