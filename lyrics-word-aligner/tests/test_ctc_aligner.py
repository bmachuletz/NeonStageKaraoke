import unittest

from app.ctc_aligner import _clean_words


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


if __name__ == "__main__":
    unittest.main()
