import unittest

from app.easy_aligner import (_line_tokens, _mean_score, _overlapping_chunk_plan,
                              _probability_from_mean_log_score)


class EasyAlignerTests(unittest.TestCase):
    def test_reversible_normalizer_keeps_word_count(self):
        self.assertEqual(["wir", "sind", "zurück"], _line_tokens("Wir sind zurück!"))

    def test_mean_score(self):
        self.assertAlmostEqual(0.6, _mean_score([{"score": 0.4}, {"score": 0.8}]))

    def test_log_probability_can_be_reported_as_probability(self):
        self.assertAlmostEqual(.02, _probability_from_mean_log_score(-3.912023), places=5)

    def test_overlapping_chunks_keep_one_gapless_global_frame_grid(self):
        plan = _overlapping_chunk_plan(186 * 16000, core_seconds=20,
                                       context_seconds=1, frame_stride_samples=320)

        self.assertEqual(10, len(plan))
        seam = plan[8]
        self.assertEqual(160 * 16000, seam["core_start"])
        self.assertEqual(159 * 16000, seam["window_start"])
        self.assertEqual(180 * 16000, seam["window_start"] +
                         seam["keep_end"] * 320)
        self.assertTrue(all(
            left["global_frame_start"] + left["keep_end"] - left["keep_start"] ==
            right["global_frame_start"]
            for left, right in zip(plan, plan[1:])))

    def test_final_partial_chunk_does_not_invent_audio_frames(self):
        plan = _overlapping_chunk_plan(186 * 16000 + 123,
                                       frame_stride_samples=320)

        self.assertEqual(180 * 16000, plan[-1]["core_start"])
        self.assertEqual(186 * 16000 + 123, plan[-1]["core_end"])
        self.assertLessEqual(plan[-1]["window_end"], 186 * 16000 + 123)


if __name__ == "__main__":
    unittest.main()
