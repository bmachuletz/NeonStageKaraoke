import unittest

from app.window_edges import mark_leading_window_edge_fallback


class WindowEdgeTests(unittest.TestCase):
    def test_marks_token_emitted_at_preroll_edge(self):
        words = [{"word": "Lichtenhagen", "start": 59.34, "end": 60.46,
                  "timing_source": "qwen-forced"}]

        marked = mark_leading_window_edge_fallback(
            words, line_timestamp=59.79, window_base=59.34, pre_roll=.45)

        self.assertTrue(marked)
        self.assertTrue(words[0]["window_edge_fallback"])
        self.assertEqual(59.34, words[0]["window_edge_base"])

    def test_does_not_mark_measured_token_inside_window(self):
        words = [{"word": "Lichtenhagen", "start": 60.81, "end": 61.95,
                  "timing_source": "qwen-forced"}]

        marked = mark_leading_window_edge_fallback(
            words, line_timestamp=59.79, window_base=59.34, pre_roll=.45)

        self.assertFalse(marked)
        self.assertNotIn("window_edge_fallback", words[0])

    def test_full_song_alignment_without_preroll_is_not_marked(self):
        words = [{"word": "Hallo", "start": 0.0, "end": .4,
                  "timing_source": "qwen-forced"}]

        marked = mark_leading_window_edge_fallback(
            words, line_timestamp=0.0, window_base=0.0, pre_roll=0.0)

        self.assertFalse(marked)


if __name__ == "__main__":
    unittest.main()
