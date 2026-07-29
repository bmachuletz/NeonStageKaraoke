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

    def test_ctc_character_boundaries_place_syllables_acoustically(self):
        line = SimpleNamespace(words=[{
            "word": "Scheiße", "start": 1.0, "end": 2.0,
            "ctc_characters": [
                {"character": value, "start": start, "end": finish, "confidence": .9}
                for value, start, finish in (
                    ("s", 1.0, 1.05), ("c", 1.05, 1.1), ("h", 1.1, 1.15),
                    ("e", 1.15, 1.25), ("i", 1.25, 1.65), ("ß", 1.7, 1.75),
                    ("e", 1.75, 2.0),
                )
            ],
        }])
        summary = enrich_lines_with_syllables([line], "de")
        parts = line.words[0]["syllables"]
        self.assertTrue(summary["acoustic_syllable_boundaries"])
        self.assertEqual("ctc-character-boundaries-v1", line.words[0]["syllable_method"])
        self.assertAlmostEqual(1.675, parts[0]["end"], places=3)
        self.assertEqual(parts[0]["end"], parts[1]["start"])

    def test_sustain_extends_only_the_final_syllable(self):
        line = SimpleNamespace(words=[{
            "word": "Leidenschaft", "start": 7.0, "acoustic_end": 7.8, "end": 8.6,
        }])
        summary = enrich_lines_with_syllables([line], "de")
        parts = line.words[0]["syllables"]
        self.assertEqual(1, summary["sustained_endings"])
        self.assertLess(parts[-2]["end"], 8.0)
        self.assertEqual(8.6, parts[-1]["end"])
        self.assertEqual(800, parts[-1]["sustain_extension_ms"])

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
