import unittest

from app.completeness import assess_lyric_completeness, apply_completeness_gate
from app.models import LrcLine


def line(start: float, end: float, text: str = "known words") -> LrcLine:
    value = LrcLine(timestamp=start, text=text, original="", source_timestamp=start)
    value.words = [{"word": word, "start": start, "end": end} for word in text.split()]
    return value


class CompletenessTests(unittest.TestCase):
    def test_rejects_asr_backed_vocal_gap_between_lyrics(self):
        lines = [line(0.0, 2.0), line(10.0, 12.0)]
        words = [
            {"word": "missing", "start": 5.0, "end": 5.4},
            {"word": "chorus", "start": 5.5, "end": 5.9},
            {"word": "here", "start": 6.0, "end": 6.4},
        ]
        result = assess_lyric_completeness(lines, [(4.9, 6.5)], words, 12.0)
        self.assertFalse(result["complete"])
        summary = {"quality": {"score": 100.0, "grade": "excellent", "publishable": True}}
        apply_completeness_gate(summary, result)
        self.assertEqual(79.9, summary["quality"]["score"])
        self.assertFalse(summary["quality"]["publishable"])


    def test_ignores_instrumental_gap_without_recognized_words(self):
        result = assess_lyric_completeness(
            [line(0.0, 2.0), line(10.0, 12.0)], [(4.9, 6.5)], [], 12.0
        )
        self.assertTrue(result["complete"])
        self.assertTrue(result["requires_targeted_reanalysis"])
        self.assertEqual(1, len(result["investigation_regions"]))


if __name__ == "__main__":
    unittest.main()
