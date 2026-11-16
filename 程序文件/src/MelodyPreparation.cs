using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HarmonicaStudio
{
    internal sealed class PreparedMelody
    {
        internal List<NoteEvent> Notes;
        internal double SourceOrigin;
        internal int SuppressedOverlaps, JoinedMicroGaps;
    }
    internal static class MelodyPreparation
    {
        internal static int PreferredTrack(IList<ScoreTrack> tracks)
        {
            if (tracks == null) throw new ArgumentNullException("tracks");
            if (tracks.Count == 0) return 0;
            int preferred = -1, priority = Int32.MaxValue;
            for (int i = 0; i < tracks.Count; i++)
            {
                int role = PrimaryRolePriority(tracks[i]);
                if (role > 0 && role < priority) { preferred = i; priority = role; }
            }
            if (preferred >= 0) return preferred;
            double best = Double.NegativeInfinity;
            for (int i = 0; i < tracks.Count; i++)
            {
                ScoreTrack track = tracks[i];
                if (!HasNotes(track) || ExcludedRole(RoleName(track))) continue;
                double score = CandidateScore(track);
                if (score > best) { best = score; preferred = i; }
            }
            return preferred;
        }

        internal static bool IsPrimaryRole(ScoreTrack track)
        { return PrimaryRolePriority(track) > 0; }

        private static bool HasNotes(ScoreTrack track)
        { return track != null && track.Notes != null && track.Notes.Count > 0; }

        private static string RoleName(ScoreTrack track)
        {
            string name = track.Name ?? "";
            // This managed estimate embeds the song title, not an authored
            // instrument role. A title containing MELODY/BASS is still a guess.
            if (name.Contains("连续旋律估计") && name.Contains("音频自动转录")) return "";
            // These application-owned quality labels describe uncertainty, not
            // an accompaniment role. Never reject the entire inferred melody.
            foreach (string label in new[] { "伴奏难区分", "低音可能干扰", "短音较多", "候选需核对", "短音未定", "旋律依据弱" })
                name = name.Replace(label, "");
            name = Regex.Replace(name, @"([a-z])([A-Z])", "$1 $2").ToUpperInvariant();
            return Regex.Replace(name, @"\s*·\s*通道\s*\d+\s*$", "").Trim();
        }

        private static bool EnglishRole(string name, string role)
        { return Regex.IsMatch(name, @"(?<![A-Z0-9])(?:" + role + @")(?![A-Z])"); }

        private static bool ExcludedRole(string name)
        {
            if (name.Contains("伴奏") || name.Contains("伴唱") || name.Contains("和声") || name.Contains("副旋律") ||
                name.Contains("副主旋律") || name.Contains("间奏") || name.Contains("前奏") || name.Contains("尾奏") ||
                name.Contains("贝斯") || name.Contains("低音伴奏") || name.Contains("低音声部") ||
                name.Contains("打击乐") || name.Contains("鼓点") || name.Contains("鼓组") || name.Contains("架子鼓")) return true;
            if (Regex.IsMatch(name, @"第二(?:主旋律|旋律|人声)|次(?:主旋律|旋律)|(?:^|[\s·_()/\-])鼓(?:$|[\s·_()/\-])")) return true;
            if (EnglishRole(name, "BASS|CONTRABASS|DRUMS?|PERCUSSION|PERC|BRIDGE|ACCOMP(?:ANIMENT)?|BACKING|HARMONY|CHORDS?|BGM|RHYTHM")) return true;
            return Regex.IsMatch(name, @"(?<![A-Z])(?:SECOND(?:ARY)?[\s_-]*(?:MELODY|LEAD|VOCALS?)|(?:MELODY|LEAD|VOCALS?)[\s_-]*(?:[2-9]\d*|II|III|IV|SECOND|SECONDARY))(?![A-Z0-9])");
        }

        private static int PrimaryRolePriority(ScoreTrack track)
        {
            if (!HasNotes(track)) return 0;
            string name = RoleName(track);
            if (ExcludedRole(name)) return 0;
            if (name.Contains("主旋律") || EnglishRole(name, @"(?:MAIN[\s_-]*)?MELODY")) return 1;
            if (name.Contains("人声") || EnglishRole(name, "VOCALS?|LEAD")) return 2;
            return 0;
        }

        private struct Boundary
        {
            internal double Time;
            internal int Change;
        }

        // Candidate evidence only: no velocity, highest-pitch or singer-identity
        // claim. Exact time occupancy separates a line from simultaneous chords.
        private static double CandidateScore(ScoreTrack track)
        {
            if (track.Notes.Count > ScoreIO.MaximumNotes) throw new FormatException("候选声轨超过音符数量上限。");
            var boundaries = new List<Boundary>(track.Notes.Count * 2);
            foreach (NoteEvent note in track.Notes)
            {
                if (note == null || note.Pitch < 0 || note.Pitch > 127 || note.Velocity < 1 || note.Velocity > 127 ||
                    Double.IsNaN(note.Start) || Double.IsInfinity(note.Start) || note.Start < 0 ||
                    Double.IsNaN(note.Duration) || Double.IsInfinity(note.Duration) || note.Duration <= 0 ||
                    Double.IsInfinity(note.Start + note.Duration) || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds)
                    throw new FormatException("候选声轨包含无效音符。");
                boundaries.Add(new Boundary { Time = note.Start, Change = 1 });
                boundaries.Add(new Boundary { Time = note.Start + note.Duration, Change = -1 });
            }
            boundaries.Sort(delegate(Boundary a, Boundary b)
            { int order = a.Time.CompareTo(b.Time); return order != 0 ? order : a.Change.CompareTo(b.Change); });
            int active = 0;
            double last = boundaries[0].Time, voiced = 0, single = 0;
            foreach (Boundary edge in boundaries)
            {
                double elapsed = edge.Time - last;
                if (active > 0) voiced += elapsed;
                if (active == 1) single += elapsed;
                active += edge.Change; last = edge.Time;
            }
            double monophony = voiced <= 0 ? 0 : single / voiced;
            if (monophony < 0.60) return Double.NegativeInfinity;
            List<NoteEvent> ordered = track.Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
            int links = 0, near = 0, changing = 0;
            for (int i = 1; i < ordered.Count; i++)
            {
                NoteEvent before = ordered[i - 1], current = ordered[i];
                double gap = current.Start - before.Start - before.Duration;
                if (gap < -0.02 || gap > 2) continue;
                links++;
                int interval = Math.Abs(current.Pitch - before.Pitch);
                if (interval <= 7) near++;
                if (interval >= 1 && interval <= 7) changing++;
            }
            int[] pitches = ordered.Select(n => n.Pitch).OrderBy(p => p).ToArray();
            int centralSpan = pitches[(int)Math.Floor((pitches.Length - 1) * 0.95)] - pitches[(int)Math.Floor((pitches.Length - 1) * 0.05)];
            double variation = Math.Min(4, pitches.Distinct().Count() - 1) / 4.0;
            return 60 * monophony + (links == 0 ? 0 : 20.0 * near / links + 10.0 * changing / links) +
                10 * variation + (centralSpan <= 24 ? 10 : centralSpan <= 36 ? 5 : 0);
        }
        internal static PreparedMelody Build(ScoreTrack track, int transpose, bool inferred)
        {
            // Validate the original track before suppressing an estimated accompaniment.
            List<NoteEvent> strict = ScoreIO.Melody(track, transpose);
            var result = new PreparedMelody { Notes = strict,
                SourceOrigin = track.Notes.Count == 0 ? 0 : track.Notes.Min(n => n.Start) };
            // A new continuous estimate has already resolved rest and attack frames.
            // Legacy polyphonic cleanup must not close those model-supported rests.
            if (!inferred || track.Notes.Count == 0 || (track.Name ?? "").Contains("连续旋律估计")) return result;
            var ordered = track.Notes.OrderBy(n => n.Start).ThenByDescending(n => n.Velocity).ToList();
            var selected = new List<NoteEvent>();
            for (int i = 0; i < ordered.Count;)
            {
                NoteEvent chosen = ordered[i]; int j = i + 1;
                while (j < ordered.Count && ordered[j].Start - ordered[i].Start <= 0.035 &&
                    ordered[j].Start < ordered[i].Start + ordered[i].Duration - 0.000001)
                {
                    if (ordered[j].Velocity > chosen.Velocity ||
                        (ordered[j].Velocity == chosen.Velocity && ordered[j].Pitch > chosen.Pitch)) chosen = ordered[j];
                    j++;
                }
                NoteEvent previous = selected.Count == 0 ? null : selected[selected.Count - 1];
                if (previous != null && chosen.Start < previous.Start + previous.Duration - 0.02 &&
                    chosen.Velocity <= previous.Velocity * 0.5 && Math.Abs(chosen.Pitch - previous.Pitch) >= 7)
                    result.SuppressedOverlaps += j - i;
                else
                {
                    selected.Add(new NoteEvent { Pitch = chosen.Pitch, Velocity = chosen.Velocity,
                        Start = chosen.Start, Duration = chosen.Duration });
                    result.SuppressedOverlaps += j - i - 1;
                }
                i = j;
            }
            result.SourceOrigin = selected.Count == 0 ? 0 : selected.Min(n => n.Start);
            result.Notes = ScoreIO.Melody(new ScoreTrack { Name = track.Name, Index = track.Index, Notes = selected }, transpose);
            for (int i = 0; i + 1 < result.Notes.Count; i++)
            {
                NoteEvent note = result.Notes[i], next = result.Notes[i + 1];
                double gap = next.Start - note.Start - note.Duration;
                if (note.Pitch != next.Pitch && gap > 0.000001 && gap <= 0.035)
                { note.Duration += gap; result.JoinedMicroGaps++; }
            }
            return result;
        }
    }
}
