using System;
using System.Collections.Generic;
using System.Globalization;

namespace HarmonicaStudio
{
    internal sealed class HarmonicaTimingAnalysis
    {
        internal double EffectiveSpeed, MaximumSpeed;
        internal bool CanPerform;
        internal string Message;
    }

    internal sealed class HarmonicaArrangedPlan
    {
        internal List<NoteEvent> Notes;
        internal double Duration, EffectiveSpeed;
        internal int SkippedCount, ArticulatedCount;
    }

    internal static class HarmonicaArrangement
    {
        private const int MinimumGateTicks = 44;
        private const int SameKeyGapTicks = 20;
        private const int ModifierGapTicks = 34;
        // Integer-tick intervals survive endpoint rounding at any start phase.
        // The tiny reserve exceeds floating-point error even near the four-hour limit.
        private const double RoundingReserveSeconds = 0.00000001;
        private const double TimeTolerance = 0.0000001;
        private static readonly double MinimumGateSeconds = MinimumGateTicks / (double)MidiWriter.TicksPerSecond + RoundingReserveSeconds;

        internal static HarmonicaTimingAnalysis Analyze(IList<NoteEvent> selectedCycle, double cycleDuration,
            bool repeat, int basePitch, double requestedSpeed)
        {
            ValidateSpeed(requestedSpeed, "requestedSpeed");
            List<NoteEvent> notes = PlayableCopy(selectedCycle, cycleDuration, basePitch);
            if (notes.Count == 0)
                return new HarmonicaTimingAnalysis { CanPerform = false, EffectiveSpeed = 0, MaximumSpeed = 0,
                    Message = "当前片段没有口琴音域内的音符，请调整移调、片段或选择主旋律。" };

            double maximum = 1.5;
            int limitingNote = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                double allowed = notes[i].Duration / MinimumGateSeconds;
                NoteEvent next = i + 1 < notes.Count ? notes[i + 1] : repeat ? notes[0] : null;
                if (next != null)
                {
                    double nextStart = next.Start + (i + 1 == notes.Count ? cycleDuration : 0);
                    double gap = GapSeconds(RequiredGapTicks(notes[i], next, basePitch));
                    allowed = Math.Min(allowed, (nextStart - notes[i].Start) / (MinimumGateSeconds + gap));
                }
                if (allowed < maximum) { maximum = allowed; limitingNote = i + 1; }
            }
            double effective = requestedSpeed <= maximum ? requestedSpeed : Math.Floor(maximum * 100) / 100;
            bool possible = effective >= 0.5;
            return new HarmonicaTimingAnalysis
            {
                MaximumSpeed = maximum,
                EffectiveSpeed = possible ? effective : 0,
                CanPerform = possible,
                Message = !possible
                    ? "第 " + limitingNote + " 个可奏音过短或换指过密，降至 0.5 倍仍不满足；请调整片段或使用较简洁的主旋律谱。"
                    : effective < requestedSpeed
                        ? "为保留全部旋律，实际速度降为 " + effective.ToString("0.##", CultureInfo.InvariantCulture) + " 倍。"
                        : "当前速度满足口琴持音与换指要求。"
            };
        }

        internal static HarmonicaArrangedPlan Prepare(PlaybackPlan plan, int basePitch, double effectiveSpeed)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            ValidateSpeed(effectiveSpeed, "effectiveSpeed");
            List<NoteEvent> notes = PlayableCopy(plan.Notes, plan.Duration, basePitch);
            if (notes.Count == 0)
                throw new InvalidOperationException("当前片段没有口琴音域内的音符，请调整移调或选择主旋律。");
            var shortened = new bool[notes.Count];
            for (int i = 0; i < notes.Count; i++)
            {
                NoteEvent note = notes[i];
                double originalDuration = note.Duration;
                if (i + 1 < notes.Count)
                {
                    double gap = GapSeconds(RequiredGapTicks(note, notes[i + 1], basePitch));
                    double available = notes[i + 1].Start - note.Start - gap * effectiveSpeed;
                    note.Duration = Math.Min(note.Duration, available);
                }
                if (note.Duration / effectiveSpeed < MinimumGateSeconds - 0.0000000001)
                    throw TimingFailure(i);
                shortened[i] = note.Duration < originalDuration - 0.0000000001;
            }
            // Match the exact arithmetic used when MainForm scales a note for export.
            // This validation does not alter the generic MIDI writer or the source plan.
            int articulated = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                NoteEvent note = notes[i];
                double start = note.Start / effectiveSpeed;
                int startTick = Tick(start), endTick = Tick(start + note.Duration / effectiveSpeed);
                if (i + 1 < notes.Count)
                {
                    double nextStart = notes[i + 1].Start / effectiveSpeed;
                    int nextTick = Tick(nextStart), gapTicks = RequiredGapTicks(note, notes[i + 1], basePitch);
                    // Splitting start/speed + duration/speed can land on the other
                    // side of a half tick from the identical next.Start/speed.
                    // Fix only that floating-point tail, never accept an overlap.
                    if (gapTicks == 0 && endTick == nextTick + 1 &&
                        start + note.Duration / effectiveSpeed - nextStart <= RoundingReserveSeconds)
                    {
                        note.Duration -= RoundingReserveSeconds * effectiveSpeed;
                        shortened[i] = true;
                        endTick = Tick(start + note.Duration / effectiveSpeed);
                    }
                    if (nextTick - endTick < gapTicks) throw TimingFailure(i);
                }
                if (note.Duration / effectiveSpeed < 0.045 || endTick - startTick < MinimumGateTicks) throw TimingFailure(i);
                if (shortened[i]) articulated++;
            }
            return new HarmonicaArrangedPlan { Notes = notes, Duration = plan.Duration, EffectiveSpeed = effectiveSpeed,
                SkippedCount = plan.Notes.Count - notes.Count, ArticulatedCount = articulated };
        }

        private static List<NoteEvent> PlayableCopy(IList<NoteEvent> source, double duration, int basePitch)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!Finite(duration) || duration <= 0 || duration > ScoreIO.MaximumDurationSeconds)
                throw new ArgumentOutOfRangeException("duration", "片段时长须大于零，且不超过 4 小时。");
            if (basePitch < 0 || basePitch > 127) throw new ArgumentOutOfRangeException("basePitch");
            if (source.Count > ScoreIO.MaximumNotes)
                throw new ArgumentException("编配旋律超过 200000 个音符，请缩短片段。", "source");
            var result = new List<NoteEvent>(source.Count);
            double previousEnd = 0;
            for (int i = 0; i < source.Count; i++)
            {
                NoteEvent note = source[i];
                if (note == null || note.Pitch < 0 || note.Pitch > 127 || note.Velocity < 1 || note.Velocity > 127 ||
                    !Finite(note.Start) || note.Start < 0 || !Finite(note.Duration) || note.Duration <= 0 ||
                    !Finite(note.Start + note.Duration) || note.Start + note.Duration > duration + TimeTolerance ||
                    note.Start + note.Duration > ScoreIO.MaximumDurationSeconds)
                    throw new ArgumentException("编配旋律包含无效音符，或音符超出片段时长。", "source");
                if (i > 0 && note.Start < previousEnd - TimeTolerance)
                    throw new ArgumentException("编配需要按时间排序、没有重叠的单旋律，请先选择并归约主旋律轨。", "source");
                previousEnd = note.Start + note.Duration;
                if (HarmonicaMap.Map(note.Pitch, basePitch).Playable)
                    result.Add(new NoteEvent { Pitch = note.Pitch, Velocity = note.Velocity, Start = note.Start, Duration = note.Duration });
            }
            return result;
        }

        private static int RequiredGapTicks(NoteEvent first, NoteEvent next, int basePitch)
        {
            Fingering before = HarmonicaMap.Map(first.Pitch, basePitch), after = HarmonicaMap.Map(next.Pitch, basePitch);
            if (before.Modifier != after.Modifier) return ModifierGapTicks;
            return before.Key == after.Key ? SameKeyGapTicks : 0;
        }
        private static double GapSeconds(int ticks) { return ticks == 0 ? 0 : ticks / (double)MidiWriter.TicksPerSecond + RoundingReserveSeconds; }
        private static InvalidOperationException TimingFailure(int index)
        { return new InvalidOperationException("第 " + (index + 1) + " 个可奏音在当前速度下过短，或不足以换指；请降低速度，或调整片段结束位置与播放总时长。不会自动删音或延长指定时长。"); }
        private static void ValidateSpeed(double speed, string name)
        { if (!Finite(speed) || speed < 0.5 || speed > 1.5) throw new ArgumentOutOfRangeException(name, "编配速度应为 0.5 至 1.5 倍。"); }
        private static int Tick(double seconds) { return checked((int)Math.Round(seconds * MidiWriter.TicksPerSecond, MidpointRounding.AwayFromZero)); }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
