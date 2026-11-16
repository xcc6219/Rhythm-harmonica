"""Local continuous melody estimation from unmodified Basic Pitch model outputs.

This postprocessor selects one plausible pitch path; it does not separate vocals
or establish that the path is the lead singer in a mixed recording.
"""
import argparse
import os
import sys
import numpy as np

# Isolated Python ignores PYTHONIOENCODING; keep the desktop progress pipe UTF-8.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")


def _runs(values):
    if len(values) == 0:
        return []
    edges = np.flatnonzero(values[1:] != values[:-1]) + 1
    starts = np.r_[0, edges]
    ends = np.r_[edges, len(values)]
    return [(int(left), int(right), int(values[left])) for left, right in zip(starts, ends)]


def _probabilities(value, shape, label):
    matrix = np.asarray(value, dtype=np.float32)
    if matrix.shape != shape or not np.all(np.isfinite(matrix)):
        raise ValueError("音符识别结果的{}矩阵无效".format(label))
    if np.any(matrix < -0.000001) or np.any(matrix > 1.000001):
        raise ValueError("音符识别结果的{}概率超出范围".format(label))
    return np.clip(matrix, 0, 1)


def _supported_short_note(begin, finish, pitch_index, bounds, activity, attacks):
    """Retain a quick articulated note, rather than accepting every short run."""
    if bounds[finish] - bounds[begin] < 0.04 - 0.0000001:
        return False
    attack_end = min(finish, begin + 3)
    return (float(np.max(attacks[begin:attack_end, pitch_index])) >= 0.55 and
            float(np.median(activity[begin:finish, pitch_index])) >= 0.45)


def _weighted_register(pitches, weights):
    ordered = np.argsort(pitches)
    ordered_pitches = pitches[ordered]
    cumulative = np.cumsum(weights[ordered])
    total = float(cumulative[-1])
    return [int(ordered_pitches[np.searchsorted(cumulative, total * fraction)]) for fraction in (0.40, 0.60)]


def _continuous_register_candidate(supported, attacks, pitches, bounds, widths, baseline):
    """Correct a dominant low-register prior only with a fuller articulated path.

    This tests temporal support from *all* pitches, not the winning amplitude of
    each frame. It cannot identify a singer: equal-coverage competing bands and
    sustained upper notes intentionally leave the baseline unchanged.
    """
    observed = (np.max(supported, axis=1) >= 0.28) & (widths > 0)
    total = float(np.sum(widths[observed]))
    if total <= 0:
        return None
    old_band = (pitches >= baseline[0] - 9) & (pitches <= baseline[1] + 9)
    old_coverage = float(np.sum(widths[observed & (np.max(supported[:, old_band], axis=1) >= 0.28)]) / total)
    best = None
    for center in range(36, 97, 3):
        band = (pitches >= center - 9) & (pitches <= center + 9)
        band_indices = np.flatnonzero(band)
        peaks = np.argmax(supported[:, band], axis=1)
        pitch_indices = band_indices[peaks]
        strength = supported[np.arange(len(supported)), pitch_indices]
        active = observed & (strength >= 0.28)
        coverage = float(np.sum(widths[active]) / total)
        if coverage < 0.50 or coverage < old_coverage + 0.20 - 0.0000001:
            continue
        register = _weighted_register(pitches[pitch_indices[active]], widths[active])
        if register[0] < baseline[1] + 12:
            continue
        stable = [(left, right, index) for left, right, index in _runs(np.where(active, pitch_indices, -1))
                  if index >= 0 and np.sum(widths[left:right]) >= 0.12]
        changes = 0
        articulated = 0
        classes = set()
        for left, right, index in stable:
            classes.add(int(pitches[index]) % 12)
        for previous, current in zip(stable, stable[1:]):
            step = abs(int(pitches[current[2]]) - int(pitches[previous[2]]))
            if 1 <= step <= 9 and bounds[current[0]] - bounds[previous[1]] <= 0.25:
                changes += 1
                if np.max(attacks[current[0]:min(current[1], current[0] + 3), current[2]]) >= 0.45:
                    articulated += 1
        if changes < 3 or articulated < 2 or len(classes) < 3:
            continue
        rank = coverage + 0.01 * min(changes, 12)
        if best is None or rank > best[0]:
            best = (rank, register, coverage, changes)
    return None if best is None else {"register": best[1], "coverage": best[2], "changes": best[3]}


def track_melody(model_output, frame_times, audio_duration=None):
    """Return monophonic (start, end, MIDI pitch, confidence, None) and metrics.

    ``frame_times`` uses Basic Pitch's model_frames_to_time, including its window
    alignment correction, rather than a guessed constant frame rate. No input
    matrix is modified. Probabilities describe model support, not transcription
    accuracy. The 36..96 search range covers five octaves without preferring the
    highest polyphonic note; the desktop later fits the estimate to its instrument.
    """
    note = np.asarray(model_output["note"], dtype=np.float32)
    if note.ndim != 2 or note.shape[1] != 88 or not 0 < note.shape[0] <= 100000:
        raise ValueError("音符识别结果为空或尺寸无效")
    frames = len(note)
    note = _probabilities(note, (frames, 88), "持续音")
    onset = _probabilities(model_output["onset"], (frames, 88), "起音")
    contour = _probabilities(model_output["contour"], (frames, 264), "音高轮廓")
    times = np.asarray(frame_times, dtype=np.float64)
    if times.ndim != 1 or len(times) not in (frames, frames + 1) or not np.all(np.isfinite(times)) or times[0] < 0:
        raise ValueError("音符识别结果的时间轴无效")
    if len(times) > 1 and np.any(np.diff(times) <= 0):
        raise ValueError("音符识别结果的时间轴必须递增")
    step = float(np.median(np.diff(times))) if len(times) > 1 else 256.0 / 22050
    bounds = times if len(times) == frames + 1 else np.r_[times, times[-1] + step]
    duration = float(bounds[-1]) if audio_duration is None else float(audio_duration)
    if not np.isfinite(duration) or duration <= 0:
        raise ValueError("音频时长无效")

    pitches = np.arange(36, 97, dtype=np.int16)
    bins = pitches - 21
    centers = bins * 3
    contour_support = np.maximum(contour[:, centers], np.maximum(contour[:, centers - 1], contour[:, centers + 1]))
    raw_note = note[:, bins]
    activity = 0.75 * raw_note + 0.25 * contour_support
    attacks = onset[:, bins]
    states = len(pitches) + 1
    rest = states - 1
    # A pitch change must earn back its cost over several supported frames. Weak
    # accompaniment onsets therefore do not automatically terminate held notes.
    transition = np.zeros((states, states), dtype=np.float32)
    distance = np.abs(pitches[:, None] - pitches[None, :])
    transition[:-1, :-1] = -(0.35 + 0.075 * distance)
    np.fill_diagonal(transition, 0)
    transition[:-1, rest] = transition[rest, :-1] = -0.7
    supported = np.where(raw_note >= 0.16, activity, 0)
    peak_indices = np.argmax(supported, axis=1)
    peak_support = supported[np.arange(frames), peak_indices]
    peak_pitches = pitches[peak_indices]
    frame_widths = np.maximum(0, np.minimum(bounds[1:], duration) - np.minimum(bounds[:-1], duration))
    trusted = (peak_support >= 0.30) & (frame_widths > 0)
    register = None
    register_source = "none"
    register_candidate = None
    bass_competition = 0.0
    register_competition = 0.0
    emission = np.log(np.maximum(activity, 0.00001) / 0.30) + 0.08 * attacks
    if np.any(trusted):
        # A time-weighted central band is robust to occasional, louder bass hits.
        # This is a soft register preference, not a classifier of singer identity.
        total = float(np.sum(frame_widths[trusted]))
        register = _weighted_register(peak_pitches[trusted], frame_widths[trusted])
        register_source = "strongest-frame"
        register_candidate = _continuous_register_candidate(supported, attacks, pitches, bounds, frame_widths, register)
        if register_candidate is not None:
            register = register_candidate["register"]
            register_source = "continuous-candidate"
        low, high = register
        near = (pitches >= low - 12) & (pitches <= high + 12)
        near_support = np.max(supported[:, near], axis=1)
        excess = np.maximum(np.maximum(low - pitches, pitches - high) - 12, 0)
        penalty = (1.75 * excess / 12.0).astype(np.float32)
        if register_candidate is not None:
            # This correction addresses a dominant low band. Extending it to
            # upper notes would newly penalize a real higher-octave section,
            # even when its support is much stronger than the old band.
            penalty[pitches > high] = 0
        # Solo low notes, and a new octave section without a competing old band,
        # receive no penalty. One-octave moves are always inside the free margin.
        emission -= np.where(near_support[:, None] >= 0.28, penalty[None, :], 0)
        bass_frames = trusted & (peak_pitches < low - 12) & (near_support >= 0.28)
        bass_competition = float(np.sum(frame_widths[bass_frames]) / total)
        far_support = np.max(np.where(np.abs(pitches[None, :] - peak_pitches[:, None]) >= 12, supported, 0), axis=1)
        register_competition = float(np.sum(frame_widths[trusted & (far_support >= 0.28)]) / total)
    emission[(raw_note < 0.16) | (activity < 0.20)] = -30
    scores = np.full(states, -np.inf, dtype=np.float64)
    scores[rest] = 0
    previous = np.empty((frames, states), dtype=np.uint8)
    destinations = np.arange(states)
    for frame in range(frames):
        candidates = scores[:, None] + transition
        parents = np.argmax(candidates, axis=0)
        previous[frame] = parents
        scores = candidates[parents, destinations]
        # Window-boundary frame spacing can be shorter than one FFT hop.
        weight = min(2.0, max(0.1, (bounds[frame + 1] - bounds[frame]) / step))
        scores[:-1] += emission[frame] * weight
        scores -= np.max(scores)
    labels = np.empty(frames, dtype=np.int16)
    state = int(np.argmax(scores))
    for frame in range(frames - 1, -1, -1):
        labels[frame] = state if state != rest else -1
        state = int(previous[frame, state])

    bridged = 0
    for left, right, pitch_index in _runs(labels):
        if pitch_index != -1 or left == 0 or right == frames or bounds[right] - bounds[left] > 0.05 + 0.0000001:
            continue
        same = int(labels[left - 1])
        if same < 0 or labels[right] != same:
            continue
        neighborhood = slice(max(0, left - 2), min(frames, right + 3))
        # Preserve a real staccato/repeated attack and any competing new note.
        if np.max(attacks[neighborhood]) >= 0.55 or activity[left - 1, same] < 0.45 or activity[right, same] < 0.45:
            continue
        other = activity[left:right].copy()
        other[:, same] = 0
        if np.max(other) >= 0.25:
            continue
        labels[left:right] = same
        bridged += 1

    events = []
    ignored_short = 0
    discarded_short_seconds = 0.0
    preserved_short = 0
    repeated = 0
    minimum_length = 0.075
    for left, right, pitch_index in _runs(labels):
        if pitch_index < 0:
            continue
        if bounds[right] - bounds[left] < minimum_length and not _supported_short_note(
                left, right, pitch_index, bounds, activity, attacks):
            ignored_short += 1
            discarded_short_seconds += max(0.0, min(duration, float(bounds[right])) - min(duration, float(bounds[left])))
            continue
        splits = [left]
        active_attacks = attacks[left:right, pitch_index] >= 0.55
        for local_left, local_right, active in _runs(active_attacks):
            if not active:
                continue
            peak = left + local_left + int(np.argmax(attacks[left + local_left:left + local_right, pitch_index]))
            if bounds[peak] - bounds[splits[-1]] < minimum_length and not _supported_short_note(
                    splits[-1], peak, pitch_index, bounds, activity, attacks):
                continue
            if bounds[right] - bounds[peak] < minimum_length and not _supported_short_note(
                    peak, right, pitch_index, bounds, activity, attacks):
                continue
            history = attacks[max(left, peak - max(1, int(0.10 / step))):peak, pitch_index]
            if len(history) == 0 or np.median(history) >= attacks[peak, pitch_index] * 0.65:
                continue
            before = raw_note[max(left, peak - 2):peak, pitch_index]
            after = raw_note[peak:min(right, peak + 3), pitch_index]
            if len(before) == 0 or np.median(before) < 0.20 or np.median(after) < 0.20:
                continue
            splits.append(peak)
            repeated += 1
        splits.append(right)
        for begin, finish in zip(splits[:-1], splits[1:]):
            start_time = float(bounds[begin])
            end_time = min(duration, float(bounds[finish]))
            if end_time - start_time <= 0:
                continue
            if end_time - start_time < minimum_length:
                if end_time - start_time < 0.04 - 0.0000001 or not _supported_short_note(
                        begin, finish, pitch_index, bounds, activity, attacks):
                    ignored_short += 1
                    discarded_short_seconds += end_time - start_time
                    continue
                preserved_short += 1
            confidence = float(np.median(activity[begin:finish, pitch_index]))
            events.append((start_time, end_time, int(pitches[pitch_index]), confidence, None))

    voiced_seconds = sum(event[1] - event[0] for event in events)
    voiced = labels >= 0
    if np.any(voiced):
        strongest = np.partition(activity[voiced], -2, axis=1)[:, -2:]
        ambiguous = (strongest[:, 0] >= 0.28) & (strongest[:, 0] >= strongest[:, 1] * 0.8)
        ambiguous_ratio = float(np.mean(ambiguous))
    else:
        ambiguous_ratio = 0.0
    quality = {
        "algorithm": "continuous-melody-v3", "notes": len(events),
        "audio_seconds": duration, "voiced_seconds": voiced_seconds,
        "voiced_ratio": min(1.0, voiced_seconds / duration),
        "median_confidence": float(np.median([event[3] for event in events])) if events else 0.0,
        "short_note_ratio": sum(event[1] - event[0] < 0.16 for event in events) / len(events) if events else 0.0,
        "ambiguous_frame_ratio": ambiguous_ratio, "bridged_gaps": bridged,
        "repeated_onsets": repeated, "ignored_short_runs": ignored_short,
        "discarded_short_seconds": discarded_short_seconds, "preserved_short_notes": preserved_short,
        "estimated_register": register, "bass_competition_ratio": bass_competition,
        "register_source": register_source,
        "register_candidate_coverage": register_candidate["coverage"] if register_candidate else 0.0,
        "register_candidate_changes": register_candidate["changes"] if register_candidate else 0,
        "competing_register_ratio": register_competition,
        "warnings": [],
    }
    if not events:
        quality["warnings"].append("未识别到稳定主旋律，请换用更清晰的人声或单乐器版本。")
    else:
        if quality["median_confidence"] < 0.40:
            quality["warnings"].append("旋律依据较弱，部分音高可能不准，请先试听确认。")
        if ambiguous_ratio > 0.35 or register_competition > 0.35:
            quality["warnings"].append("伴奏与旋律较难区分，结果可能混入伴奏，请先试听确认。")
        if bass_competition > 0.12:
            quality["warnings"].append("低音伴奏可能遮住较弱旋律，结果仍需试听确认。")
        if register_candidate is not None:
            quality["warnings"].append("已优先追踪较连贯的旋律候选，仍可能混入伴奏，请试听或优先使用现成旋律谱。")
        if quality["short_note_ratio"] > 0.35:
            quality["warnings"].append("短音较多，可能仍有断音，请换更清晰版本或先试听确认。")
        if discarded_short_seconds >= max(0.10, duration * 0.005):
            quality["warnings"].append("部分短音证据不足，已保留其间隙，可能出现断音，请试听或换用现成旋律谱。")
    return events, quality


def create_melody_midi(events, title, quality=None):
    """Build one non-overlapping inferred track while retaining the legacy marker."""
    if not events:
        raise ValueError("未识别到稳定主旋律，请换用更清晰的人声或单乐器音频")
    import pretty_midi
    midi = pretty_midi.PrettyMIDI(initial_tempo=120, charset="utf-8")
    tags = []
    if quality is not None:
        if quality.get("short_note_ratio", 0) > 0.35:
            tags.append("短音较多")
        if quality.get("ambiguous_frame_ratio", 0) > 0.35 or quality.get("competing_register_ratio", 0) > 0.35:
            tags.append("伴奏难区分")
        if quality.get("median_confidence", 1) < 0.40:
            tags.append("旋律依据弱")
        if quality.get("bass_competition_ratio", 0) > 0.12:
            tags.append("低音可能干扰")
        if quality.get("discarded_short_seconds", 0) >= max(0.10, quality.get("audio_seconds", 0) * 0.005):
            tags.append("短音未定")
        if quality.get("register_source") == "continuous-candidate":
            tags.append("候选需核对")
    suffix = " · 连续旋律估计" + (" · " + " / ".join(tags) if tags else "") + " · 需试听 · 音频自动转录"
    instrument = pretty_midi.Instrument(program=pretty_midi.instrument_name_to_program("Harmonica"),
        name=title[:max(0, 120 - len(suffix))] + suffix)
    previous_end = 0.0
    for start, end, pitch, confidence, _ in events:
        if not (np.isfinite(start) and np.isfinite(end) and 0 <= start < end and start >= previous_end - 0.0000001 and
                0 <= pitch <= 127 and np.isfinite(confidence) and 0 <= confidence <= 1):
            raise ValueError("主旋律结果的音符或次序无效")
        instrument.notes.append(pretty_midi.Note(velocity=max(1, min(127, int(round(127 * confidence)))),
            pitch=int(pitch), start=float(start), end=float(end)))
        previous_end = end
    midi.instruments.append(instrument)
    return midi


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input")
    parser.add_argument("output")
    parser.add_argument("--title", default="音频转录")
    args = parser.parse_args()
    if not os.path.isfile(args.input):
        raise ValueError("音频文件不存在")
    if os.path.getsize(args.input) > 128 * 1024 * 1024:
        raise ValueError("音频文件超过 128 MB")
    import soundfile as sf
    info = sf.info(args.input)
    if not 0 < info.duration <= 600:
        raise ValueError("音频时长须在 0–10 分钟以内")
    from basic_pitch.inference import run_inference
    from basic_pitch import ICASSP_2022_MODEL_PATH
    from basic_pitch.note_creation import model_frames_to_time
    print("正在识别音符，音频只在本机处理...", flush=True)
    output = run_inference(args.input, ICASSP_2022_MODEL_PATH)
    print("正在估计连续主旋律，并核对休止与重复起音...", flush=True)
    events, quality = track_melody(output, model_frames_to_time(len(output["note"])), info.duration)
    if not events:
        raise ValueError("未识别到稳定主旋律，请换用更清晰的人声或单乐器音频")
    midi = create_melody_midi(events, args.title, quality)
    midi.write(args.output)
    for warning in quality["warnings"]:
        print("转录提示：" + warning, flush=True)
    print("转录完成：{} 个音符 · 连续主旋律估计；混音仍需试听确认。".format(len(events)), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("转录失败：{}".format(error), file=sys.stderr, flush=True)
        sys.exit(1)
