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

    def test_repairs_positive_duration_words_that_overlap_each_other(self):
        line = LrcLine(38.2, "to live but", "", words=[
            {"word": "to", "start": 39.28, "end": 39.32},
            {"word": "live", "start": 39.29, "end": 39.34},
            {"word": "but", "start": 39.30, "end": 39.36},
        ], source_timestamp=38.2)

        count = repair_with_vocal_activity(
            [line], AlignmentConfig(), [(38.2, 39.0), (39.1, 40.1)])

        self.assertEqual(1, count)
        self.assertTrue(all(left["end"] <= right["start"]
                            for left, right in zip(line.words, line.words[1:])))
        self.assertTrue(all(word["timing_source"] == "vocal-activity-repair"
                            for word in line.words))

    def test_preserves_valid_phrase_with_a_few_short_function_words(self):
        line = LrcLine(38.2, "I got a lot of toys", "", words=[
            {"word": "I", "start": 38.20, "end": 38.22},
            {"word": "got", "start": 38.30, "end": 38.70},
            {"word": "a", "start": 38.80, "end": 38.82},
            {"word": "lot", "start": 38.90, "end": 39.20},
            {"word": "of", "start": 39.30, "end": 39.38},
            {"word": "toys", "start": 39.45, "end": 39.90},
        ], source_timestamp=38.2)
        original = [(word["start"], word["end"]) for word in line.words]

        count = repair_with_vocal_activity(
            [line], AlignmentConfig(), [(38.15, 39.95)])

        self.assertEqual(0, count)
        self.assertEqual(original, [(word["start"], word["end"]) for word in line.words])

    def test_repairs_only_small_tail_collisions_without_moving_phrase_onset(self):
        line = LrcLine(128.7, "I don't know how to read but I got a lot of toys", "", words=[
            {"word": "I", "start": 128.70, "end": 128.72},
            {"word": "don't", "start": 128.841, "end": 129.081},
            {"word": "know", "start": 129.101, "end": 129.221},
            {"word": "how", "start": 129.362, "end": 129.522},
            {"word": "to", "start": 129.743, "end": 129.923},
            {"word": "read", "start": 130.043, "end": 130.143},
            {"word": "but", "start": 130.184, "end": 130.444},
            {"word": "I", "start": 130.564, "end": 130.584},
            {"word": "got", "start": 130.66, "end": 130.85},
            {"word": "a", "start": 131.005, "end": 131.045},
            {"word": "lot", "start": 131.019, "end": 131.059},
            {"word": "of", "start": 131.066, "end": 131.106},
            {"word": "toys", "start": 131.085, "end": 131.21},
        ], source_timestamp=128.7)

        count = repair_with_vocal_activity(
            [line], AlignmentConfig(), [(128.68, 131.25)])

        self.assertEqual(1, count)
        self.assertEqual(128.7, line.words[0]["start"])
        self.assertEqual(130.66, line.words[8]["start"])
        self.assertTrue(all(left["end"] <= right["start"]
                            for left, right in zip(line.words, line.words[1:])))

    def test_preserves_fast_valid_punk_phrase(self):
        words = []
        for index, token in enumerate("I don't know how to read but I got a lot of toys".split()):
            start = 128.7 + index * .24
            words.append({"word": token, "start": start, "end": start + .08})
        line = LrcLine(128.7, "fast phrase", "", words=words, source_timestamp=128.7)
        original = [(word["start"], word["end"]) for word in words]

        count = repair_with_vocal_activity(
            [line], AlignmentConfig(), [(128.65, 132.0)])

        self.assertEqual(0, count)
        self.assertEqual(original, [(word["start"], word["end"]) for word in line.words])

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
