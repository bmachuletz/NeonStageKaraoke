import unittest

import numpy as np

from app.consensus import (eliminate_remaining_line_overlaps, extend_final_word_sustains, reconcile_acoustic_boundaries,
                           stabilize_acoustic_display_durations)
from app.models import LrcLine


class ConsensusTests(unittest.TestCase):
    def test_extends_held_line_end_through_vocal_release(self):
        lines = [
            LrcLine(36.8, "on fire", "", words=[
                {"word": "on", "start": 40.2, "end": 40.42,
                 "timing_source": "stable-ts-whisper"},
                {"word": "fire", "start": 40.42, "end": 40.96,
                 "timing_source": "stable-ts-whisper"},
            ]),
            LrcLine(42.0, "next line", "", words=[
                {"word": "next", "start": 42.0, "end": 42.3,
                 "timing_source": "stable-ts-whisper"},
            ]),
        ]

        result = extend_final_word_sustains(lines, [(40.3, 41.28)])

        self.assertEqual(1, result["adjusted_words"])
        self.assertEqual(41.28, lines[0].words[-1]["end"])
        self.assertEqual(40.96, lines[0].words[-1]["acoustic_end"])

    def test_sustain_never_reaches_next_line(self):
        lines = [
            LrcLine(1.0, "held", "", words=[
                {"word": "held", "start": 1.0, "end": 1.5,
                 "timing_source": "qwen-forced"}]),
            LrcLine(2.0, "next", "", words=[
                {"word": "next", "start": 2.0, "end": 2.3,
                 "timing_source": "qwen-forced"}]),
        ]
        extend_final_word_sustains(lines, [(1.1, 2.2)])
        self.assertEqual(1.88, lines[0].words[-1]["end"])

    def test_sustain_ignores_activity_continuing_into_later_phrases(self):
        lines = [
            LrcLine(83.0, "fried", "", words=[
                {"word": "fried", "start": 87.9, "end": 88.328,
                 "timing_source": "stable-ts-whisper"}]),
            LrcLine(88.55, "next", "", words=[
                {"word": "next", "start": 88.55, "end": 88.9,
                 "timing_source": "stable-ts-whisper"}]),
        ]

        result = extend_final_word_sustains(lines, [(83.0, 108.28)])

        self.assertEqual(0, result["adjusted_words"])
        self.assertEqual(88.328, lines[0].words[-1]["end"])

    def test_extends_internal_held_word_only_when_activity_ends_before_next_word(self):
        line = LrcLine(1.0, "zieh lang weiter", "", words=[
            {"word": "zieh", "start": 1.0, "end": 1.25, "timing_source": "qwen-forced"},
            {"word": "lang", "start": 1.3, "end": 1.7, "timing_source": "qwen-forced"},
            {"word": "weiter", "start": 2.5, "end": 2.9, "timing_source": "qwen-forced"},
        ])

        result = extend_final_word_sustains([line], [(1.25, 2.1)])

        self.assertEqual(1, result["adjusted_words"])
        self.assertEqual(2.1, line.words[1]["end"])
        self.assertEqual(1.7, line.words[1]["acoustic_end"])

    def test_quiet_tonal_release_extends_beyond_global_activity_limit(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 5, dtype=np.float32)
        start = int(0.75 * sample_rate)
        stop = int(3.1 * sample_rate)
        time = np.arange(stop - start) / sample_rate
        envelope = np.linspace(0.22, 0.018, stop - start)
        audio[start:stop] = envelope * np.sin(2 * np.pi * 220 * time)
        lines = [
            LrcLine(0.8, "la", "", words=[
                {"word": "la", "start": 0.8, "end": 1.15,
                 "timing_source": "stable-ts-whisper"}]),
            LrcLine(3.6, "next", "", words=[
                {"word": "next", "start": 3.6, "end": 3.9,
                 "timing_source": "stable-ts-whisper"}]),
        ]

        result = extend_final_word_sustains(lines, [], audio=audio)

        self.assertEqual(1, result["adjusted_words"])
        self.assertGreater(lines[0].words[0]["end"], 3.0)
        self.assertLessEqual(lines[0].words[0]["end"], 3.48)
        self.assertEqual("local-tonal-sustain-release-v4", result["method"])

    def test_broadband_separator_noise_does_not_become_a_sustain(self):
        generator = np.random.default_rng(7)
        sample_rate = 16000
        audio = np.zeros(sample_rate * 4, dtype=np.float32)
        voiced_start, voiced_stop = int(.75 * sample_rate), int(1.2 * sample_rate)
        time = np.arange(voiced_stop - voiced_start) / sample_rate
        audio[voiced_start:voiced_stop] = .2 * np.sin(2 * np.pi * 220 * time)
        audio[int(1.2 * sample_rate):int(3.0 * sample_rate)] = (
            generator.normal(0, .004, int(1.8 * sample_rate)).astype(np.float32))
        line = LrcLine(.8, "la", "", words=[
            {"word": "la", "start": .8, "end": 1.15,
             "timing_source": "stable-ts-whisper"}])

        extend_final_word_sustains([line], [], audio=audio)

        self.assertLess(line.words[0]["end"], 1.4)

    def test_display_floor_preserves_acoustic_measurement(self):
        line = LrcLine(1.0, "a", "", words=[
            {"word": "a", "start": 1.0, "end": 1.02,
             "timing_source": "ctc-phoneme-alignment"}
        ])
        summary = stabilize_acoustic_display_durations([line])
        self.assertEqual(1, summary["adjusted_words"])
        self.assertEqual(1.02, line.words[0]["acoustic_end"])
        self.assertEqual(1.04, line.words[0]["end"])

    def test_display_floor_does_not_upgrade_heuristic_timing(self):
        line = LrcLine(1.0, "a", "", words=[
            {"word": "a", "start": 1.0, "end": 1.01,
             "timing_source": "vocal-activity-repair"}
        ])
        summary = stabilize_acoustic_display_durations([line])
        self.assertEqual(0, summary["adjusted_words"])
        self.assertEqual(1.01, line.words[0]["end"])

    def test_reconciles_small_overlap_between_acoustic_words(self):
        lines = [
            LrcLine(1, "eins", "", words=[
                {"word": "eins", "start": 1.0, "end": 2.2, "timing_source": "ctc-phoneme-alignment"}]),
            LrcLine(2, "zwei", "", words=[
                {"word": "zwei", "start": 2.0, "end": 2.8, "timing_source": "qwen-forced"}]),
        ]

        result = reconcile_acoustic_boundaries(lines)

        self.assertEqual(1, result["adjusted_boundaries"])
        self.assertEqual(2.1, lines[0].words[0]["end"])
        self.assertEqual(2.1, lines[1].words[0]["start"])

    def test_snaps_heuristic_tail_to_verified_acoustic_onset(self):
        lines = [
            LrcLine(1, "eins", "", words=[
                {"word": "eins", "start": 1.0, "end": 2.0, "timing_source": "vocal-activity-repair"}]),
            LrcLine(2, "zwei", "", words=[
                {"word": "zwei", "start": 1.8, "end": 2.8, "timing_source": "ctc-phoneme-alignment"}]),
        ]

        result = reconcile_acoustic_boundaries(lines)

        self.assertEqual(1, result["adjusted_boundaries"])
        self.assertEqual(1.8, lines[0].words[0]["end"])
        self.assertEqual(1.8, lines[1].words[0]["start"])
        self.assertEqual("trim-heuristic-tail", result["adjustments"][0]["mode"])

    def test_does_not_hide_large_heuristic_overlap(self):
        lines = [
            LrcLine(1, "eins", "", words=[
                {"word": "eins", "start": 1.0, "end": 3.0,
                 "timing_source": "vocal-activity-repair"}]),
            LrcLine(2, "zwei", "", words=[
                {"word": "zwei", "start": 1.8, "end": 2.8,
                 "timing_source": "ctc-phoneme-alignment"}]),
        ]

        result = reconcile_acoustic_boundaries(lines)

        self.assertEqual(0, result["adjusted_boundaries"])
        self.assertEqual(1, result["rejected_boundaries"])

    def test_final_fallback_keeps_next_lead_onset_and_removes_overlap(self):
        lines = [
            LrcLine(204.5, "century digital boy", "", words=[
                {"word": "century", "start": 206.02, "end": 206.98,
                 "timing_source": "ctc-overlap-reanalysis"},
                {"word": "digital", "start": 206.98, "end": 207.86,
                 "timing_source": "ctc-overlap-reanalysis"},
                {"word": "boy", "start": 207.86, "end": 208.02,
                 "timing_source": "ctc-overlap-reanalysis"},
            ]),
            LrcLine(206.97, "I don't know", "", words=[
                {"word": "I", "start": 206.97, "end": 207.01,
                 "timing_source": "sofa-singing-alignment"},
            ]),
        ]

        result = eliminate_remaining_line_overlaps(lines)

        self.assertEqual(1, result["adjusted_pairs"])
        self.assertEqual(206.97, lines[0].words[-1]["end"])
        self.assertEqual(206.97, lines[1].words[0]["start"])
        self.assertTrue(all(word["end"] <= 206.97 for word in lines[0].words))
        self.assertTrue(all(word["timing_source"] == "overlap-display-lane-fallback"
                            for word in lines[0].words))
        self.assertTrue(all(left["end"] <= right["start"]
                            for left, right in zip(lines[0].words, lines[0].words[1:])))

    def test_final_fallback_keeps_many_compressed_words_monotonic(self):
        previous = LrcLine(66.954, "It's going ya ya ya ya ya ya ya", "", words=[
            {"word": token, "start": 66.954 + index * .55,
             "end": 67.354 + index * .55, "timing_source": "qwen-forced"}
            for index, token in enumerate("It's going ya ya ya ya ya ya ya".split())
        ])
        current = LrcLine(67.331, "Oh yeah", "", words=[
            {"word": "Oh", "start": 67.331, "end": 67.389,
             "timing_source": "qwen-forced"},
            {"word": "yeah", "start": 67.389, "end": 71.77,
             "timing_source": "qwen-forced"},
        ])

        eliminate_remaining_line_overlaps([previous, current])

        self.assertEqual(67.331, previous.words[-1]["end"])
        self.assertTrue(all(left["end"] <= right["start"]
                            for left, right in zip(previous.words, previous.words[1:])))
        self.assertTrue(all(word["end"] > word["start"] for word in previous.words))


if __name__ == "__main__":
    unittest.main()
