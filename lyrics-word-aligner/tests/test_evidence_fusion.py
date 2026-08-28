import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from app.evidence_fusion import fuse_syllable_evidence


def line_with_syllables(syllables):
    return SimpleNamespace(words=[{
        "word": "forever", "start": syllables[0][1], "end": syllables[-1][2],
        "confidence": .9, "timing_source": "qwen-forced",
        "syllables": [
            {"text": text, "start": start, "end": end}
            for text, start, end in syllables
        ],
    }])


class EvidenceFusionTests(unittest.TestCase):
    def test_independent_pitch_and_spectral_evidence_can_refine_existing_syllable(self):
        lines = [line_with_syllables([("for", 1.0, 1.46), ("ever", 1.46, 2.0)])]
        basic_pitch = {"enabled": True, "service": "basic-pitch", "notes": [{
            "start": 1.49, "end": 1.9, "pitch": 64, "amplitude": .9,
        }]}
        with patch("app.evidence_fusion.banded_boundary_step",
                   return_value={"independent_db": 5.0}):
            report = fuse_syllable_evidence(
                lines, basic_pitch, np.zeros(48000), np.zeros(48000), mode="select")
        self.assertEqual(1, report["selected_boundaries"])
        self.assertEqual(1.49, lines[0].words[0]["syllables"][1]["start"])
        self.assertGreaterEqual(report["decisions"][0]["independent_families"], 3)

    def test_melisma_does_not_create_syllables_from_multiple_notes(self):
        lines = [line_with_syllables([("love", 1.0, 2.0)])]
        basic_pitch = {"enabled": True, "notes": [
            {"start": 1.1, "end": 1.4, "pitch": 60, "amplitude": .9},
            {"start": 1.5, "end": 1.9, "pitch": 64, "amplitude": .9},
        ]}
        report = fuse_syllable_evidence(
            lines, basic_pitch, np.zeros(48000), None, mode="select")
        self.assertEqual(0, report["candidate_boundaries"])
        self.assertEqual(1, len(lines[0].words[0]["syllables"]))

    def test_mix_candidate_without_matching_accompaniment_stays_diagnostic(self):
        lines = [line_with_syllables([("for", 1.0, 1.46), ("ever", 1.46, 2.0)])]
        basic_pitch = {"enabled": True, "notes": [{
            "start": 1.49, "end": 1.9, "pitch": 64, "amplitude": .9,
        }]}
        with patch("app.evidence_fusion.banded_boundary_step",
                   return_value={"independent_db": 8.0}):
            report = fuse_syllable_evidence(
                lines, basic_pitch, np.zeros(48000), None, mode="select")
        self.assertEqual(0, report["selected_boundaries"])
        self.assertEqual(1.46, lines[0].words[0]["syllables"][1]["start"])

    def test_same_note_across_syllables_does_not_remove_textual_boundary(self):
        lines = [line_with_syllables([("for", 1.0, 1.5), ("ever", 1.5, 2.0)])]
        basic_pitch = {"enabled": True, "notes": [
            {"start": 1.0, "end": 2.0, "pitch": 60, "amplitude": .9},
        ]}
        report = fuse_syllable_evidence(
            lines, basic_pitch, np.zeros(48000), None, mode="select")
        self.assertEqual(0, report["selected_boundaries"])
        self.assertEqual(1.5, lines[0].words[0]["syllables"][1]["start"])

    def test_unpitched_vocal_is_not_penalized_or_mutated(self):
        lines = [line_with_syllables([("shout", 1.0, 1.5)])]
        report = fuse_syllable_evidence(
            lines, {"enabled": True, "notes": []}, np.zeros(32000), None,
            mode="select")
        self.assertEqual("no-basic-pitch-events", report["reason"])
        self.assertNotIn("score_penalty", report)


if __name__ == "__main__":
    unittest.main()
