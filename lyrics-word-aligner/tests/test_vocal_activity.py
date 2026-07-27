import unittest

try:
    import numpy as np
except ModuleNotFoundError:  # Host-side lightweight test environment
    np = None

from app.models import AlignmentConfig, LrcLine
if np is not None:
    from app.vocal_activity import (detect_vocal_activity, repair_anchor_context,
                                    repair_anchor_line_tails, repair_with_vocal_activity)


@unittest.skipIf(np is None, "NumPy ist nur in der Audio-/GPU-Umgebung installiert")
class VocalActivityTests(unittest.TestCase):
    def test_detects_two_energy_regions(self):
        audio = np.zeros(16000 * 3, dtype=np.float32)
        audio[4000:12000] = 0.4
        audio[24000:36000] = 0.3
        regions = detect_vocal_activity(audio)
        self.assertEqual(2, len(regions))
        self.assertAlmostEqual(0.25, regions[0][0], delta=0.05)
        self.assertAlmostEqual(1.5, regions[1][0], delta=0.05)

    def test_repairs_collapsed_words_inside_activity_only(self):
        line = LrcLine(1.0, "eins zwei", "", words=[
            {"word": "eins", "start": 1.0, "end": 1.0},
            {"word": "zwei", "start": 1.0, "end": 1.0},
        ], source_timestamp=1.0)
        count = repair_with_vocal_activity([line], AlignmentConfig(), [(1.2, 2.0)])
        self.assertEqual(1, count)
        self.assertEqual(1.2, line.words[0]["start"])
        self.assertEqual(2.0, line.words[-1]["end"])
        self.assertTrue(all(word["timing_source"] == "vocal-activity-repair" for word in line.words))

    def test_repairs_only_tail_after_fixed_anchor(self):
        line = LrcLine(1.0, "fest danach", "", words=[
            {"word": "fest", "start": 1.0, "end": 1.5,
             "timing_source": "asr-repetition-anchor"},
            {"word": "danach", "start": 0.0, "end": 0.0},
        ], source_timestamp=1.0)
        count = repair_anchor_context([line], AlignmentConfig(), [(1.6, 2.2)])
        self.assertEqual(1, count)
        self.assertEqual(1.0, line.words[0]["start"])
        self.assertEqual(1.6, line.words[1]["start"])
        self.assertEqual("anchor-context-vocal-activity", line.words[1]["timing_source"])

    def test_repairs_immediate_words_after_anchor_without_moving_anchor(self):
        anchored = {"word": "weiter", "start": 10.0, "end": 10.5,
                    "timing_source": "asr-repetition-anchor"}
        line = LrcLine(10, "weiter ich", "", words=[
            anchored, {"word": "ich", "start": 9.0, "end": 9.0}
        ])
        following = LrcLine(12, "danach", "", source_timestamp=12.0)
        count = repair_anchor_line_tails([line, following], [(10.6, 11.2)])
        self.assertEqual(1, count)
        self.assertEqual(10.0, anchored["start"])
        self.assertEqual(10.6, line.words[1]["start"])
        self.assertEqual("anchor-tail-vocal-activity", line.words[1]["timing_source"])
