import unittest
from types import SimpleNamespace

from app.syllables import enrich_lines_with_syllables


class SyllableAlignmentTests(unittest.TestCase):
    def test_boundaries_fill_the_gpu_word_window_without_gaps(self):
        line = SimpleNamespace(words=[{"word": "Leidenschaft", "start": 7.92, "end": 8.88}])

        summary = enrich_lines_with_syllables([line], "de")

        parts = line.words[0]["syllables"]
        self.assertGreaterEqual(len(parts), 2)
        self.assertEqual(7.92, parts[0]["start"])
        self.assertEqual(8.88, parts[-1]["end"])
        for left, right in zip(parts, parts[1:]):
            self.assertEqual(left["end"], right["start"])
        self.assertFalse(summary["acoustic_syllable_boundaries"])

    def test_punctuation_is_preserved(self):
        line = SimpleNamespace(words=[{"word": "(gehen),", "start": 1.0, "end": 1.8}])
        enrich_lines_with_syllables([line], "de")
        text = "".join(part["text"] for part in line.words[0]["syllables"])
        self.assertEqual("(gehen),", text)

    def test_unknown_language_falls_back_to_low_confidence_intervals(self):
        line = SimpleNamespace(words=[{"word": "karaoke", "start": 2.0, "end": 2.7}])
        enrich_lines_with_syllables([line], "xx-not-a-language")
        self.assertGreaterEqual(len(line.words[0]["syllables"]), 1)
        self.assertLess(line.words[0]["syllable_confidence"], 0.62)


if __name__ == "__main__":
    unittest.main()
