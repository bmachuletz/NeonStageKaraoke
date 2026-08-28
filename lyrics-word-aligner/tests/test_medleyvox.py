import os
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from app.medleyvox import (_activity_regions, _adjacent_lead_proposals,
                           _cluster_solo_voice_lines,
                           _preferred_source_for_lane,
                           _preferred_source_from_proposals,
                           _line_voice_proposals,
                           _separation_evidence, _stabilize_source_identity,
                           analyze_multiple_singing_voices,
                           apply_multiple_singing_voice_proposals,
                           plan_duet_windows, planned_voice_lanes)
from app.models import LrcLine


class MedleyVoxTests(unittest.TestCase):
    def test_plans_only_nonlexical_or_existing_secondary_voice_windows(self):
        lines = [
            SimpleNamespace(timestamp=10.0, text="normal text", words=[], voice_lane=0),
            SimpleNamespace(timestamp=20.0, text="Wohohohoh", words=[
                {"start": 20.2, "end": 22.0}], voice_lane=0),
            SimpleNamespace(timestamp=40.0, text="second singer", words=[
                {"start": 40.0, "end": 42.0}], voice_lane=1),
        ]
        self.assertEqual([(18.2, 24.0), (38.0, 44.0)], plan_duet_windows(lines, 60.0))

    def test_adjacent_candidate_windows_are_merged(self):
        lines = [
            SimpleNamespace(timestamp=10.0, text="la la la", words=[
                {"start": 10.0, "end": 12.0}], voice_lane=0),
            SimpleNamespace(timestamp=13.0, text="oh oh oh", words=[
                {"start": 13.0, "end": 15.0}], voice_lane=0),
        ]
        self.assertEqual([(8.0, 17.0)], plan_duet_windows(lines, 30.0))

    def test_energy_evidence_requires_two_materially_active_sources(self):
        rate = 1000
        time = np.arange(2000) / rate
        first = np.sin(2 * np.pi * 100 * time).astype(np.float32) * .2
        second = np.sin(2 * np.pi * 160 * time).astype(np.float32) * .15
        result = _separation_evidence([first, second], rate)
        self.assertTrue(result["plausible_overlap"])
        silent = _separation_evidence([first, np.zeros_like(first)], rate)
        self.assertFalse(silent["plausible_overlap"])

    def test_disabled_shadow_never_loads_audio_or_model(self):
        with patch.dict(os.environ, {"LRC_MEDLEYVOX_ENABLED": "false"}):
            result = analyze_multiple_singing_voices("missing.wav", [], "unused")
        self.assertFalse(result["enabled"])
        self.assertEqual("feature-disabled", result["reason"])

    def test_promote_applies_only_accepted_non_manual_backing_phrase(self):
        line = SimpleNamespace(timestamp=10.0, source_timestamp=10.0,
            text="Wohoho", words=[{"word": "Wohoho", "start": 10.0, "end": 11.0}],
            voice_lane=0, voice_label=None, manual_adjusted=False)
        report = {"mode": "promote", "lane_proposals": [{
            "text": "Wohoho", "source_start": 10.0, "start": 10.4, "end": 12.2,
            "confidence": .88, "accepted": True}]}
        summary = apply_multiple_singing_voice_proposals([line], report)
        self.assertEqual(1, summary["applied"])
        self.assertEqual(1, line.voice_lane)
        self.assertEqual("Backing Vocals", line.voice_label)
        self.assertAlmostEqual(10.4, line.words[0]["start"])
        self.assertAlmostEqual(12.2, line.words[0]["end"])

    def test_promote_never_overwrites_manual_line(self):
        line = SimpleNamespace(timestamp=10.0, source_timestamp=10.0,
            text="Wohoho", words=[{"word": "Wohoho", "start": 10.0, "end": 11.0}],
            voice_lane=0, voice_label=None, manual_adjusted=True)
        report = {"mode": "promote", "lane_proposals": [{
            "text": "Wohoho", "source_start": 10.0, "start": 10.4, "end": 12.2,
            "confidence": .88, "accepted": True}]}
        summary = apply_multiple_singing_voice_proposals([line], report)
        self.assertEqual(0, summary["applied"])
        self.assertEqual(0, line.voice_lane)

    def test_singer_identity_reorders_swapped_disjoint_window(self):
        prototypes = [None, None]
        first = [np.full(2000, .1, dtype=np.float32),
                 np.full(2000, .2, dtype=np.float32)]
        identity_vectors = {
            id(first[0]): np.array([1., 0.], dtype=np.float32),
            id(first[1]): np.array([0., 1.], dtype=np.float32),
        }
        with patch("app.medleyvox._voice_fingerprint",
                   side_effect=lambda audio, _rate: identity_vectors[id(audio)]):
            ordered, _ = _stabilize_source_identity(first, 1000, prototypes)
        self.assertIs(ordered[0], first[0])

        second = [np.full(2000, .3, dtype=np.float32),
                  np.full(2000, .4, dtype=np.float32)]
        identity_vectors = {
            id(second[0]): np.array([0., 1.], dtype=np.float32),
            id(second[1]): np.array([1., 0.], dtype=np.float32),
        }
        with patch("app.medleyvox._voice_fingerprint",
                   side_effect=lambda audio, _rate: identity_vectors[id(audio)]):
            ordered, report = _stabilize_source_identity(second, 1000, prototypes)
        self.assertTrue(report["swapped"])
        self.assertIs(ordered[0], second[1])

    def test_manual_lane_votes_require_stable_majority(self):
        anchors = [
            {"voice_lane": 1, "source_candidate": 2, "accepted": True},
            {"voice_lane": 1, "source_candidate": 2, "accepted": True},
            {"voice_lane": 1, "source_candidate": 1, "accepted": False},
        ]
        self.assertEqual(1, _preferred_source_for_lane(anchors, 1))

    def test_repeated_clear_proposals_establish_automatic_backing_source(self):
        proposals = [
            {"accepted": True, "voice_candidate": 2, "margin": .12},
            {"accepted": True, "voice_candidate": 2, "margin": .31},
            {"accepted": False, "voice_candidate": 1, "margin": .02},
        ]
        self.assertEqual(1, _preferred_source_from_proposals(proposals))

    def test_shadow_margin_compares_singers_not_two_islands_of_same_singer(self):
        line = SimpleNamespace(timestamp=10.0, source_timestamp=10.0,
            text="Wohoho", words=[{"start": 10.0, "end": 12.0}])
        with patch("app.medleyvox._activity_regions", side_effect=[
                [(10.0, 12.0), (10.04, 12.04)], [(10.7, 12.7)]]):
            proposals = _line_voice_proposals(
                [line], [(8.0, 14.0)], [{"plausible_overlap": True}],
                [np.ones(100), np.ones(100)], 10,
                compare_distinct_sources=True)
        self.assertTrue(proposals[0]["accepted"])
        self.assertGreater(proposals[0]["margin"], .08)

    def test_shadow_solo_clustering_proposes_only_stable_minority_run(self):
        rate = 1000
        audio = np.zeros(12000, dtype=np.float32)
        lines = []
        for index in range(10):
            start = index + .05
            end = index + .85
            audio[int(start * rate):int(end * rate)] = .1 if index < 6 else .2
            lines.append(SimpleNamespace(
                timestamp=start, text=f"line {index}", voice_lane=0,
                words=[{"start": start, "end": end}]))

        def fingerprint(segment, _sample_rate):
            return (np.array([1., 0.], dtype=np.float32)
                    if float(np.mean(segment)) < .15
                    else np.array([0., 1.], dtype=np.float32))

        with patch("app.medleyvox._voice_fingerprint", side_effect=fingerprint):
            result = _cluster_solo_voice_lines(audio, rate, lines)
        self.assertEqual("stable-two-singer-clusters", result["reason"])
        self.assertEqual(4, result["accepted_lines"])
        self.assertTrue(all(not item["accepted"] for item in result["proposals"][:6]))
        self.assertTrue(all(item["accepted"] for item in result["proposals"][6:]))

    def test_shadow_uses_complementary_source_for_adjacent_lead_phrases(self):
        lines = [
            SimpleNamespace(timestamp=8.0, source_timestamp=8.0,
                text="lead before", words=[{"start": 8.0, "end": 10.0}]),
            SimpleNamespace(timestamp=10.0, source_timestamp=10.0,
                text="Wohoho", words=[{"start": 10.0, "end": 12.0}]),
            SimpleNamespace(timestamp=12.8, source_timestamp=12.8,
                text="lead after", words=[{"start": 12.8, "end": 15.0}]),
        ]
        backing = [{"accepted": True, "voice_candidate": 2,
                    "source_start": 10.0, "start": 10.2, "end": 12.1}]
        with patch("app.medleyvox._activity_regions",
                   return_value=[(8.8, 10.8), (12.1, 14.98)]):
            proposals = _adjacent_lead_proposals(
                lines, [(7.0, 16.0)], backing,
                [np.ones(200), np.ones(200)], np.ones(200), 10)
        self.assertEqual(2, len(proposals))
        self.assertTrue(all(item["accepted"] for item in proposals))
        self.assertAlmostEqual(8.8, proposals[0]["start"])
        self.assertAlmostEqual(12.1, proposals[1]["start"])

    def test_activity_hysteresis_keeps_quiet_sustain_attached(self):
        audio = np.zeros(3000, dtype=np.float32)
        audio[500:1000] = .5
        audio[1000:1900] = .08
        regions = _activity_regions(audio, 1000, 0, 3)
        self.assertTrue(any(start <= .5 and end >= 1.88 for start, end in regions))


if __name__ == "__main__":
    unittest.main()


class PlannedVoiceLaneProjectionTests(unittest.TestCase):
    """The lane projection must predict promotion without performing it."""

    def _lines(self):
        return [
            LrcLine(99.15, "wollen wir noch", "", words=[
                {"word": "wollen", "start": 99.15, "end": 99.6}]),
            LrcLine(101.04, "(Wohohohohoh)", "", words=[
                {"word": "Wohohohohoh", "start": 101.04, "end": 101.96}],
                source_timestamp=101.04),
        ]

    def test_accepted_lane_proposal_is_projected_without_mutation(self):
        lines = self._lines()
        report = {"mode": "promote", "lane_proposals": [
            {"text": "(Wohohohohoh)", "source_start": 101.04, "accepted": True,
             "start": 101.4, "end": 103.96}]}

        planned = planned_voice_lanes(lines, report)

        self.assertEqual({1: 1}, planned)
        self.assertEqual(0, lines[1].voice_lane)
        self.assertEqual(101.04, lines[1].words[0]["start"])

    def test_rejected_and_shadow_mode_proposals_are_not_projected(self):
        lines = self._lines()
        rejected = {"mode": "promote", "lane_proposals": [
            {"text": "(Wohohohohoh)", "source_start": 101.04, "accepted": False,
             "start": 101.4, "end": 103.96}]}
        shadow = {"mode": "shadow", "lane_proposals": [
            {"text": "(Wohohohohoh)", "source_start": 101.04, "accepted": True,
             "start": 101.4, "end": 103.96}]}

        self.assertEqual({}, planned_voice_lanes(lines, rejected))
        self.assertEqual({}, planned_voice_lanes(lines, shadow))

    def test_projection_matches_the_applied_promotion(self):
        projected_lines, applied_lines = self._lines(), self._lines()
        report = {"mode": "promote", "lane_proposals": [
            {"text": "(Wohohohohoh)", "source_start": 101.04, "accepted": True,
             "start": 101.4, "end": 103.96, "confidence": 0.58}]}

        planned = planned_voice_lanes(projected_lines, report)
        apply_multiple_singing_voice_proposals(applied_lines, dict(report))

        self.assertEqual(planned,
                         {index: line.voice_lane
                          for index, line in enumerate(applied_lines)
                          if line.voice_lane})
