import unittest

from app.models import AlignmentConfig, LrcLine
from app.validator import validate
from app.vocal_boundaries import constrain_to_stage_vocals


class StageVocalBoundaryTests(unittest.TestCase):
    def test_coherent_phrase_snaps_to_stage_vocal_after_real_pause(self):
        previous = LrcLine(74.0, "previous", "", words=[
            {"word": "previous", "start": 74.0, "end": 74.8,
             "timing_source": "stable-ts-whisper"},
        ])
        line = LrcLine(77.624, "Die Vögel singen", "", words=[
            {"word": "Die", "start": 77.624, "end": 77.684,
             "timing_source": "coherent-sentence-ipa-path"},
            {"word": "Vögel", "start": 77.785, "end": 78.206,
             "timing_source": "coherent-sentence-ipa-path"},
            {"word": "singen", "start": 78.328, "end": 80.12,
             "timing_source": "coherent-sentence-ipa-path"},
        ])

        report = constrain_to_stage_vocals(
            [previous, line], [(73.9, 74.82), (77.35, 80.15)])

        self.assertEqual(1, report["phrase_onset_corrections"])
        self.assertEqual(77.35, line.timestamp)
        self.assertEqual(77.35, line.words[0]["start"])
        self.assertEqual(77.511, line.words[1]["start"])
        self.assertEqual(80.12, line.words[-1]["end"])

    def test_phrase_snap_needs_a_real_preceding_gap_and_complete_ipa_path(self):
        previous = LrcLine(9.2, "previous", "", words=[
            {"word": "previous", "start": 9.2, "end": 9.9,
             "timing_source": "stable-ts-whisper"},
        ])
        continuous = LrcLine(10.3, "continuous line", "", words=[
            {"word": "continuous", "start": 10.3, "end": 10.8,
             "timing_source": "coherent-sentence-ipa-path"},
            {"word": "line", "start": 10.8, "end": 11.4,
             "timing_source": "coherent-sentence-ipa-path"},
        ])
        heuristic = LrcLine(20.4, "heuristic line", "", words=[
            {"word": "heuristic", "start": 20.4, "end": 20.9,
             "timing_source": "stable-ts-whisper"},
            {"word": "line", "start": 20.9, "end": 21.4,
             "timing_source": "stable-ts-whisper"},
        ])

        report = constrain_to_stage_vocals(
            [previous, continuous, heuristic],
            [(9.2, 11.5), (20.0, 21.5)])

        self.assertEqual(0, report["phrase_onset_corrections"])
        self.assertEqual(10.3, continuous.words[0]["start"])
        self.assertEqual(20.4, heuristic.words[0]["start"])

    def test_long_word_cannot_jump_from_its_release_to_a_later_phrase(self):
        line = LrcLine(24.9, "an der Wand", "", words=[
            {"word": "an", "start": 24.9, "end": 25.2,
             "timing_source": "stable-ts-whisper"},
            {"word": "der", "start": 25.2, "end": 25.7,
             "timing_source": "stable-ts-whisper"},
            {"word": "Wand", "start": 25.7, "end": 28.38,
             "timing_source": "stable-ts-whisper"},
        ])

        report = constrain_to_stage_vocals(
            [line], [(24.74, 25.99), (28.64, 30.24)])

        self.assertEqual(1, report["release_corrections"])
        self.assertAlmostEqual(26.03, line.words[-1]["end"])
        self.assertEqual(2350.0, line.words[-1]["stage_vocal_release_trim_ms"])

    def test_onset_at_dying_previous_island_is_a_quality_conflict(self):
        line = LrcLine(30.215, "Von schnellen Autos", "", words=[
            {"word": "Von", "start": 30.215, "end": 31.40,
             "timing_source": "stable-ts-whisper"},
            {"word": "schnellen", "start": 31.40, "end": 32.0,
             "timing_source": "stable-ts-whisper"},
        ], source_timestamp=30.2)

        report = constrain_to_stage_vocals(
            [line], [(28.64, 30.24), (31.29, 32.2)])
        quality = validate([line], AlignmentConfig())

        self.assertEqual(1, report["onset_conflicts"])
        self.assertEqual(1075.0, line.words[0]["stage_vocal_onset_conflict_ms"])
        self.assertFalse(quality["quality"]["publishable"])
        self.assertEqual(1, quality["quality"]["stage_vocal_onset_conflicts"])

    def test_supported_held_word_is_not_trimmed(self):
        line = LrcLine(10.0, "Feuer", "", words=[
            {"word": "Feuer", "start": 10.0, "end": 12.45,
             "timing_source": "ctc-phoneme-alignment"},
        ])

        report = constrain_to_stage_vocals([line], [(9.98, 12.50)])

        self.assertEqual(0, report["release_corrections"])
        self.assertEqual(12.45, line.words[0]["end"])

    def test_verified_quiet_tonal_release_overrides_early_binary_vad_end(self):
        line = LrcLine(1.0, "Yacht", "", words=[
            {"word": "Yacht", "start": 1.0, "end": 2.02,
             "timing_source": "stable-ts-whisper",
             "sustain_tonal_release": 2.02,
             "sustain_release_confidence": 0.96},
        ])

        report = constrain_to_stage_vocals([line], [(0.98, 1.75)])

        self.assertEqual(0, report["release_corrections"])
        self.assertEqual(1, report["release_overrides"])
        self.assertEqual(2.02, line.words[0]["end"])

    def test_sub_perceptual_vad_edge_is_not_an_onset_conflict(self):
        line = LrcLine(31.26, "Von schnellen Autos", "", words=[
            {"word": "Von", "start": 31.26, "end": 31.5,
             "timing_source": "input-enhanced-lrc"},
        ])

        report = constrain_to_stage_vocals(
            [line], [(31.30, 31.8)])

        self.assertEqual(0, report["onset_conflicts"])
        self.assertNotIn("stage_vocal_onset_conflict_ms", line.words[0])

    def test_low_confidence_recovered_lead_is_trimmed_to_exact_stage_vocal(self):
        line = LrcLine(182.70, "We had not much to say", "", words=[
            {"word": "We", "start": 182.70, "end": 183.08,
             "timing_source": "instrumental-leakage-stable-ts",
             "stable_ts_probability": 0.0399},
            {"word": "had", "start": 183.08, "end": 183.26,
             "timing_source": "instrumental-leakage-stable-ts",
             "stable_ts_probability": 0.98},
        ])

        report = constrain_to_stage_vocals(
            [line], [(182.1, 182.73), (183.05, 184.0)])

        self.assertEqual(1, report["onset_corrections"])
        self.assertEqual(0, report["onset_conflicts"])
        self.assertEqual(183.05, line.timestamp)
        self.assertEqual(183.05, line.words[0]["start"])
        self.assertNotIn("stage_vocal_onset_conflict_ms", line.words[0])

    def test_source_anchored_pickup_is_reflowed_inside_exact_vocal_onset(self):
        line = LrcLine(58.58, "And that will validate", "", words=[
            {"word": "And", "start": 58.58, "end": 58.76,
             "timing_source": "stable-ts-source-anchor-interpolation"},
            {"word": "that", "start": 58.76, "end": 59.00,
             "timing_source": "stable-ts-source-anchor-interpolation"},
            {"word": "will", "start": 59.00, "end": 59.08,
             "timing_source": "stable-ts-whisper"},
            {"word": "validate", "start": 59.18, "end": 59.80,
             "timing_source": "stable-ts-whisper"},
        ])

        report = constrain_to_stage_vocals(
            [line], [(57.9, 58.61), (58.87, 60.0)])

        self.assertEqual(1, report["onset_corrections"])
        self.assertEqual(0, report["onset_conflicts"])
        self.assertEqual(58.87, line.timestamp)
        self.assertEqual(59.00, line.words[1]["end"])
        self.assertEqual(59.00, line.words[2]["start"])
        self.assertTrue(line.words[0]["stage_vocal_pickup_reflow"])

    def test_high_confidence_recovered_lead_remains_a_quality_conflict(self):
        line = LrcLine(182.70, "We had not much to say", "", words=[
            {"word": "We", "start": 182.70, "end": 183.08,
             "timing_source": "instrumental-leakage-stable-ts",
             "stable_ts_probability": 0.91},
        ])

        report = constrain_to_stage_vocals(
            [line], [(182.1, 182.73), (183.05, 184.0)])

        self.assertEqual(0, report["onset_corrections"])
        self.assertEqual(1, report["onset_conflicts"])
        self.assertEqual(182.70, line.words[0]["start"])


def _displaced_line(lane: int = 0):
    """A line whose three leading words end before the next vocal island."""
    return LrcLine(98.35, "wollen wir noch ein bisschen zusamm rumhängen", "",
                   words=[
                       {"word": "wollen", "start": 98.35, "end": 98.764},
                       {"word": "wir", "start": 98.764, "end": 98.971},
                       {"word": "noch", "start": 98.971, "end": 99.247},
                       {"word": "ein", "start": 99.247, "end": 99.454},
                       {"word": "bisschen", "start": 99.454, "end": 100.005},
                       {"word": "zusamm", "start": 100.005, "end": 100.419},
                       {"word": "rumhängen", "start": 100.419, "end": 101.04},
                   ], voice_lane=lane)


def _preceding_line():
    return LrcLine(93.35, "Nicht mehr lang", "", words=[
        {"word": "Nicht", "start": 93.35, "end": 94.0},
        {"word": "lang", "start": 94.0, "end": 96.71},
    ])


ISLANDS = [(93.35, 96.6), (99.4, 99.72), (99.78, 100.94), (100.96, 106.73)]


class SilentLeadingRunRecoveryTests(unittest.TestCase):
    """A line displaced further than its first word must still be detected."""

    def test_disabled_by_default_so_variant_1_2_is_unchanged(self):
        previous, line = _preceding_line(), _displaced_line()

        report = constrain_to_stage_vocals([previous, line], ISLANDS)

        self.assertFalse(report["silent_prefix_recovery"]["enabled"])
        self.assertEqual(0, report["silent_prefix_recovery"]["corrected_lines"])
        self.assertEqual(98.35, line.words[0]["start"])
        self.assertEqual(101.04, line.words[-1]["end"])
        # The historical detector cannot see this line: its first word ends
        # long before the island, so no conflict is reported either.
        self.assertEqual(0, report["onset_conflicts"])

    def test_leading_run_in_silence_translates_the_complete_line(self):
        previous, line = _preceding_line(), _displaced_line()

        report = constrain_to_stage_vocals(
            [previous, line], ISLANDS, silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(1, recovery["corrected_lines"])
        correction = recovery["corrections"][0]
        self.assertEqual("corrected", correction["status"])
        self.assertEqual(3, correction["silent_leading_words"])
        self.assertEqual(1050.0, correction["shift_ms"])
        # Rigid translation: unlike the coherent-IPA pickup the displaced
        # release moves with the onset instead of being retained.
        self.assertEqual(99.4, line.timestamp)
        self.assertEqual(99.4, line.words[0]["start"])
        self.assertEqual(102.09, line.words[-1]["end"])
        self.assertEqual(0.621, round(line.words[-1]["end"]
                                      - line.words[-1]["start"], 3))
        self.assertEqual(98.35, line.words[0]["stage_vocal_silent_prefix_original_start"])
        self.assertEqual(0, report["onset_conflicts"])

    def test_a_backing_phrase_in_another_lane_does_not_block_the_lead_line(self):
        previous, line = _preceding_line(), _displaced_line()
        backing = LrcLine(101.4, "Wohohohohoh", "", words=[
            {"word": "Wohohohohoh", "start": 101.4, "end": 103.96},
        ], voice_lane=1)

        report = constrain_to_stage_vocals(
            [previous, line, backing], ISLANDS, silent_prefix_recovery=True)

        self.assertEqual(1, report["silent_prefix_recovery"]["corrected_lines"])
        self.assertEqual(99.4, line.words[0]["start"])
        self.assertGreater(line.words[-1]["end"], backing.words[0]["start"])

    def test_a_lane_decided_but_not_yet_applied_already_unblocks_the_lead(self):
        # Lane promotion runs after this gate. Reading the stale lead lane
        # would reject the very correction the promotion is about to enable.
        previous, line = _preceding_line(), _displaced_line()
        backing = LrcLine(101.4, "Wohohohohoh", "", words=[
            {"word": "Wohohohohoh", "start": 101.4, "end": 103.96},
        ], voice_lane=0)

        report = constrain_to_stage_vocals(
            [previous, line, backing], ISLANDS, silent_prefix_recovery=True,
            pending_voice_lanes={2: 1})

        self.assertEqual(1, report["silent_prefix_recovery"]["corrected_lines"])
        self.assertEqual(99.4, line.words[0]["start"])
        # The projection stays read-only; promotion itself happens later.
        self.assertEqual(0, backing.voice_lane)

    def test_a_following_line_in_the_same_lane_blocks_the_translation(self):
        previous, line = _preceding_line(), _displaced_line()
        following = LrcLine(101.4, "Wohohohohoh", "", words=[
            {"word": "Wohohohohoh", "start": 101.4, "end": 103.96},
        ], voice_lane=0)

        report = constrain_to_stage_vocals(
            [previous, line, following], ISLANDS, silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(0, recovery["corrected_lines"])
        self.assertEqual("shifted-line-overruns-next-line-in-lane",
                         recovery["rejections"][0]["reason"])
        self.assertEqual(98.35, line.words[0]["start"])

    def test_missing_preceding_pause_is_rejected_with_a_reason(self):
        # Without an independently observed singing pause the next island may
        # simply be the continuation of the previous phrase, so a translation
        # would move the line onto somebody else's vocal.
        previous = LrcLine(93.35, "Nicht mehr lang", "", words=[
            {"word": "Nicht", "start": 93.35, "end": 94.0},
            {"word": "lang", "start": 94.0, "end": 96.71},
        ])
        line = LrcLine(96.8, "wollen wir noch", "", words=[
            {"word": "wollen", "start": 96.8, "end": 96.95},
            {"word": "wir", "start": 96.95, "end": 97.3},
            {"word": "noch", "start": 97.3, "end": 97.9},
        ])

        report = constrain_to_stage_vocals(
            [previous, line], [(93.35, 96.75), (97.0, 99.5)],
            silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(0, recovery["corrected_lines"])
        self.assertEqual("no-preceding-quiet-gap",
                         recovery["rejections"][0]["reason"])
        self.assertEqual(96.8, line.words[0]["start"])

    def test_island_beyond_the_search_radius_is_not_silently_grabbed(self):
        previous, line = _preceding_line(), _displaced_line()
        distant = [(93.35, 96.6), (101.4, 106.73)]

        report = constrain_to_stage_vocals(
            [previous, line], distant, silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(0, recovery["corrected_lines"])
        self.assertEqual("island-beyond-onset-search",
                         recovery["rejections"][0]["reason"])
        self.assertEqual(98.35, line.words[0]["start"])

    def test_a_line_already_inside_activity_is_left_alone(self):
        previous = _preceding_line()
        line = _displaced_line()
        inside = [(93.35, 96.6), (98.2, 106.73)]

        report = constrain_to_stage_vocals(
            [previous, line], inside, silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(0, recovery["corrected_lines"])
        self.assertEqual(0, recovery["rejected_lines"])
        self.assertEqual(98.35, line.words[0]["start"])

    def test_shifted_release_must_land_inside_measured_activity(self):
        previous, line = _preceding_line(), _displaced_line()
        # The target island is long enough to accept the onset but ends before
        # the translated final word, which would place the release in silence.
        short_tail = [(93.35, 96.6), (99.4, 99.9)]

        report = constrain_to_stage_vocals(
            [previous, line], short_tail, silent_prefix_recovery=True)

        recovery = report["silent_prefix_recovery"]
        self.assertEqual(0, recovery["corrected_lines"])
        self.assertEqual("shifted-release-outside-activity",
                         recovery["rejections"][0]["reason"])
        self.assertEqual(98.35, line.words[0]["start"])


if __name__ == "__main__":
    unittest.main()
