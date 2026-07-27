import unittest

from app.mms_aligner import normalize_words


class MmsAlignerTests(unittest.TestCase):
    def test_normalizes_german_words_and_numbers_one_to_one(self):
        self.assertEqual(
            ["strasse", "fur", "tausend", "traume"],
            normalize_words("Straße für 1000 Träume"),
        )


if __name__ == "__main__":
    unittest.main()
