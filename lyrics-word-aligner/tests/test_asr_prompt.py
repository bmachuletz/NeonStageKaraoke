import os
import unittest
from unittest.mock import patch

from app.asr_prompt import build_asr_prompt


class AsrPromptTests(unittest.TestCase):
    def test_builds_bounded_distinct_vocabulary_instead_of_ordered_lyrics(self):
        prompt, summary = build_asr_prompt(
            [
                "Nazis nehmen uns die Arbeitsplätze weg",
                "Nazis nehmen uns die Arbeitsplätze weg",
                "Wir verzweifeln nicht",
            ],
            "Nazis nehmen uns die Arbeitsplätze weg - Testband",
            "de",
            max_chars=260,
        )

        self.assertIsNotNone(prompt)
        self.assertLessEqual(len(prompt), 260)
        self.assertIn("Arbeitsplätze", prompt)
        self.assertEqual(1, prompt.split("Vocabulary hints", 1)[1].count("Arbeitsplätze"))
        self.assertFalse(summary["ordered_lyrics_supplied"])
        self.assertGreater(summary["vocabulary_terms"], 0)

    def test_can_be_disabled_for_comparative_runs(self):
        with patch.dict(os.environ, {"LRC_ASR_PROMPT": "false"}):
            prompt, summary = build_asr_prompt(["Seltene Wörter"], "Song", "de")

        self.assertIsNone(prompt)
        self.assertFalse(summary["enabled"])

    def test_prioritizes_distinctive_terms_when_budget_is_small(self):
        prompt, _summary = build_asr_prompt(
            ["und die der außergewöhnlicher Störtebeker"], "Test", "de", max_chars=180)

        self.assertIn("außergewöhnlicher", prompt)
        self.assertIn("Störtebeker", prompt)


if __name__ == "__main__":
    unittest.main()
