using System;
using System.Collections.Generic;
using System.Linq;

namespace HarmonicaStudio
{
    internal sealed class PitchAdaptationResult
    {
        internal readonly List<NoteEvent> Notes = new List<NoteEvent>();
        internal readonly int OctaveAdjustedCount = 0;
        internal int NearbyAdjustedCount, SkippedCount;
        internal int WholeSongShift;
        internal int AdaptedCount { get { return OctaveAdjustedCount + NearbyAdjustedCount; } }
    }
    internal static class PitchAdaptationPolicy
    {
        internal static PitchAdaptationResult Apply(IList<NoteEvent> notes, int basePitch, bool enabled, bool allowWholeSongShift = true)
        {
            if (notes == null) throw new ArgumentNullException("notes");
            if (basePitch < 0 || basePitch > 127) throw new ArgumentOutOfRangeException("basePitch");
            var result = new PitchAdaptationResult();
            if (enabled && allowWholeSongShift && notes.Count > 0)
            {
                double best = -1;
                foreach (int shift in new[] { 0, -12, 12, -24, 24 })
                {
                    double coverage = 0;
                    foreach (NoteEvent note in notes)
                        if (note != null && HarmonicaMap.Map(note.Pitch + shift, basePitch).Playable)
                            coverage += Math.Min(2, Math.Max(0, note.Duration)) * Math.Max(1, note.Velocity);
                    if (coverage > best + 0.000001) { best = coverage; result.WholeSongShift = shift; }
                }
            }
            foreach (NoteEvent note in notes)
            {
                if (note == null) throw new ArgumentException("音符无效。", "notes");
                int pitch = note.Pitch + result.WholeSongShift;
                if (pitch < 0 || pitch > 127) pitch = note.Pitch;
                if (enabled && !HarmonicaMap.Map(pitch, basePitch).Playable)
                {
                    int adapted = -1;
                    // Folding individual notes by an octave can reverse a melody.
                    // Keep one global register; only rescue a neighboring edge pitch.
                    for (int distance = 1; distance <= 2 && adapted < 0; distance++)
                        foreach (int shift in new[] { -distance, distance })
                            if (HarmonicaMap.Map(pitch + shift, basePitch).Playable) { adapted = pitch + shift; break; }
                    if (adapted >= 0) result.NearbyAdjustedCount++;
                    if (adapted >= 0) pitch = adapted;
                }
                if (!HarmonicaMap.Map(pitch, basePitch).Playable) result.SkippedCount++;
                result.Notes.Add(new NoteEvent { Pitch = pitch, Start = note.Start, Duration = note.Duration, Velocity = note.Velocity });
            }
            return result;
        }
    }
}
