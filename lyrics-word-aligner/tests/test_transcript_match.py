import unittest

from app.transcript_match import compare_transcripts, normalize_words, repeated_blocks, word_similarity


class TranscriptMatchTests(unittest.TestCase):
    def test_two_refrain_repetitions_are_opt_in(self):
        expected = "alle hassen nazis alle hassen nazis"
        default = compare_transcripts(expected, expected)
        punk_candidate = compare_transcripts(expected, expected, min_repetitions=2)
        self.assertEqual([], default["repeated_blocks"]["expected"])
        self.assertEqual(2, punk_candidate["repeated_blocks"]["expected"][0]["repetitions"])

    def test_normalizes_german_punctuation_without_losing_umlauts(self):
        self.assertEqual(["träum", "weiter"], normalize_words("TRÄUM, weiter!"))

    def test_detects_an_extra_repetition(self):
        result = compare_transcripts("träum weiter träum weiter", "träum weiter träum weiter träum weiter")
        self.assertEqual(2, result["counts"]["insert"])
        self.assertEqual(4, result["matching_words"])
        self.assertLess(result["similarity"], 1.0)

    def test_exact_text_is_a_perfect_match(self):
        result = compare_transcripts("Hallo schöne Welt", "Hallo schöne Welt")
        self.assertEqual(1.0, result["similarity"])
        self.assertEqual(0, result["edit_distance"])

    def test_phonetically_close_words_are_approximate(self):
        self.assertGreaterEqual(word_similarity("träum", "triumph"), 0.72)
        result = compare_transcripts("träum", "triumph")
        self.assertEqual(1, result["counts"]["approximate"])

    def test_repeated_phrase_blocks_are_detected_independently_of_words(self):
        expected = repeated_blocks(normalize_words("träum weiter " * 6))
        recognized = repeated_blocks(normalize_words("neunmal tag " * 6))
        self.assertEqual(6, expected[0]["repetitions"])
        self.assertEqual(6, recognized[0]["repetitions"])
        self.assertEqual(2, expected[0]["unit_words"])

    def test_different_repeated_phrases_are_paired_by_structure(self):
        result = compare_transcripts(
            "anfang " + "träum weiter " * 7 + "ende",
            "anfang " + "neunmal tag " * 6 + "ende",
        )
        pairs = result["repeated_blocks"]["paired"]
        self.assertEqual(1, len(pairs))
        self.assertEqual("träum weiter", pairs[0]["expected"]["unit"])
        self.assertEqual("neunmal tag", pairs[0]["recognized"]["unit"])
        self.assertGreater(pairs[0]["confidence"], 0.8)
