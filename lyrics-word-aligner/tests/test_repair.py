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


if __name__ == "__main__":
    unittest.main()
