import unittest
from types import SimpleNamespace

import numpy as np

from app.syllables import enrich_lines_with_syllables
from app.acoustic_boundaries import _select_monotone_boundaries


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

    def test_ipa_vowel_nuclei_define_sung_syllable_boundaries(self):
        line = SimpleNamespace(words=[{
            "word": "Leben", "start": 1.0, "end": 2.0,
            "phoneme_confidence": 0.82,
            "phonemes": [
                {"phone": "l", "start": 1.0, "end": 1.08},
                {"phone": "eː", "start": 1.08, "end": 1.55},
                {"phone": "b", "start": 1.55, "end": 1.64},
                {"phone": "ə", "start": 1.64, "end": 1.91},
                {"phone": "n", "start": 1.91, "end": 2.0},
            ],
        }])

        summary = enrich_lines_with_syllables([line], "de")

        parts = line.words[0]["syllables"]
        self.assertEqual(1, summary["phoneme_nucleus_splits"])
        self.assertEqual("phoneme-syllable-onsets-v1.1", line.words[0]["syllable_method"])
        self.assertEqual(1.55, parts[0]["end"])
        self.assertEqual("phoneme-syllable-onset", parts[0]["boundary_source"])

    def test_textual_syllable_onset_selects_only_its_consonant_from_a_cluster(self):
        line = SimpleNamespace(words=[{
            "word": "Fenster", "start": 2.0, "end": 3.0,
            "phoneme_confidence": 0.86,
            "phonemes": [
                {"phone": "f", "start": 2.0, "end": 2.08},
                {"phone": "ɛ", "start": 2.08, "end": 2.42},
                {"phone": "n", "start": 2.42, "end": 2.52},
                {"phone": "s", "start": 2.52, "end": 2.61},
                {"phone": "t", "start": 2.61, "end": 2.70},
                {"phone": "ɐ", "start": 2.70, "end": 2.94},
            ],
        }])

        enrich_lines_with_syllables([line], "de")

        parts = line.words[0]["syllables"]
        self.assertEqual(["Fens", "ter"], [part["text"] for part in parts])
        self.assertEqual(2.61, parts[0]["end"])

    def test_punctuation_is_preserved(self):
        line = SimpleNamespace(words=[{"word": "(gehen),", "start": 1.0, "end": 1.8}])
        enrich_lines_with_syllables([line], "de")
        text = "".join(part["text"] for part in line.words[0]["syllables"])
        self.assertEqual("(gehen),", text)

    def test_german_diphthong_is_one_sung_syllable(self):
        line = SimpleNamespace(words=[
            {"word": "Träum", "start": 1.0, "end": 1.6},
            {"word": "Bäume", "start": 1.8, "end": 2.6},
        ])

        enrich_lines_with_syllables([line], "de")

        self.assertEqual(["Träum"], [
            part["text"] for part in line.words[0]["syllables"]])
        self.assertEqual(["Bäu", "me"], [
            part["text"] for part in line.words[1]["syllables"]])

    def test_unknown_language_falls_back_to_low_confidence_intervals(self):
        line = SimpleNamespace(words=[{"word": "karaoke", "start": 2.0, "end": 2.7}])
        enrich_lines_with_syllables([line], "xx-not-a-language")
        self.assertGreaterEqual(len(line.words[0]["syllables"]), 1)
        self.assertLess(line.words[0]["syllable_confidence"], 0.62)

    def test_local_spectral_change_refines_an_orthographic_boundary(self):
        sample_rate = 16000
        time = np.arange(sample_rate, dtype=np.float32) / sample_rate
        # Constant loudness but a clear vocal-tract-like spectral transition at
        # 600 ms.  A plain energy threshold cannot detect this boundary.
        audio = np.where(
            time < 0.6,
            np.sin(2 * np.pi * 190 * time) + 0.35 * np.sin(2 * np.pi * 570 * time),
            np.sin(2 * np.pi * 310 * time) + 0.35 * np.sin(2 * np.pi * 930 * time),
        ).astype(np.float32) * 0.25
        line = SimpleNamespace(words=[{
            "word": "Leben", "start": 0.0, "end": 1.0,
        }])

        summary = enrich_lines_with_syllables([line], "de", audio=audio)

        parts = line.words[0]["syllables"]
        self.assertEqual(2, len(parts))
        self.assertGreaterEqual(summary["acoustic_change_point_refinements"], 1)
        self.assertAlmostEqual(0.6, parts[0]["end"], delta=0.06)
        self.assertEqual("acoustic-change-point", parts[0]["boundary_source"])

    def test_flat_sustain_does_not_override_the_prior(self):
        sample_rate = 16000
        time = np.arange(sample_rate, dtype=np.float32) / sample_rate
        audio = (0.25 * np.sin(2 * np.pi * 220 * time)).astype(np.float32)
        baseline = SimpleNamespace(words=[{
            "word": "Leben", "start": 0.0, "end": 1.0,
        }])
        enrich_lines_with_syllables([baseline], "de")
        prior = baseline.words[0]["syllables"][0]["end"]
        line = SimpleNamespace(words=[{
            "word": "Leben", "start": 0.0, "end": 1.0,
        }])

        summary = enrich_lines_with_syllables([line], "de", audio=audio)

        self.assertEqual(0, summary["acoustic_change_point_refinements"])
        self.assertEqual(prior, line.words[0]["syllables"][0]["end"])

    def test_joint_boundary_path_does_not_let_a_peak_steal_next_syllable(self):
        rows = [
            [
                {"time": 0.40, "score": 0.0, "is_prior": True},
                {"time": 0.66, "score": 1.2, "is_prior": False},
            ],
            [
                {"time": 0.70, "score": 0.0, "is_prior": True},
                {"time": 0.73, "score": 0.8, "is_prior": False},
            ],
        ]

        selected = _select_monotone_boundaries(rows, minimum_part=0.08)

        self.assertIsNotNone(selected)
        self.assertEqual([0.40, 0.73], [item["time"] for item in selected])


if __name__ == "__main__":
    unittest.main()
