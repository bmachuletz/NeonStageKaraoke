import unittest
from app.models import AlignmentConfig, LrcLine
from app.repair import repair_collapsed_timings


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


if __name__ == "__main__":
    unittest.main()
