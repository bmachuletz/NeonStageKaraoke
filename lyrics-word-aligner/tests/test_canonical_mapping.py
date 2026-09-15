import unittest

from app.canonical_mapping import assign_canonical_timings
from app.models import LrcLine


class CanonicalMappingTests(unittest.TestCase):
    def test_display_text_is_preserved_and_quantized(self):
        lines = [LrcLine(0, '"Wir ham die Scheiße satt.', '')]
        aligned = [
            {"word": word, "start": 10 + index * .203,
             "end": 10.18 + index * .203, "probability": .9}
            for index, word in enumerate(["Wir", "ham", "die", "Scheiße", "satt"])
        ]
        summary = assign_canonical_timings(lines, aligned)
        self.assertEqual(['"Wir', "ham", "die", "Scheiße", "satt."],
                         [word["word"] for word in lines[0].words])
        self.assertEqual(1.0, summary["mapping_similarity"])
        self.assertTrue(all(word["timing_source"] == "easyaligner-global-direct"
                            for word in lines[0].words))

    def test_incomplete_path_is_rejected(self):
        lines = [LrcLine(0, "eins zwei drei", "")]
        with self.assertRaisesRegex(ValueError, "unvollständig"):
            assign_canonical_timings(lines, [
                {"word": "eins", "start": 1.0, "end": 1.2},
                {"word": "drei", "start": 1.4, "end": 1.6},
            ])


if __name__ == "__main__":
    unittest.main()
