import unittest

import numpy as np

from app.ctc_aligner import (_candidate_rejection_reason, _clean_words,
                             _section_allows_atomic_replacement)
from app.models import LrcLine


class CtcAlignerTests(unittest.TestCase):
    def test_normalizes_punctuation_but_keeps_german_letters(self):
        dictionary = {character: index for index, character in enumerate("-|abcdefghijklmnopqrstuvwxyzäöüß")}

        words = _clean_words("Träum weiter, Straße!", dictionary)

        self.assertEqual(["träum", "weiter", "straße"], words)

    def test_drops_characters_unknown_to_acoustic_dictionary(self):
        dictionary = {character: index for index, character in enumerate("-|abcdefghijklmnopqrstuvwxyz")}

        words = _clean_words("Café déjà", dictionary)

        self.assertEqual(["caf", "dj"], words)

    def test_expands_common_numeric_tokens_without_losing_word_cardinality(self):
        dictionary = {character: index for index, character in enumerate("-|abcdefghijklmnopqrstuvwxyzäöüß")}

        words = _clean_words("Die 1000 2000", dictionary)

        self.assertEqual(["die", "tausend", "zweitausend"], words)

    def test_rejects_a_single_forced_word_with_very_low_confidence(self):
        words = [
            {"word": "Du", "start": 1.0, "end": 1.1, "confidence": 0.0931},
            {"word": "träumst", "start": 1.2, "end": 1.5, "confidence": 0.4},
        ]

        reason = _candidate_rejection_reason(words, np.ones(32000, dtype=np.float32))

        self.assertEqual("low-word-confidence", reason)

    def test_rejects_an_implausible_gap_inside_one_lyric_line(self):
        words = [
            {"word": "Du", "start": 1.0, "end": 1.1, "confidence": 0.4},
            {"word": "träumst", "start": 2.02, "end": 2.3, "confidence": 0.4},
        ]

        reason = _candidate_rejection_reason(words, np.ones(48000, dtype=np.float32))

        self.assertEqual("implausible-internal-gap", reason)

    def test_rejects_a_weak_word_without_relative_acoustic_support(self):
        audio = np.zeros(32000, dtype=np.float32)
        audio[int(1.2 * 16000):int(1.5 * 16000)] = 0.5
        words = [
            {"word": "Du", "start": 1.0, "end": 1.1, "confidence": 0.15},
            {"word": "träumst", "start": 1.2, "end": 1.5, "confidence": 0.5},
        ]

        reason = _candidate_rejection_reason(words, audio)

        self.assertEqual("weak-word-without-acoustic-support", reason)

    def test_atomic_section_does_not_overwrite_a_plausible_neighbour(self):
        plausible = LrcLine(1.0, "bereits gut", "", words=[
            {"word": "bereits", "start": 1.0, "end": 1.2,
             "timing_source": "qwen-word-alignment"},
        ])
        heuristic = LrcLine(2.0, "noch schwach", "", words=[
            {"word": "schwach", "start": 2.0, "end": 2.2,
             "timing_source": "geometric-repair"},
        ])

        allowed = _section_allows_atomic_replacement(
            [plausible, heuristic], {id(heuristic)})

        self.assertFalse(allowed)

    def test_atomic_section_can_replace_an_entirely_heuristic_block(self):
        lines = [LrcLine(float(index), "schwach", "", words=[
            {"word": "schwach", "start": float(index), "end": index + .2,
             "timing_source": "geometric-repair"},
        ]) for index in (1, 2)]

        allowed = _section_allows_atomic_replacement(lines, {id(line) for line in lines})

        self.assertTrue(allowed)


if __name__ == "__main__":
    unittest.main()
