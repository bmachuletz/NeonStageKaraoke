import unittest

from app.transcriber import language_code


class TranscriberLanguageTests(unittest.TestCase):
    def test_maps_model_language_name_to_aligner_code(self):
        self.assertEqual("en", language_code("English"))
        self.assertEqual("de", language_code("German"))

    def test_accepts_supported_iso_code(self):
        self.assertEqual("pt", language_code("pt-BR"))

    def test_rejects_language_without_forced_alignment_model(self):
        with self.assertRaises(ValueError):
            language_code("Dutch")

    def test_uses_lyrics_when_asr_returns_no_language(self):
        self.assertEqual("de", language_code(None, "Wir sind nicht allein und gehen nach Haus"))
        self.assertEqual("en", language_code(None, "You and me in the house with my baby"))


if __name__ == "__main__":
    unittest.main()
