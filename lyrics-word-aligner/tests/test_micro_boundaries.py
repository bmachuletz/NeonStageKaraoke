import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from app.micro_boundaries import (
    VoicingTrack,
    _phone_class,
    analyze_voicing,
    compare_timing_reference,
    refine_ipa_phone_path,
    refine_sustain_releases_with_voicing,
    serialize_voicing_evidence,
    sustain_voicing_intervals,
)


class MicroBoundaryTests(unittest.TestCase):
    def test_pyin_track_is_serialized_as_independent_f0_evidence(self):
        track = VoicingTrack(
            times=np.array([.1, .2]), f0=np.array([220.0, np.nan]),
            probability=np.array([.9, .1]), voiced=np.array([True, False]),
            sample_rate=16000, frame_length=1024, hop_length=160)
        report = serialize_voicing_evidence(track, {"enabled": True})
        self.assertEqual("pyin", report["family"])
        self.assertEqual(220.0, report["track"][0]["f0_hz"])
        self.assertIsNone(report["track"][1]["f0_hz"])

    def test_phone_classes_distinguish_singing_boundary_types(self):
        self.assertEqual("plosive", _phone_class("t"))
        self.assertEqual("fricative", _phone_class("ʃ"))
        self.assertEqual("vowel", _phone_class("aː"))
        self.assertEqual("nasal", _phone_class("ŋ"))
        self.assertEqual("liquid", _phone_class("l"))

    def test_pyin_timebase_uses_exact_unpadded_frame_centers(self):
        sample_rate = 16000
        time = np.arange(sample_rate, dtype=np.float32) / sample_rate
        audio = (0.25 * np.sin(2 * np.pi * 220 * time)).astype(np.float32)

        track, summary = analyze_voicing(audio, sample_rate)

        self.assertIsNotNone(track)
        self.assertFalse(summary["center_padding"])
        self.assertEqual("absolute-source-samples", summary["timeline"])
        self.assertAlmostEqual(1024 / 2 / sample_rate, track.times[0], places=6)
        self.assertEqual(160, track.hop_length)

    def test_pitch_analysis_is_limited_to_plausible_sustain_windows(self):
        lines = [
            SimpleNamespace(words=[
                {"word": "kurz", "start": .2, "end": .4},
                {"word": "lang", "start": .5, "end": 1.1},
            ]),
            SimpleNamespace(words=[
                {"word": "weiter", "start": 2.0, "end": 2.5,
                 "acoustic_end": 2.2},
            ]),
        ]

        intervals = sustain_voicing_intervals(lines, 3.0)

        self.assertEqual(2, len(intervals))
        self.assertGreaterEqual(intervals[0][0], .9)
        self.assertLessEqual(intervals[0][1], 1.58)
        self.assertGreaterEqual(intervals[1][0], 2.0)

    def test_better_monotone_micro_path_moves_phone_onsets_in_select_mode(self):
        aligned = [{
            "word": "Test", "start": .20, "end": .80, "confidence": .9,
            "phonemes": [
                {"phone": "t", "start": .20, "end": .49},
                {"phone": "ɛ", "start": .50, "end": .80},
            ],
        }]

        def evidence(_signal, time, _phone_class_value, _voicing, _sample_rate):
            return .92 if min(abs(time - .22), abs(time - .53)) < .003 else .20

        with patch("app.micro_boundaries._multires_boundary_score", side_effect=evidence):
            report = refine_ipa_phone_path(
                np.zeros(16000, dtype=np.float32), aligned, None, mode="select")

        self.assertEqual(1, report["applied_words"])
        self.assertEqual(2, report["moved_phone_onsets"])
        self.assertAlmostEqual(.22, aligned[0]["start"], delta=.003)
        self.assertAlmostEqual(.53, aligned[0]["phonemes"][1]["start"], delta=.003)

    def test_shadow_mode_reports_but_does_not_change_phone_path(self):
        aligned = [{
            "word": "Test", "start": .20, "end": .80, "confidence": .9,
            "phonemes": [
                {"phone": "t", "start": .20, "end": .49},
                {"phone": "ɛ", "start": .50, "end": .80},
            ],
        }]

        def evidence(_signal, time, _phone_class_value, _voicing, _sample_rate):
            return .92 if min(abs(time - .22), abs(time - .53)) < .003 else .20

        with patch("app.micro_boundaries._multires_boundary_score", side_effect=evidence):
            report = refine_ipa_phone_path(
                np.zeros(16000, dtype=np.float32), aligned, None, mode="shadow")

        self.assertEqual(1, report["candidate_words"])
        self.assertEqual(0, report["applied_words"])
        self.assertEqual(.20, aligned[0]["start"])
        self.assertEqual(.50, aligned[0]["phonemes"][1]["start"])

    def test_connected_voicing_can_extend_a_held_vowel_release(self):
        times = np.arange(0, 2.0, .005, dtype=np.float64)
        probability = np.zeros_like(times, dtype=np.float32)
        probability[(times >= .90) & (times <= 1.48)] = .86
        track = VoicingTrack(
            times=times, f0=np.full_like(times, 220, dtype=np.float32),
            probability=probability, voiced=probability >= .38,
            sample_rate=16000, frame_length=1024, hop_length=80)
        line = SimpleNamespace(words=[{
            "word": "lang", "start": .7, "acoustic_end": 1.0,
            "end": 1.28, "sustain_extension_ms": 280,
        }])

        report = refine_sustain_releases_with_voicing([line], track, mode="select")

        self.assertEqual(1, report["applied_words"])
        self.assertGreater(line.words[0]["end"], 1.48)
        self.assertEqual(1.28, line.words[0]["pyin_release_original"])

    def test_internal_word_cannot_extend_past_independent_vocal_activity(self):
        times = np.arange(0, 2.5, .005, dtype=np.float64)
        probability = np.zeros_like(times, dtype=np.float32)
        probability[(times >= .90) & (times <= 1.48)] = .86
        track = VoicingTrack(
            times=times, f0=np.full_like(times, 220, dtype=np.float32),
            probability=probability, voiced=probability >= .38,
            sample_rate=16000, frame_length=1024, hop_length=80)
        line = SimpleNamespace(words=[
            {"word": "brennenden", "start": .7, "acoustic_end": 1.0,
             "end": 1.28, "sustain_extension_ms": 280,
             "sustain_activity_end": 1.10},
            {"word": "Barrikaden", "start": 1.8, "end": 2.2},
        ])

        report = refine_sustain_releases_with_voicing(
            [line], track, mode="select")

        self.assertEqual(0, report["applied_words"])
        self.assertEqual(1.28, line.words[0]["end"])
        self.assertEqual("internal-release-beyond-vocal-activity",
                         report["details"][0]["reason"])

    def test_reference_report_makes_boundary_changes_measurable(self):
        current = [SimpleNamespace(words=[
            {"word": "Hallo", "start": 1.02, "end": 1.42},
            {"word": "Welt", "start": 1.51, "end": 1.90},
        ])]
        reference = SimpleNamespace(lines=[SimpleNamespace(words=[
            {"word": "Hallo", "start": 1.00, "end": 1.40},
            {"word": "Welt", "start": 1.50, "end": 1.88},
        ])])

        report = compare_timing_reference(current, reference)

        self.assertTrue(report["comparable"])
        self.assertEqual(2, report["words"])
        self.assertEqual(15.0, report["median_absolute_start_delta_ms"])
        self.assertEqual(0, report["boundaries_changed_over_20ms"])


if __name__ == "__main__":
    unittest.main()
