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
            "expected": {"start": 1, "end": 7, "unit": "träum weiter", "repetitions": 3},
            "recognized": {"unit": "neunmal tag", "repetitions": 3},
        }]}}

        requests = local_repetition_requests(lines, comparison, 60.0)

        self.assertEqual(18.0, requests[0]["start"])
        self.assertEqual(34.0, requests[0]["end"])
        self.assertEqual("träum weiter träum weiter träum weiter", requests[0]["transcript"])

    def test_asr_must_not_increase_the_canonical_repetition_count(self):
        lines = [LrcLine(20.0, "träum weiter träum weiter träum weiter", "")]
        comparison = {"repeated_blocks": {"paired": [{
            "expected": {"start": 0, "end": 6, "unit": "träum weiter",
                         "repetitions": 3},
            "recognized": {"unit": "träum weiter", "repetitions": 4},
        }]}}

        request = local_repetition_requests(lines, comparison, 60.0)[0]

        self.assertEqual("träum weiter träum weiter träum weiter", request["transcript"])

    def test_collapses_only_the_abnormally_close_extra_asr_repetition(self):
        comparison = {"repeated_blocks": {"paired": [{
            "expected": {"start": 0, "end": 6, "unit": "träum weiter",
                         "unit_words": 2, "repetitions": 3},
            "recognized": {"start": 0, "end": 8, "unit": "träum weiter",
                           "unit_words": 2, "repetitions": 4},
        }]}}
        recognized = [
            {"word": "träum", "start": 10.0, "end": 10.4},
            {"word": "weiter", "start": 10.4, "end": 10.8},
            {"word": "träum", "start": 11.2, "end": 11.6},
            {"word": "weiter", "start": 11.6, "end": 12.0},
            {"word": "träum", "start": 12.4, "end": 12.82},
            {"word": "weiter", "start": 12.82, "end": 12.83},
            {"word": "träum", "start": 12.84, "end": 13.1},
            {"word": "weiter", "start": 13.1, "end": 13.5},
        ]

        timed = timestamp_repetition_pairs(comparison, recognized)

        self.assertEqual(1, len(timed))
        self.assertEqual(1, timed[0]["recognized_repetitions_collapsed"])
        self.assertEqual(6, len(timed[0]["audio_words"]))
        self.assertEqual({"start": 12.4, "end": 13.1}, timed[0]["audio_words"][-2])
        self.assertEqual({"start": 13.1, "end": 13.5}, timed[0]["audio_words"][-1])

    def test_does_not_collapse_evenly_spaced_real_repetitions(self):
        comparison = {"repeated_blocks": {"paired": [{
            "expected": {"start": 0, "end": 4, "unit": "go", "repetitions": 4},
            "recognized": {"start": 0, "end": 5, "unit": "go", "repetitions": 5},
        }]}}
        recognized = [{"word": "go", "start": float(index), "end": index + .4}
                      for index in range(5)]

        self.assertEqual([], timestamp_repetition_pairs(comparison, recognized))

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

    def test_unrelated_bad_word_in_same_line_does_not_reject_local_anchor(self):
        pair = {
            "expected": {"start": 0, "end": 2},
            "audio_start": 10.0,
            "audio_end": 11.0,
            "expected_lrc_start": 10.0,
            "audio_words": [
                {"start": 10.0, "end": 10.4},
                {"start": 10.5, "end": 11.0},
            ],
        }
        line = LrcLine(10.0, "go now trailing", "", words=[
            {"word": "go", "start": 2.0, "end": 2.2},
            {"word": "now", "start": 2.2, "end": 2.4},
            {"word": "trailing", "start": 12.0, "end": 11.9},
        ])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(1, summary["blocks"])
        self.assertEqual(10.0, line.words[0]["start"])

    def test_anchor_tail_is_clipped_to_the_following_word_without_overlap(self):
        pair = {
            "expected": {"start": 0, "end": 2},
            "audio_start": 10.0,
            "audio_end": 11.13,
            "expected_lrc_start": 10.0,
            "audio_words": [
                {"start": 10.0, "end": 10.5},
                {"start": 10.5, "end": 11.13},
            ],
        }
        line = LrcLine(10.0, "dream again after", "", words=[
            {"word": "dream", "start": 10.0, "end": 10.4},
            {"word": "again", "start": 10.4, "end": 11.0},
            {"word": "after", "start": 11.08, "end": 11.5},
        ])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(1, summary["blocks"])
        self.assertEqual(11.08, line.words[1]["end"])
        self.assertEqual(50, line.words[1]["repetition_following_boundary_clip_ms"])

    def test_repetition_end_rescales_multiword_lexical_tail_instead_of_clipping(self):
        pair = {
            "expected": {"start": 0, "end": 2},
            "audio_start": 10.0,
            "audio_end": 11.13,
            "expected_lrc_start": 10.0,
            "audio_words": [
                {"start": 10.0, "end": 10.5},
                {"start": 10.5, "end": 11.13},
            ],
        }
        line = LrcLine(10.0, "dream again I remain", "", words=[
            {"word": "dream", "start": 10.0, "end": 10.4},
            {"word": "again", "start": 10.4, "end": 11.0},
            {"word": "I", "start": 11.08, "end": 11.45},
            {"word": "remain", "start": 11.45, "end": 12.1},
        ])

        summary = apply_repetition_anchors([line], [pair])

        self.assertEqual(1, summary["blocks"])
        self.assertEqual(11.13, line.words[1]["end"])
        self.assertEqual(11.13, line.words[2]["start"])
        self.assertEqual(12.1, line.words[3]["end"])
        self.assertEqual(2, summary["rescaled_following_tail_words"])
        self.assertNotIn("repetition_following_boundary_clip_ms", line.words[1])

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
