import unittest

from app.full_transcription import (audio_chunk_windows, group_words_into_lines,
                                    keep_owned_words, plan_transcription_windows)


class FullTranscriptionTests(unittest.TestCase):
    def test_normal_song_remains_one_complete_asr_window(self):
        windows, chunked = plan_transcription_windows(
            240 * 16000, threshold_seconds=300, chunk_seconds=20, overlap_seconds=2)
        self.assertFalse(chunked)
        self.assertEqual([(0, 240 * 16000, 0.0, 240.0)], windows)

    def test_only_long_song_uses_bounded_windows(self):
        windows, chunked = plan_transcription_windows(
            430 * 16000, threshold_seconds=300, chunk_seconds=20, overlap_seconds=2)
        self.assertTrue(chunked)
        self.assertGreater(len(windows), 1)
        self.assertTrue(all((end - start) <= 20 * 16000 for start, end, _, _ in windows))

    def test_long_audio_is_split_into_bounded_overlapping_windows(self):
        windows = audio_chunk_windows(65 * 16000, chunk_seconds=20, overlap_seconds=2)
        self.assertEqual(4, len(windows))
        self.assertTrue(all((end - start) <= 20 * 16000 for start, end, _, _ in windows))
        self.assertEqual(0.0, windows[0][2])
        self.assertEqual(65.0, windows[-1][3])

    def test_overlap_ownership_emits_each_word_only_once(self):
        first = keep_owned_words([
            {"word": "links", "start": 17.5, "end": 18.1},
            {"word": "rechts", "start": 18.8, "end": 19.2},
        ], base_seconds=0, keep_start=0, keep_end=19)
        second = keep_owned_words([
            {"word": "links", "start": 0.0, "end": 0.1},
            {"word": "rechts", "start": 0.8, "end": 1.2},
        ], base_seconds=18, keep_start=19, keep_end=37)
        self.assertEqual(["links"], [word["word"] for word in first])
        self.assertEqual(["rechts"], [word["word"] for word in second])

    def test_invalid_chunk_overlap_is_rejected(self):
        with self.assertRaises(ValueError):
            audio_chunk_windows(16000, chunk_seconds=20, overlap_seconds=20)

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
