import unittest

from app.transcriber import (language_code, merge_transcript_chunks,
                             reconcile_detected_language)


class TranscriberLanguageTests(unittest.TestCase):
    def test_merges_word_overlap_between_asr_chunks(self):
        self.assertEqual(
            "we sing tonight together now",
            merge_transcript_chunks([
                "we sing tonight", "tonight together", "together now"
            ]),
        )

    def test_keeps_non_overlapping_chunk_text(self):
        self.assertEqual(
            "first phrase second phrase",
            merge_transcript_chunks(["first phrase", "second phrase"]),
        )

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


class LanguageReconciliationTests(unittest.TestCase):
    """Canonical lyrics outrank a contradicting ASR language guess."""

    GERMAN = ("Wollen wir noch ein bisschen zusammen rumhängen "
              "Auf den Dächern dieser Stadt vergessen wieder mal die Zeit "
              "Die Vögel singen es wird hell und wir singen mit "
              "Nicht mehr lang dann geht die Sonne auf")
    ENGLISH = ("We got a rose in the garden and the sun is on my face "
               "I have not got the time to say what you want from me "
               "And that is all we have in the end of it")

    def test_unambiguous_german_lyrics_overrule_an_english_guess(self):
        language, evidence = reconcile_detected_language("en", self.GERMAN)

        self.assertEqual("de", language)
        self.assertTrue(evidence["applied"])
        self.assertEqual("canonical-lyrics-contradict-asr", evidence["reason"])
        self.assertGreater(evidence["german_score"], evidence["english_score"])

    def test_agreeing_text_leaves_the_detection_untouched(self):
        language, evidence = reconcile_detected_language("de", self.GERMAN)

        self.assertEqual("de", language)
        self.assertFalse(evidence["applied"])
        self.assertEqual("text-agrees-with-asr", evidence["reason"])

    def test_english_lyrics_are_not_flipped_to_german(self):
        language, evidence = reconcile_detected_language("en", self.ENGLISH)

        self.assertEqual("en", language)
        self.assertFalse(evidence["applied"])

    def test_a_few_loan_words_never_flip_a_song(self):
        language, evidence = reconcile_detected_language(
            "en", "Baby you and me tonight und du")

        self.assertEqual("en", language)
        self.assertFalse(evidence["applied"])

    def test_a_narrow_textual_lead_is_not_decisive_enough(self):
        # German leads, but by too little to overrule an acoustic verdict.
        language, evidence = reconcile_detected_language(
            "en", "und du und wir mit dem Rest von dieser langen Nacht")

        self.assertEqual("en", language)
        self.assertFalse(evidence["applied"])
        self.assertEqual("text-evidence-too-weak", evidence["reason"])
        self.assertLess(evidence["margin"], evidence["minimum_margin"])

    def test_a_language_without_markers_keeps_the_asr_verdict(self):
        language, evidence = reconcile_detected_language(
            "fr", "Je ne regrette rien de tout cela")

        self.assertEqual("fr", language)
        self.assertFalse(evidence["applied"])

    def test_empty_lyrics_keep_the_asr_verdict(self):
        language, evidence = reconcile_detected_language("en", "")

        self.assertEqual("en", language)
        self.assertFalse(evidence["applied"])
