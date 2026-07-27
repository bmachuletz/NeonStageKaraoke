import unittest

from app.models import LrcLine
from app.repetition_anchors import (apply_local_timestamp, apply_repetition_anchors,
                                    local_repetition_requests,
                                    refine_stretched_repetition_anchors,
                                    timestamp_repetition_pairs)


class RepetitionAnchorTests(unittest.TestCase):
    def test_timestamps_and_applies_structural_repetition_anchor(self):
        comparison = {"repeated_blocks": {"paired": [{
            "expected": {"start": 0, "end": 4, "unit": "träum weiter", "repetitions": 2},
            "recognized": {"start": 0, "end": 4, "unit": "neunmal tag", "repetitions": 2},
            "confidence": 0.9,
        }]}}
        recognized = [
            {"word": "neunmal", "start": 10.0, "end": 10.4},
            {"word": "tag", "start": 10.4, "end": 10.8},
            {"word": "neunmal", "start": 11.0, "end": 11.4},
            {"word": "tag", "start": 11.4, "end": 11.8},
        ]
        line = LrcLine(0, "", "", words=[
            {"word": word, "start": 9.8, "end": 9.8}
            for word in ("träum", "weiter", "träum", "weiter")
        ])

        timed = timestamp_repetition_pairs(comparison, recognized)
        summary = apply_repetition_anchors([line], timed)

        self.assertEqual(1, summary["blocks"])
        self.assertEqual(10.0, line.words[0]["start"])
        self.assertEqual(11.8, line.words[-1]["end"])
        self.assertEqual(10.0, line.timestamp)
        self.assertTrue(all(word["end"] > word["start"] for word in line.words))
        self.assertTrue(all(word["timing_source"] == "asr-repetition-anchor" for word in line.words))

    def test_rejects_an_anchor_far_from_existing_timed_lrc_window(self):
        pair = {"expected": {"start": 0, "end": 1}, "audio_start": 40.0, "audio_end": 41.0}
        line = LrcLine(10, "wort", "", words=[{"word": "wort", "start": 10.0, "end": 10.2}])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(0, summary["blocks"])
        self.assertEqual(1, summary["rejected_blocks"])
        self.assertEqual("rejected", pair["anchor_status"])
        self.assertEqual(10.0, line.words[0]["start"])

    def test_uses_original_lrc_reference_instead_of_bad_candidate_alignment(self):
        pair = {"expected": {"start": 0, "end": 1}, "audio_start": 10.2, "audio_end": 11.0,
                "expected_lrc_start": 10.0}
        line = LrcLine(10, "wort", "", words=[{"word": "wort", "start": 30.0, "end": 30.2}])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(1, summary["blocks"])
        self.assertEqual(10.2, line.words[0]["start"])

    def test_geometric_repair_does_not_overwrite_asr_anchor(self):
        from app.models import AlignmentConfig
        from app.repair import repair_collapsed_timings

        line = LrcLine(10, "wort", "", words=[
            {"word": "wort", "start": 10.0, "end": 10.0,
             "timing_source": "asr-repetition-anchor"},
        ])
        self.assertEqual(0, repair_collapsed_timings([line], AlignmentConfig()))
        self.assertEqual(10.0, line.words[0]["end"])

    def test_builds_a_bounded_request_from_original_lrc_lines(self):
        lines = [
            LrcLine(10.0, "intro", ""),
            LrcLine(20.0, "träum weiter träum weiter", ""),
            LrcLine(24.0, "träum weiter ende", ""),
            LrcLine(30.0, "danach", ""),
        ]
        comparison = {"repeated_blocks": {"paired": [{
            "expected": {"start": 1, "end": 7},
            "recognized": {"unit": "neunmal tag", "repetitions": 3},
        }]}}

        requests = local_repetition_requests(lines, comparison, 60.0)

        self.assertEqual(18.0, requests[0]["start"])
        self.assertEqual(34.0, requests[0]["end"])
        self.assertEqual("neunmal tag neunmal tag neunmal tag", requests[0]["transcript"])

    def test_offsets_local_alignment_to_song_time(self):
        pair = {}
        applied = apply_local_timestamp(pair, [
            {"word": "a", "start": 1.0, "end": 1.4},
            {"word": "b", "start": 2.0, "end": 2.5},
        ], 18.0)
        self.assertTrue(applied)
        self.assertEqual(19.0, pair["audio_start"])
        self.assertEqual(20.5, pair["audio_end"])
        self.assertEqual([
            {"start": 19.0, "end": 19.4},
            {"start": 20.0, "end": 20.5},
        ], pair["audio_words"])
        self.assertEqual("local-lrc-bounded-asr-alignment", pair["timestamp_method"])

    def test_applies_measured_repetition_word_bounds_without_stretching_silence(self):
        pair = {
            "expected": {"start": 0, "end": 3},
            "audio_start": 10.0,
            "audio_end": 35.0,
            "audio_words": [
                {"start": 10.0, "end": 10.5},
                {"start": 20.0, "end": 20.5},
                {"start": 34.0, "end": 35.0},
            ],
        }
        line = LrcLine(10.0, "party party party", "", words=[
            {"word": "party", "start": 10.0, "end": 10.1} for _ in range(3)
        ])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(1, summary["blocks"])
        self.assertEqual([0.5, 0.5, 1.0], [
            word["end"] - word["start"] for word in line.words
        ])
        self.assertEqual([10.0, 20.0, 34.0], [word["start"] for word in line.words])

    def test_refines_stretched_calls_with_distinct_vocal_islands(self):
        first = LrcLine(10.0, "party", "", words=[
            {"word": "party", "start": 10.0, "end": 18.0,
             "timing_source": "asr-repetition-anchor"}])
        second = LrcLine(18.0, "party", "", words=[
            {"word": "party", "start": 18.0, "end": 26.0,
             "timing_source": "asr-repetition-anchor"}])

        result = refine_stretched_repetition_anchors(
            [first, second], [(10.5, 12.0), (20.0, 22.0)]
        )

        self.assertEqual(2, result["refined_words"])
        self.assertEqual((10.5, 12.0),
                         (first.words[0]["start"], first.words[0]["end"]))
        self.assertEqual("asr-repetition-activity", second.words[0]["timing_source"])
