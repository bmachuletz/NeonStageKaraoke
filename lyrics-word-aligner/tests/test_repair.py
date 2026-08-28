import unittest
from app.models import AlignmentConfig, LrcLine
from app.repair import (constrain_final_words_to_source_boundaries,
                        repair_collapsed_timings)


class CollapsedAlignmentRepairTests(unittest.TestCase):
    def test_repeated_zero_duration_words_are_spread_monotonically(self):
        line = LrcLine(36.88, "Träum weiter träum weiter", "")
        line.words = [
            {"word": "Träum", "start": 36.43, "end": 37.15},
            {"word": "weiter", "start": 37.23, "end": 37.55},
            {"word": "träum", "start": 41.23, "end": 41.23},
            {"word": "weiter", "start": 41.23, "end": 41.23},
        ]
        following = LrcLine(41.13, "Danach", "")
        repaired = repair_collapsed_timings([line, following], AlignmentConfig())
        self.assertEqual(1, repaired)
        self.assertTrue(all(float(word["end"]) > float(word["start"]) for word in line.words))
        self.assertTrue(all(float(a["end"]) <= float(b["start"]) for a, b in zip(line.words, line.words[1:])))
        self.assertEqual(41.13, line.words[-1]["end"])

    def test_geometric_repair_does_not_fill_pause_until_next_line(self):
        line = LrcLine(10.0, "Sing this now", "", words=[
            {"word": "Sing", "start": 10.0, "end": 10.45},
            {"word": "this", "start": 10.5, "end": 10.5},
            {"word": "now", "start": 11.1, "end": 11.1},
        ])
        following = LrcLine(18.0, "After a long pause", "")

        repaired = repair_collapsed_timings(
            [line, following], AlignmentConfig())

        self.assertEqual(1, repaired)
        self.assertEqual(11.1, line.words[-1]["end"])
        self.assertLess(line.words[-1]["end"], following.timestamp - 6.0)

    def test_overlapping_positive_words_are_not_treated_as_valid(self):
        line = LrcLine(10.0, "to live but", "", words=[
            {"word": "to", "start": 10.0, "end": 10.4},
            {"word": "live", "start": 10.2, "end": 10.6},
            {"word": "but", "start": 10.3, "end": 10.8},
        ])

        repaired = repair_collapsed_timings([line], AlignmentConfig())

        self.assertEqual(1, repaired)
        self.assertTrue(all(left["end"] <= right["start"]
                            for left, right in zip(line.words, line.words[1:])))

    def test_short_function_words_do_not_flatten_valid_acoustic_timing(self):
        line = LrcLine(38.2, "I got a lot of toys", "", words=[
            {"word": "I", "start": 38.20, "end": 38.22},
            {"word": "got", "start": 38.30, "end": 38.70},
            {"word": "a", "start": 38.80, "end": 38.82},
            {"word": "lot", "start": 38.90, "end": 39.20},
            {"word": "of", "start": 39.30, "end": 39.38},
            {"word": "toys", "start": 39.45, "end": 39.90},
        ])
        original = [(word["start"], word["end"]) for word in line.words]

        repaired = repair_collapsed_timings([line], AlignmentConfig())

        self.assertEqual(0, repaired)
        self.assertEqual(original, [(word["start"], word["end"]) for word in line.words])

    def test_late_sustain_cannot_cross_explicit_empty_lrc_marker(self):
        line = LrcLine(136.61, "and play", "", source_end_boundary=144.78,
                       words=[
                           {"word": "and", "start": 142.08, "end": 143.28},
                           {"word": "play", "start": 143.30, "end": 146.27,
                            "sustain_extension_ms": 2670},
                       ])

        constrained = constrain_final_words_to_source_boundaries([line])

        self.assertEqual(1, constrained)
        self.assertEqual(144.78, line.words[-1]["end"])
        self.assertEqual(1490.0, line.words[-1]["source_boundary_trim_ms"])

    def test_confirmed_sung_release_can_override_inaccurate_empty_marker(self):
        line = LrcLine(90.0, "play", "", source_end_boundary=91.91,
                       words=[{"word": "play", "start": 90.45, "end": 93.65,
                               "sustain_activity_end": 90.72,
                               "sustain_tonal_release": 93.59,
                               "sustain_release_confidence": .91}])

        constrained = constrain_final_words_to_source_boundaries([line])

        self.assertEqual(0, constrained)
        self.assertEqual(93.65, line.words[-1]["end"])
        self.assertEqual(91.91, line.words[-1]["source_boundary_overridden"])
        self.assertIsNone(line.source_end_boundary)


if __name__ == "__main__":
    unittest.main()
