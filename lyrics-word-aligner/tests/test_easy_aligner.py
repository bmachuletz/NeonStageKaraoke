import unittest

from app.easy_aligner import _line_tokens, _mean_score


class EasyAlignerTests(unittest.TestCase):
    def test_reversible_normalizer_keeps_word_count(self):
        self.assertEqual(["wir", "sind", "zurück"], _line_tokens("Wir sind zurück!"))

    def test_mean_score(self):
        self.assertAlmostEqual(0.6, _mean_score([{"score": 0.4}, {"score": 0.8}]))


if __name__ == "__main__":
    unittest.main()
