"""Synthetic probability tests; no audio inference, downloads or model loading."""
import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch
import numpy as np

script = Path(__file__).resolve().parents[1] / "scripts" / "audio-to-midi.py"
spec = importlib.util.spec_from_file_location("studio_audio_transcription", script)
transcription = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transcription)


def output(frames):
    return {"note": np.zeros((frames, 88), dtype=np.float32),
            "onset": np.zeros((frames, 88), dtype=np.float32),
            "contour": np.zeros((frames, 264), dtype=np.float32)}


def voice(data, pitch, begin, end, strength=0.8, contour=None):
    index = pitch - 21
    data["note"][begin:end, index] = strength
    center = index * 3
    data["contour"][begin:end, center - 1:center + 2] = strength if contour is None else contour


def attack(data, pitch, frame, strength=0.9):
    data["onset"][frame, pitch - 21] = strength


def track(data, duration=None, times=None):
    if times is None:
        times = np.arange(len(data["note"])) * 0.01
    return transcription.track_melody(data, times, duration)


class MelodyTrackingTests(unittest.TestCase):
    def assert_pitches(self, events, pitches):
        self.assertEqual([event[2] for event in events], pitches)
        for previous, current in zip(events, events[1:]):
            self.assertLessEqual(previous[1], current[0] + 0.0000001)
        self.assertTrue(all(start >= 0 and end > start and 0 <= confidence <= 1 for start, end, _, confidence, _ in events))

    def test_held_melody_is_not_cut_by_weak_accompaniment_onsets(self):
        data = output(300)
        voice(data, 60, 0, 300)
        attack(data, 60, 0)
        for begin in range(20, 280, 20):
            voice(data, 72, begin, begin + 8, strength=0.35)
            attack(data, 72, begin)
        events, quality = track(data)
        self.assert_pitches(events, [60])
        self.assertAlmostEqual(events[0][0], 0)
        self.assertAlmostEqual(events[0][1], 3)
        self.assertEqual(quality["repeated_onsets"], 0)

    def test_highest_polyphonic_pitch_is_not_automatically_the_melody(self):
        data = output(200)
        voice(data, 60, 0, 200, strength=0.8)
        voice(data, 90, 0, 200, strength=0.4)
        events, _ = track(data)
        self.assert_pitches(events, [60])

    def test_intermittent_strong_bass_does_not_mask_continuous_weaker_melody(self):
        for lead, bass in ((60, 40), (70, 50)):
            data = output(400)
            voice(data, lead, 0, 400, strength=0.40)
            voice(data, bass, 80, 130, strength=0.95)
            voice(data, bass, 220, 270, strength=0.95)
            attack(data, bass, 80)
            attack(data, bass, 220)
            events, quality = track(data)
            self.assert_pitches(events, [lead])
            self.assertAlmostEqual(events[0][1], 4)
            self.assertEqual(quality["estimated_register"], [lead, lead])
            self.assertAlmostEqual(quality["bass_competition_ratio"], 0.25)
            self.assertTrue(any("低音伴奏" in warning for warning in quality["warnings"]))

    def test_real_low_solo_register_is_estimated_without_forcing_middle_pitch(self):
        data = output(300)
        voice(data, 37, 0, 100)
        voice(data, 40, 100, 200)
        voice(data, 43, 200, 300)
        events, quality = track(data)
        self.assert_pitches(events, [37, 40, 43])
        self.assertEqual(quality["estimated_register"], [40, 40])
        self.assertEqual(quality["bass_competition_ratio"], 0)

    def test_majority_loud_bass_does_not_define_register_over_full_articulated_line(self):
        for shift in (0, 10):
            data = output(300)
            melody = [60 + shift, 62 + shift, 64 + shift, 65 + shift, 67 + shift, 64 + shift]
            for begin, pitch in zip(range(0, 300, 50), melody):
                voice(data, pitch, begin, begin + 50, strength=0.45)
                attack(data, pitch, begin, strength=0.75)
            voice(data, 40 + shift, 0, 190, strength=0.9)
            events, quality = track(data)
            self.assert_pitches(events, melody)
            self.assertEqual(quality["register_source"], "continuous-candidate")
            self.assertGreater(quality["register_candidate_coverage"], 0.99)
            self.assertGreaterEqual(quality["register_candidate_changes"], 3)
            self.assertAlmostEqual(quality["voiced_seconds"], 3.0)
            self.assertTrue(any("仍可能混入伴奏" in warning for warning in quality["warnings"]))
            midi = transcription.create_melody_midi(events, "候选非歌手身份", quality)
            self.assertIn("候选需核对", midi.instruments[0].name)

    def test_higher_articulated_band_with_equal_coverage_does_not_claim_singer(self):
        data = output(300)
        voice(data, 40, 0, 300, strength=0.9)
        for begin, pitch in zip(range(0, 300, 50), (60, 62, 64, 65, 67, 64)):
            voice(data, pitch, begin, begin + 50, strength=0.45)
            attack(data, pitch, begin)
        events, quality = track(data)
        self.assert_pitches(events, [40])
        self.assertEqual(quality["register_source"], "strongest-frame")
        self.assertEqual(quality["register_candidate_changes"], 0)
        self.assertTrue(any("伴奏与旋律" in warning for warning in quality["warnings"]))

    def test_short_upper_decoration_does_not_replace_real_low_register(self):
        data = output(300)
        voice(data, 40, 0, 240, strength=0.8)
        for begin, pitch in zip(range(30, 180, 30), (60, 62, 64, 65, 67)):
            voice(data, pitch, begin, begin + 20, strength=0.5)
            attack(data, pitch, begin)
        events, quality = track(data)
        self.assert_pitches(events, [40])
        self.assertEqual(quality["register_source"], "strongest-frame")

    def test_upper_candidate_real_rest_remains_a_rest(self):
        data = output(400)
        melody = (60, 62, 64, 65, 67, 64)
        for begin, pitch in zip((0, 50, 100, 200, 250, 300), melody):
            voice(data, pitch, begin, begin + 50, strength=0.45)
            attack(data, pitch, begin)
        voice(data, 40, 0, 150, strength=0.9)
        voice(data, 40, 200, 240, strength=0.9)
        events, quality = track(data)
        self.assert_pitches(events, list(melody))
        self.assertAlmostEqual(events[2][1], 1.5)
        self.assertAlmostEqual(events[3][0], 2.0)
        self.assertEqual(quality["bridged_gaps"], 0)

    def test_corrected_register_does_not_suppress_real_two_octave_section(self):
        data = output(425)
        melody = (60, 62, 64, 65, 67, 64, 60, 62)
        for begin, pitch in zip(range(0, 400, 50), melody):
            voice(data, pitch, begin, begin + 50, strength=0.45)
            attack(data, pitch, begin)
        voice(data, 40, 0, 260, strength=0.9)
        voice(data, 84, 400, 425, strength=0.8)
        attack(data, 84, 400)
        voice(data, 60, 400, 425, strength=0.35)
        events, quality = track(data)
        self.assert_pitches(events, list(melody) + [84])
        self.assertEqual(quality["register_source"], "continuous-candidate")
        self.assertAlmostEqual(events[-1][0], 4)
        self.assertAlmostEqual(events[-1][1], 4.25)

    def test_real_octave_section_with_weak_low_accompaniment_is_preserved(self):
        data = output(300)
        voice(data, 60, 0, 150)
        voice(data, 72, 150, 300)
        voice(data, 48, 0, 300, strength=0.35)
        events, quality = track(data)
        self.assert_pitches(events, [60, 72])
        self.assertEqual(quality["estimated_register"], [60, 72])
        self.assertAlmostEqual(events[0][1], 1.5)
        self.assertAlmostEqual(events[1][0], 1.5)

    def test_two_octave_sections_without_competition_remain_inferred(self):
        data = output(300)
        voice(data, 60, 0, 150)
        voice(data, 84, 150, 300)
        events, quality = track(data)
        self.assert_pitches(events, [60, 84])
        self.assertEqual(quality["estimated_register"], [60, 84])

    def test_two_continuous_bands_warn_instead_of_claiming_singer_identity(self):
        data = output(300)
        voice(data, 40, 0, 300, strength=0.9)
        voice(data, 60, 0, 300, strength=0.4)
        events, quality = track(data)
        self.assert_pitches(events, [40])
        self.assertEqual(quality["estimated_register"], [40, 40])
        self.assertGreater(quality["competing_register_ratio"], 0.99)
        self.assertTrue(any("伴奏与旋律" in warning for warning in quality["warnings"]))

    def test_sustained_real_pitch_change_is_preserved(self):
        data = output(160)
        voice(data, 60, 0, 80)
        voice(data, 64, 80, 160)
        attack(data, 60, 0)
        attack(data, 64, 80)
        events, _ = track(data)
        self.assert_pitches(events, [60, 64])
        self.assertAlmostEqual(events[0][1], 0.8)
        self.assertAlmostEqual(events[1][0], 0.8)

    def test_repeated_strong_onsets_on_tracked_pitch_keep_syllables(self):
        data = output(300)
        voice(data, 60, 0, 300)
        for frame in (0, 100, 200):
            attack(data, 60, frame)
        events, quality = track(data)
        self.assert_pitches(events, [60, 60, 60])
        self.assertEqual(quality["repeated_onsets"], 2)
        self.assertAlmostEqual(events[0][1], 1)
        self.assertAlmostEqual(events[1][1], 2)

    def test_onset_plateau_is_one_attack_not_many_fragments(self):
        data = output(240)
        voice(data, 60, 0, 240)
        data["onset"][100:112, 60 - 21] = 0.9
        events, quality = track(data)
        self.assert_pitches(events, [60, 60])
        self.assertEqual(quality["repeated_onsets"], 1)

    def test_fast_supported_repeated_syllables_are_not_merged(self):
        data = output(60)
        voice(data, 60, 0, 60)
        for frame in (0, 10, 20, 30, 40, 50):
            attack(data, 60, frame)
        events, quality = track(data)
        self.assert_pitches(events, [60] * 6)
        self.assertEqual(quality["repeated_onsets"], 5)
        self.assertTrue(all(abs(event[1] - event[0] - 0.1) < 0.0000001 for event in events))

    def test_articulated_fifty_ms_pitch_changes_are_not_deleted(self):
        data = output(100)
        for begin, pitch in zip((0, 5, 10, 15), (60, 64, 67, 65)):
            voice(data, pitch, begin, begin + 5, strength=0.85)
            attack(data, pitch, begin)
        events, quality = track(data)
        self.assert_pitches(events, [60, 64, 67, 65])
        self.assertTrue(all(abs(event[1] - event[0] - 0.05) < 0.0000001 for event in events))
        self.assertEqual(quality["preserved_short_notes"], 4)
        self.assertEqual(quality["ignored_short_runs"], 0)
        self.assertAlmostEqual(quality["voiced_seconds"], 0.2)

    def test_sixty_ms_repeated_attacks_keep_fast_syllable_timing(self):
        data = output(60)
        voice(data, 60, 0, 60)
        for frame in range(0, 60, 6):
            attack(data, 60, frame)
        events, quality = track(data)
        self.assert_pitches(events, [60] * 10)
        self.assertEqual(quality["repeated_onsets"], 9)
        self.assertEqual(quality["preserved_short_notes"], 10)
        self.assertTrue(all(abs(event[1] - event[0] - 0.06) < 0.0000001 for event in events))

    def test_unattacked_short_change_keeps_gap_instead_of_extending_old_pitch(self):
        data = output(100)
        voice(data, 60, 0, 40)
        voice(data, 67, 40, 45, strength=0.9)
        voice(data, 60, 45, 100)
        events, quality = track(data)
        self.assert_pitches(events, [60, 60])
        self.assertAlmostEqual(events[0][1], 0.40)
        self.assertAlmostEqual(events[1][0], 0.45)
        self.assertEqual(quality["ignored_short_runs"], 1)
        self.assertAlmostEqual(quality["discarded_short_seconds"], 0.05)

    def test_weak_short_attack_is_not_promoted_to_a_real_note(self):
        data = output(100)
        voice(data, 60, 20, 25, strength=0.35)
        attack(data, 60, 20)
        events, quality = track(data)
        self.assertEqual(events, [])
        self.assertEqual(quality["preserved_short_notes"], 0)

    def test_repeated_discarded_runs_report_duration_without_filling_rest(self):
        data = output(200)
        voice(data, 60, 0, 200)
        for begin in (30, 80, 130):
            data["note"][begin:begin + 5, 60 - 21] = 0
            data["contour"][begin:begin + 5, (60 - 21) * 3 - 1:(60 - 21) * 3 + 2] = 0
            voice(data, 67, begin, begin + 5, strength=0.9)
        events, quality = track(data)
        self.assert_pitches(events, [60] * 4)
        self.assertEqual(quality["bridged_gaps"], 0)
        self.assertAlmostEqual(quality["discarded_short_seconds"], 0.15)
        self.assertTrue(any("已保留其间隙" in warning for warning in quality["warnings"]))
        midi = transcription.create_melody_midi(events, "短段不确定", quality)
        self.assertIn("短音未定", midi.instruments[0].name)

    def test_real_long_rest_is_not_filled_and_resets_pitch(self):
        data = output(250)
        voice(data, 60, 0, 80)
        voice(data, 79, 170, 250)
        events, quality = track(data)
        self.assert_pitches(events, [60, 79])
        self.assertAlmostEqual(events[0][1], 0.8)
        self.assertAlmostEqual(events[1][0], 1.7)
        self.assertAlmostEqual(quality["voiced_ratio"], 0.64)
        self.assertEqual(quality["bridged_gaps"], 0)

    def test_short_missed_frames_join_only_the_same_supported_pitch(self):
        data = output(200)
        voice(data, 60, 0, 80)
        voice(data, 60, 83, 200)
        events, quality = track(data)
        self.assert_pitches(events, [60])
        self.assertAlmostEqual(events[0][1], 2)
        self.assertEqual(quality["bridged_gaps"], 1)

    def test_short_staccato_rest_with_new_attack_stays_a_rest(self):
        data = output(200)
        voice(data, 60, 0, 80)
        voice(data, 60, 83, 200)
        attack(data, 60, 83)
        events, quality = track(data)
        self.assert_pitches(events, [60, 60])
        self.assertAlmostEqual(events[0][1], 0.8)
        self.assertAlmostEqual(events[1][0], 0.83)
        self.assertEqual(quality["bridged_gaps"], 0)

    def test_short_gap_between_different_pitches_is_not_joined(self):
        data = output(200)
        voice(data, 60, 0, 80)
        voice(data, 64, 83, 200)
        events, quality = track(data)
        self.assert_pitches(events, [60, 64])
        self.assertAlmostEqual(events[0][1], 0.8)
        self.assertAlmostEqual(events[1][0], 0.83)
        self.assertEqual(quality["bridged_gaps"], 0)

    def test_competing_strong_attack_cannot_be_filled_as_the_old_pitch(self):
        data = output(200)
        voice(data, 60, 0, 80)
        voice(data, 67, 80, 83)
        voice(data, 60, 83, 200)
        attack(data, 67, 80)
        events, quality = track(data)
        self.assert_pitches(events, [60, 60])
        self.assertAlmostEqual(events[0][1], 0.8)
        self.assertAlmostEqual(events[1][0], 0.83)
        self.assertEqual(quality["bridged_gaps"], 0)

    def test_low_confidence_is_not_fabricated_into_notes(self):
        data = output(200)
        voice(data, 60, 0, 200, strength=0.1)
        events, quality = track(data)
        self.assertEqual(events, [])
        self.assertEqual(quality["voiced_seconds"], 0)
        self.assertEqual(quality["median_confidence"], 0)
        self.assertIn("未识别到稳定主旋律", quality["warnings"][0])

    def test_isolated_one_frame_spike_is_not_a_stable_note(self):
        data = output(200)
        voice(data, 60, 100, 101, strength=0.9)
        events, _ = track(data)
        self.assertEqual(events, [])

    def test_contour_support_can_prefer_fundamental_over_louder_octave(self):
        data = output(200)
        voice(data, 60, 0, 200, strength=0.70, contour=0.85)
        voice(data, 72, 0, 200, strength=0.85, contour=0.20)
        events, _ = track(data)
        self.assert_pitches(events, [60])

    def test_supplied_nonuniform_model_time_axis_is_respected(self):
        data = output(200)
        voice(data, 60, 0, 200)
        times = np.arange(200) * 0.012
        times[172:] -= 0.01
        events, _ = track(data, times=times)
        self.assert_pitches(events, [60])
        self.assertAlmostEqual(events[0][1], times[-1] + 0.012)

    def test_audio_boundary_clips_estimate_and_never_exceeds_duration(self):
        data = output(200)
        voice(data, 60, 0, 200)
        events, quality = track(data, duration=1.25)
        self.assert_pitches(events, [60])
        self.assertAlmostEqual(events[0][1], 1.25)
        self.assertEqual(quality["voiced_ratio"], 1)

    def test_audio_boundary_does_not_turn_a_supported_short_note_into_twenty_ms(self):
        data = output(10)
        voice(data, 60, 0, 5)
        attack(data, 60, 0)
        events, quality = track(data, duration=0.02)
        self.assertEqual(events, [])
        self.assertEqual(quality["preserved_short_notes"], 0)
        self.assertAlmostEqual(quality["discarded_short_seconds"], 0.02)

    def test_inputs_are_not_modified(self):
        data = output(200)
        voice(data, 60, 0, 200)
        snapshot = {key: value.copy() for key, value in data.items()}
        track(data)
        for key, value in snapshot.items():
            np.testing.assert_array_equal(value, data[key])

    def test_invalid_probabilities_shapes_and_times_fail_explicitly(self):
        data = output(10)
        data["note"][0, 0] = np.nan
        with self.assertRaises(ValueError):
            track(data)
        data = output(10)
        data["onset"][0, 0] = 1.1
        with self.assertRaises(ValueError):
            track(data)
        data = output(10)
        data["contour"] = np.zeros((10, 88))
        with self.assertRaises(ValueError):
            track(data)
        with self.assertRaises(ValueError):
            track(output(10), times=np.zeros(10))
        with self.assertRaises(ValueError):
            track(output(10), duration=float("inf"))

    def test_ambiguous_polyphony_produces_review_warning(self):
        data = output(200)
        voice(data, 60, 0, 200, strength=0.8)
        voice(data, 67, 0, 200, strength=0.75)
        events, quality = track(data)
        self.assert_pitches(events, [60])
        self.assertGreater(quality["ambiguous_frame_ratio"], 0.9)
        self.assertTrue(any("伴奏与旋律" in warning for warning in quality["warnings"]))

    def test_midi_is_one_monophonic_track_with_legacy_marker(self):
        import pretty_midi
        midi = transcription.create_melody_midi([(0, 1, 60, 0.8, None), (1.2, 2, 64, 0.7, None)], "模拟歌曲")
        self.assertEqual(len(midi.instruments), 1)
        self.assertTrue(midi.instruments[0].name.endswith(" · 音频自动转录"))
        self.assertIn("需试听", midi.instruments[0].name)
        self.assertEqual(midi.instruments[0].pitch_bends, [])
        buffer = io.BytesIO()
        midi.write(buffer)
        buffer.seek(0)
        restored = pretty_midi.PrettyMIDI(buffer, charset="utf-8")
        self.assertEqual(restored.instruments[0].name, midi.instruments[0].name)
        self.assertEqual(len(restored.instruments[0].notes), 2)
        with self.assertRaises(ValueError):
            transcription.create_melody_midi([], "空结果不能虚报成功")
        with self.assertRaises(ValueError):
            transcription.create_melody_midi([(0, 2, 60, 0.8, None), (1, 3, 64, 0.7, None)], "重叠结果不能成为单旋律")

    def test_cli_quality_warning_and_empty_failure_without_inference_or_writes(self):
        import pretty_midi
        data = output(200)
        voice(data, 60, 0, 200)
        voice(data, 67, 0, 200, strength=0.75)
        basic = types.ModuleType("basic_pitch")
        basic.ICASSP_2022_MODEL_PATH = "mock-model"
        inference = types.ModuleType("basic_pitch.inference")
        inference.run_inference = lambda *_: data
        creation = types.ModuleType("basic_pitch.note_creation")
        creation.model_frames_to_time = lambda frames: np.arange(frames) * 0.01
        fake_soundfile = types.ModuleType("soundfile")
        fake_soundfile.info = lambda _: types.SimpleNamespace(duration=2.0)
        modules = {"basic_pitch": basic, "basic_pitch.inference": inference,
                   "basic_pitch.note_creation": creation, "soundfile": fake_soundfile}
        with patch.dict(sys.modules, modules), patch.object(sys, "argv", ["audio-to-midi.py", "mock-input.wav", "mock-output.mid"]), \
                patch.object(transcription.os.path, "isfile", return_value=True), patch.object(transcription.os.path, "getsize", return_value=1024), \
                patch.object(pretty_midi.PrettyMIDI, "write") as write:
            log = io.StringIO()
            with contextlib.redirect_stdout(log):
                transcription.main()
            write.assert_called_once_with("mock-output.mid")
            self.assertIn("连续主旋律估计", log.getvalue())
            self.assertIn("伴奏与旋律", log.getvalue())
            self.assertIn("需试听确认", log.getvalue())
            self.assertIn("转录提示：", log.getvalue())
            write.reset_mock()
            inference.run_inference = lambda *_: output(200)
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, "未识别到稳定主旋律"):
                transcription.main()
            write.assert_not_called()

    def test_quality_tags_persist_in_track_name_without_losing_legacy_marker(self):
        import pretty_midi
        quality = {"short_note_ratio": 0.5, "ambiguous_frame_ratio": 0.5,
                   "median_confidence": 0.3, "bass_competition_ratio": 0.25}
        midi = transcription.create_melody_midi([(0, 1, 60, 0.8, None)], "长标题" * 80, quality)
        name = midi.instruments[0].name
        self.assertLessEqual(len(name), 120)
        for tag in ("短音较多", "伴奏难区分", "旋律依据弱", "低音可能干扰"):
            self.assertIn(tag, name)
        self.assertTrue(name.endswith(" · 音频自动转录"))
        buffer = io.BytesIO()
        midi.write(buffer)
        buffer.seek(0)
        restored = pretty_midi.PrettyMIDI(buffer, charset="utf-8")
        self.assertEqual(restored.instruments[0].name, name)


if __name__ == "__main__":
    unittest.main(verbosity=2)
