import unittest

from app.models import AlignmentConfig, LrcLine
from app.validator import validate
from app.vocal_boundaries import constrain_to_stage_vocals


class StageVocalBoundaryTests(unittest.TestCase):
    def test_long_word_cannot_jump_from_its_release_to_a_later_phrase(self):
        line = LrcLine(24.9, "an der Wand", "", words=[
            {"word": "an", "start": 24.9, "end": 25.2,
             "timing_source": "stable-ts-whisper"},
            {"word": "der", "start": 25.2, "end": 25.7,
             "timing_source": "stable-ts-whisper"},
            {"word": "Wand", "start": 25.7, "end": 28.38,
             "timing_source": "stable-ts-whisper"},
        ])

        report = constrain_to_stage_vocals(
            [line], [(24.74, 25.99), (28.64, 30.24)])

        self.assertEqual(1, report["release_corrections"])
        self.assertAlmostEqual(26.03, line.words[-1]["end"])
        self.assertEqual(2350.0, line.words[-1]["stage_vocal_release_trim_ms"])

    def test_onset_at_dying_previous_island_is_a_quality_conflict(self):
        line = LrcLine(30.215, "Von schnellen Autos", "", words=[
            {"word": "Von", "start": 30.215, "end": 31.40,
             "timing_source": "stable-ts-whisper"},
            {"word": "schnellen", "start": 31.40, "end": 32.0,
             "timing_source": "stable-ts-whisper"},
        ], source_timestamp=30.2)

        report = constrain_to_stage_vocals(
            [line], [(28.64, 30.24), (31.29, 32.2)])
        quality = validate([line], AlignmentConfig())

        self.assertEqual(1, report["onset_conflicts"])
        self.assertEqual(1075.0, line.words[0]["stage_vocal_onset_conflict_ms"])
        self.assertFalse(quality["quality"]["publishable"])
        self.assertEqual(1, quality["quality"]["stage_vocal_onset_conflicts"])

    def test_supported_held_word_is_not_trimmed(self):
        line = LrcLine(10.0, "Feuer", "", words=[
            {"word": "Feuer", "start": 10.0, "end": 12.45,
             "timing_source": "ctc-phoneme-alignment"},
        ])

        report = constrain_to_stage_vocals([line], [(9.98, 12.50)])

        self.assertEqual(0, report["release_corrections"])
        self.assertEqual(12.45, line.words[0]["end"])

    def test_sub_perceptual_vad_edge_is_not_an_onset_conflict(self):
        line = LrcLine(31.26, "Von schnellen Autos", "", words=[
            {"word": "Von", "start": 31.26, "end": 31.5,
             "timing_source": "input-enhanced-lrc"},
        ])

        report = constrain_to_stage_vocals(
            [line], [(31.30, 31.8)])

        self.assertEqual(0, report["onset_conflicts"])
        self.assertNotIn("stage_vocal_onset_conflict_ms", line.words[0])


if __name__ == "__main__":
    unittest.main()
