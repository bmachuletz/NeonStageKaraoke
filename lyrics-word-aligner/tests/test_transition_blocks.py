import unittest

from app.models import LrcLine
from app.repetition_anchors import apply_transition_words, transition_requests


class TransitionBlockTests(unittest.TestCase):
    def test_plans_anchor_and_following_lines(self):
        lines = [LrcLine(float(index * 5), text, "", source_timestamp=float(index * 5))
                 for index, text in enumerate(("vorher", "träum weiter", "ich hingegen", "danach", "ende"))]
        pair = {"expected": {"start": 1, "end": 3}, "audio_start": 5.1, "audio_end": 8.0}
        requests = transition_requests(lines, [pair], 30.0, following_lines=2)
        self.assertEqual(1, requests[0]["first_line"])
        self.assertEqual(4, requests[0]["end_line"])
        self.assertIn("ich hingegen", requests[0]["transcript"])

    def test_preserves_anchor_and_replaces_context(self):
        line = LrcLine(1, "fest danach", "", words=[
            {"word": "fest", "start": 1.0, "end": 1.5,
             "timing_source": "asr-repetition-anchor"},
            {"word": "danach", "start": 0.0, "end": 0.0},
        ])
        changed = apply_transition_words([line], {"first_line": 0, "end_line": 1}, [
            {"word": "fest", "start": 9.0, "end": 9.5},
            {"word": "danach", "start": 1.6, "end": 2.2},
        ])
        self.assertEqual(1, changed)
        self.assertEqual(1.0, line.words[0]["start"])
        self.assertEqual(1.6, line.words[1]["start"])
        self.assertEqual("transition-block-qwen", line.words[1]["timing_source"])
