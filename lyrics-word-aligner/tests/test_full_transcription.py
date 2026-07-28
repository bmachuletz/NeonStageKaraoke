import unittest

from app.full_transcription import group_words_into_lines


class FullTranscriptionTests(unittest.TestCase):
    def test_groups_monotonic_words_at_vocal_pause(self):
        lines = group_words_into_lines([
            {"word": "Wir", "start": 1.0, "end": 1.3},
            {"word": "singen", "start": 1.31, "end": 1.8},
            {"word": "weiter", "start": 3.0, "end": 3.6},
        ])
        self.assertEqual(2, len(lines))
        self.assertEqual("Wir singen", lines[0].text)
        self.assertEqual(3.0, lines[1].timestamp)

    def test_repairs_overlapping_asr_word_windows_without_line_overlap(self):
        lines = group_words_into_lines([
            {"word": "eins", "start": 2.0, "end": 2.5},
            {"word": "zwei", "start": 2.4, "end": 2.9},
        ], maximum_words=1)
        self.assertEqual(2.5, lines[1].words[0]["start"])
        self.assertLessEqual(lines[0].words[-1]["end"], lines[1].words[0]["start"])

    def test_limits_line_length_for_stage_readability(self):
        words = [{"word": f"wort{index}", "start": index * .25,
                  "end": index * .25 + .2} for index in range(15)]
        lines = group_words_into_lines(words, maximum_words=5)
        self.assertEqual(3, len(lines))
        self.assertTrue(all(len(line.words) <= 5 for line in lines))


if __name__ == "__main__":
    unittest.main()
