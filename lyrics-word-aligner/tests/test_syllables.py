import unittest
from types import SimpleNamespace

import numpy as np

from app.syllables import (_enforce_minimum_syllable_geometry,
                           enrich_lines_with_syllables)
from app.acoustic_boundaries import _select_monotone_boundaries


class SyllableAlignmentTests(unittest.TestCase):
    def test_impossible_edge_syllable_is_projected_inside_the_word(self):
        parts = [
            {"text": "real", "start": 10.0, "end": 10.032},
            {"text": "ly", "start": 10.032, "end": 10.32},
        ]

        parts, repaired = _enforce_minimum_syllable_geometry(
            parts, 10.0, 10.32)

        self.assertTrue(repaired)
        self.assertTrue(all(part["end"] - part["start"] >= 0.054
                            for part in parts))

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

    def test_ipa_vowel_nuclei_define_separated_articulation_windows(self):
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
        self.assertEqual(1, summary["phoneme_vowel_articulation_windows"])
        self.assertEqual("phoneme-vowel-articulation-windows-v1",
                         line.words[0]["syllable_method"])
        self.assertEqual(1.55, parts[0]["end"])
        self.assertEqual(1.64, parts[1]["start"])
        self.assertEqual("phoneme-vowel-articulation-window",
                         parts[0]["boundary_source"])

    def test_unterschreib_uses_short_vowel_windows_with_real_gaps(self):
        line = SimpleNamespace(words=[{
            "word": "unterschreib", "start": 49.601, "end": 51.104,
            "phoneme_confidence": 0.383,
            "phonemes": [
                {"phone": "ʊ", "start": 49.601, "end": 49.822},
                {"phone": "n", "start": 49.822, "end": 50.343},
                {"phone": "t", "start": 50.363, "end": 50.423},
                {"phone": "ɜ", "start": 50.443, "end": 50.603},
                {"phone": "ʃ", "start": 50.623, "end": 50.683},
                {"phone": "r", "start": 50.683, "end": 50.743},
                {"phone": "aɪ", "start": 50.763, "end": 51.084},
            ],
        }])

        summary = enrich_lines_with_syllables([line], "de")

        parts = line.words[0]["syllables"]
        self.assertEqual(["un", "ter", "schreib"],
                         [part["text"] for part in parts])
        self.assertEqual([(49.601, 49.822), (50.443, 50.603),
                          (50.763, 51.104)],
                         [(part["start"], part["end"]) for part in parts])
        self.assertGreater(parts[1]["start"] - parts[0]["end"], 0.60)
        self.assertGreater(parts[2]["start"] - parts[1]["end"], 0.15)
        self.assertEqual(1, summary["phoneme_vowel_articulation_windows"])

    def test_truncated_phone_path_cannot_collapse_final_syllable(self):
        line = SimpleNamespace(words=[{
            "word": "Lage", "start": 1.0, "end": 1.42,
            "phoneme_confidence": 0.82,
            "phonemes": [
                {"phone": "l", "start": 1.0, "end": 1.08},
                {"phone": "a", "start": 1.08, "end": 1.36},
                {"phone": "g", "start": 1.42, "end": 1.42},
                {"phone": "ə", "start": 1.42, "end": 1.42},
            ],
        }])

        enrich_lines_with_syllables([line], "de")

        parts = line.words[0]["syllables"]
        self.assertEqual(["La", "ge"], [part["text"] for part in parts])
        self.assertGreaterEqual(parts[-1]["end"] - parts[-1]["start"], 0.055)
        self.assertNotEqual("phoneme-syllable-onsets-v1.1",
                            line.words[0]["syllable_method"])

    def test_internal_only_phone_path_places_syllables_without_moving_word(self):
        line = SimpleNamespace(words=[{
            "word": "sozusagen", "start": 10.0, "end": 12.0,
            "syllable_phoneme_confidence": .49,
            "syllable_phonemes": [
                {"phone": "z", "start": 10.0, "end": 10.08},
                {"phone": "o", "start": 10.08, "end": 10.29},
                {"phone": "ts", "start": 10.30, "end": 10.38},
                {"phone": "u", "start": 10.38, "end": 10.67},
                {"phone": "z", "start": 10.71, "end": 10.79},
                {"phone": "a", "start": 10.79, "end": 11.08},
                {"phone": "g", "start": 11.12, "end": 11.20},
                {"phone": "ə", "start": 11.20, "end": 11.72},
                {"phone": "n", "start": 11.72, "end": 11.81},
            ],
        }])

        enrich_lines_with_syllables([line], "de")

        word = line.words[0]
        self.assertEqual(10.0, word["start"])
        self.assertEqual(12.0, word["end"])
        self.assertEqual(["so", "zu", "sa", "gen"],
                         [item["text"] for item in word["syllables"]])
        self.assertAlmostEqual(10.29, word["syllables"][0]["end"], places=2)
        self.assertAlmostEqual(10.38, word["syllables"][1]["start"], places=2)
        self.assertAlmostEqual(10.67, word["syllables"][1]["end"], places=2)
        self.assertAlmostEqual(10.79, word["syllables"][2]["start"], places=2)

    def test_english_ipa_nuclei_correct_pyphen_away_and_apart(self):
        line = SimpleNamespace(words=[
            {"word": "away", "start": 1.0, "end": 2.2,
             "phoneme_confidence": .8, "phonemes": [
                 {"phone": "ɐ", "start": 1.05, "end": 1.35},
                 {"phone": "w", "start": 1.35, "end": 1.5},
                 {"phone": "eɪ", "start": 1.5, "end": 2.15}]},
            {"word": "apart", "start": 2.3, "end": 3.4,
             "phoneme_confidence": .8, "phonemes": [
                 {"phone": "ɐ", "start": 2.3, "end": 2.55},
                 {"phone": "p", "start": 2.55, "end": 2.7},
                 {"phone": "ɑːɹ", "start": 2.7, "end": 3.2},
                 {"phone": "t", "start": 3.2, "end": 3.4}]},
        ])

        summary = enrich_lines_with_syllables([line], "en")

        self.assertEqual(["a", "way"], [part["text"] for part in line.words[0]["syllables"]])
        self.assertEqual(["a", "part"], [part["text"] for part in line.words[1]["syllables"]])
        self.assertEqual(2, summary["phoneme_corrected_text_splits"])

    def test_espeak_pronunciation_repairs_away_without_accepted_ctc_path(self):
        line = SimpleNamespace(words=[{
            "word": "away", "start": 1.0, "end": 3.0,
        }])

        summary = enrich_lines_with_syllables([line], "en")

        self.assertEqual(["a", "way"], [
            part["text"] for part in line.words[0]["syllables"]])
        self.assertEqual("espeak-ipa-vowel-nucleus-count",
                         line.words[0]["syllable_split_source"])
        self.assertEqual(1, summary["phoneme_corrected_text_splits"])

    def test_vowel_windows_do_not_paint_through_an_internal_consonant_cluster(self):
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
        self.assertEqual(2.42, parts[0]["end"])
        self.assertEqual(2.70, parts[1]["start"])

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
        self.assertGreaterEqual(line.words[0]["syllable_confidence"], 0.62)
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
