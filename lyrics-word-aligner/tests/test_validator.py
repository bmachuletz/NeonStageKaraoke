import unittest

from app.models import AlignmentConfig, LrcLine
from app.validator import validate


class ValidatorTests(unittest.TestCase):
    def test_qwen_word_at_preroll_edge_is_unverified_and_reduces_coverage(self):
        line = LrcLine(59.34, "Lichtenhagen NSU", "", words=[
            {"word": "Lichtenhagen", "start": 59.34, "end": 60.46,
             "timing_source": "qwen-forced", "window_edge_fallback": True},
            {"word": "NSU", "start": 61.10, "end": 62.94,
             "timing_source": "qwen-forced"},
        ], source_timestamp=59.79)

        result = validate([line], AlignmentConfig())

        self.assertEqual("uncertain", line.status)
        self.assertIn("Analysefensterrand", line.reason)
        self.assertEqual(1, result["quality"]["heuristically_placed_words"])
        self.assertEqual(0.5, result["quality"]["acoustically_aligned_word_coverage"])

    def test_adjacent_decoder_frame_words_are_reported_as_compressed_run(self):
        line = LrcLine(1.0, "und einer", "", words=[
            {"word": "und", "start": 1.0, "end": 1.02,
             "timing_source": "qwen-forced"},
            {"word": "einer", "start": 1.025, "end": 1.05,
             "timing_source": "qwen-forced"},
        ])

        result = validate([line], AlignmentConfig())

        self.assertEqual(1, result["quality"]["compressed_word_runs"])
        self.assertEqual("uncertain", line.status)
        self.assertIn("komprimiert", line.reason)

    def test_fast_short_words_with_acoustic_gaps_are_not_decoder_collapse(self):
        line = LrcLine(1.0, "it up and", "", words=[
            {"word": "it", "start": 1.0, "end": 1.04,
             "timing_source": "ctc-phoneme-alignment"},
            {"word": "up", "start": 1.18, "end": 1.22,
             "timing_source": "ctc-phoneme-alignment"},
            {"word": "and", "start": 1.31, "end": 1.39,
             "timing_source": "ctc-phoneme-alignment"},
        ], source_timestamp=1.0)

        result = validate([line], AlignmentConfig())

        self.assertEqual(0, result["quality"]["compressed_word_runs"])
        self.assertEqual("ok", line.status)

    def test_clean_global_alignment_is_publishable(self):
        lines = [
            LrcLine(10.0, "Hallo Welt", "", words=[
                {"word": "Hallo", "start": 10.0, "end": 10.4},
                {"word": "Welt", "start": 10.45, "end": 10.9},
            ], source_timestamp=10.0),
        ]

        result = validate(lines, AlignmentConfig())

        self.assertEqual("excellent", result["quality"]["grade"])
        self.assertTrue(result["quality"]["publishable"])

    def test_geometric_repairs_reduce_quality(self):
        lines = [
            LrcLine(float(index), "Wort", "", words=[
                {"word": "Wort", "start": float(index), "end": float(index) + 0.2,
                 "timing_source": "geometric-repair" if index < 2 else "qwen-forced"},
            ], source_timestamp=float(index))
            for index in range(4)
        ]

        result = validate(lines, AlignmentConfig(), repaired_lines=2)

        self.assertEqual("reject", result["quality"]["grade"])
        self.assertFalse(result["quality"]["publishable"])

    def test_replaced_historical_repairs_do_not_penalize_final_alignment(self):
        lines = [
            LrcLine(float(index), "Wort", "", words=[
                {"word": "Wort", "start": float(index), "end": float(index) + 0.2,
                 "timing_source": "qwen-forced"},
            ], source_timestamp=float(index))
            for index in range(4)
        ]

        result = validate(lines, AlignmentConfig(), repaired_lines=4)

        self.assertEqual(0, result["quality"]["geometrically_repaired_lines"])
        self.assertTrue(result["quality"]["publishable"])

    def test_global_timestamp_is_checked_against_original_lrc(self):
        line = LrcLine(15.0, "Hallo", "", words=[
            {"word": "Hallo", "start": 15.0, "end": 15.4},
        ], source_timestamp=10.0)

        result = validate([line], AlignmentConfig(max_start_deviation=2.0))

        self.assertEqual(1, result["uncertain"])
        self.assertIn("LRC-Zeitpunkt", line.reason)

    def test_bounded_acoustic_repair_may_override_rough_lrc_timestamp(self):
        line = LrcLine(15.0, "Hallo", "", words=[
            {"word": "Hallo", "start": 15.0, "end": 15.4,
             "timing_source": "vocal-activity-repair"},
        ], source_timestamp=10.0)

        result = validate([line], AlignmentConfig(max_start_deviation=2.0))

        self.assertEqual(0, result["uncertain"])
        self.assertFalse(result["quality"]["publishable"])
        self.assertEqual(0.0, result["quality"]["acoustically_aligned_word_coverage"])

    def test_vocal_activity_placement_is_not_mistaken_for_word_alignment(self):
        lines = [LrcLine(1.0, "Nur ungefähr", "", words=[
            {"word": "Nur", "start": 1.0, "end": 1.3,
             "timing_source": "vocal-activity-repair"},
            {"word": "ungefähr", "start": 1.3, "end": 2.0,
             "timing_source": "vocal-activity-repair"},
        ], source_timestamp=1.0)]

        result = validate(lines, AlignmentConfig())

        self.assertEqual(2, result["quality"]["heuristically_placed_words"])
        self.assertFalse(result["quality"]["publishable"])

    def test_overlapping_acoustic_lines_are_never_publishable(self):
        lines = [
            LrcLine(10.0, "Lead vocal", "", words=[
                {"word": "Lead", "start": 10.0, "end": 11.0,
                 "timing_source": "qwen-forced"},
                {"word": "vocal", "start": 11.0, "end": 13.0,
                 "timing_source": "qwen-forced"},
            ], source_timestamp=10.0),
            LrcLine(11.0, "Backing vocal", "", words=[
                {"word": "Backing", "start": 11.0, "end": 11.5,
                 "timing_source": "easyaligner-global"},
                {"word": "vocal", "start": 11.5, "end": 12.0,
                 "timing_source": "easyaligner-global"},
            ], source_timestamp=11.0),
        ]

        result = validate(lines, AlignmentConfig())

        self.assertGreater(result["uncertain"], 0)
        self.assertFalse(result["quality"]["publishable"])
        self.assertEqual(1, result["quality"]["line_overlap_conflicts"])
        self.assertIn("überlappt", lines[1].reason)

    def test_overlap_with_heuristic_next_line_remains_uncertain(self):
        lines = [
            LrcLine(10.0, "Lead vocal", "", words=[
                {"word": "Lead", "start": 10.0, "end": 11.0,
                 "timing_source": "qwen-forced"},
                {"word": "vocal", "start": 11.0, "end": 13.0,
                 "timing_source": "qwen-forced"},
            ], source_timestamp=10.0),
            LrcLine(11.0, "Uncertain", "", words=[
                {"word": "Uncertain", "start": 11.0, "end": 11.5,
                 "timing_source": "vocal-activity-repair"},
            ], source_timestamp=11.0),
        ]

        result = validate(lines, AlignmentConfig())

        self.assertEqual(2, result["uncertain"])
        self.assertIn("ragt stark", lines[0].reason)

    def test_overlapping_words_inside_one_line_are_never_publishable(self):
        line = LrcLine(38.2, "to live but", "", words=[
            {"word": "to", "start": 39.28, "end": 39.32,
             "timing_source": "qwen-forced"},
            {"word": "live", "start": 39.29, "end": 39.34,
             "timing_source": "qwen-forced"},
            {"word": "but", "start": 39.30, "end": 39.36,
             "timing_source": "qwen-forced"},
        ], source_timestamp=38.2)

        result = validate([line], AlignmentConfig())

        self.assertFalse(result["quality"]["publishable"])
        self.assertEqual(2, result["quality"]["word_overlap_conflicts"])
        self.assertIn("innerhalb", line.reason)

    def test_independent_voice_lanes_may_overlap(self):
        lead = LrcLine(10.0, "Lead", "", words=[
            {"word": "Lead", "start": 10.0, "end": 13.0,
             "timing_source": "qwen-forced"}], source_timestamp=10.0,
            voice_lane=0)
        backing = LrcLine(11.0, "Woho", "", words=[
            {"word": "Woho", "start": 11.0, "end": 14.0,
             "timing_source": "medleyvox-backing-vocal-activity"}],
            source_timestamp=11.0, voice_lane=1)

        result = validate([lead, backing], AlignmentConfig())

        self.assertEqual([], result["line_overlaps"])
        self.assertNotIn("überlappt", backing.reason or "")


if __name__ == "__main__":
    unittest.main()
