import os
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from app.basic_pitch_evidence import (analyze_and_refine_line_onsets,
                                      candidate_pitch_quality,
                                      _alignment_confidence, _consonant_lead, _fit_drift,
                                      _pitch_timeline, _refine_internal_word_boundaries,
                                      _robust_vocal_pitch_range,
                                      _refine_syllable_boundaries,
                                      _repetition_fingerprints, _word_pitch_evidence)
from app.boundary_evidence import LeakageReference


class BasicPitchEvidenceTests(unittest.TestCase):
    def test_unpitched_candidate_receives_no_separator_score_or_penalty(self):
        with patch.dict(os.environ, {"BASIC_PITCH_URL": "http://basic-pitch:8090"}), \
             patch("app.basic_pitch_evidence._request_evidence", return_value={
                 "notes": [], "onsets": [], "service": "basic-pitch"}):
            result = candidate_pitch_quality(np.zeros(32000, dtype=np.float32))
        self.assertFalse(result["eligible"])
        self.assertNotIn("quality", result)

    def test_supported_phrase_onset_moves_only_first_word(self):
        lines = [SimpleNamespace(timestamp=1.0, words=[
            {"word": "Hello", "start": 1.0, "end": 1.5},
            {"word": "world", "start": 1.6, "end": 2.0},
        ])]
        notes = [{"start": .91, "end": 1.4, "pitch": 64, "amplitude": .8}]
        with patch.dict(os.environ, {"BASIC_PITCH_URL": "http://basic-pitch:8090"}), \
             patch("app.basic_pitch_evidence._request_evidence", return_value={
                 "notes": notes, "onsets": [], "service": "spotify/basic-pitch-0.4.0"}), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": 4.2}):
            report = analyze_and_refine_line_onsets(
                lines, np.zeros(48000, dtype=np.float32),
                np.zeros(48000, dtype=np.float32))

        self.assertEqual(1, report["applied"])
        self.assertEqual(.91, lines[0].words[0]["start"])
        self.assertEqual(1.6, lines[0].words[1]["start"])
        self.assertEqual(64, lines[0].words[0]["basic_pitch_pitch"])

    def test_instrumental_correlated_note_is_diagnostic_only(self):
        lines = [SimpleNamespace(timestamp=1.0, words=[
            {"word": "Hello", "start": 1.0, "end": 1.5},
        ])]
        notes = [{"start": .9, "end": 1.4, "pitch": 60, "amplitude": .9}]
        with patch.dict(os.environ, {"BASIC_PITCH_URL": "http://basic-pitch:8090"}), \
             patch("app.basic_pitch_evidence._request_evidence", return_value={
                 "notes": notes, "onsets": [], "service": "spotify/basic-pitch-0.4.0"}), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": .4}):
            report = analyze_and_refine_line_onsets(
                lines, np.zeros(32000, dtype=np.float32),
                np.zeros(32000, dtype=np.float32))

        self.assertEqual(0, report["applied"])
        self.assertEqual(1.0, lines[0].words[0]["start"])
        self.assertFalse(report["details"][0]["accepted"])

    def test_raw_onset_is_preferred_over_decoded_note(self):
        lines = [SimpleNamespace(timestamp=1.0, words=[
            {"word": "Hello", "start": 1.0, "end": 1.5},
        ])]
        evidence = {
            "notes": [{"start": .84, "end": 1.5, "pitch": 60, "amplitude": .9}],
            "onsets": [{"time": .92, "pitch": 61, "confidence": .8}],
            "service": "spotify/basic-pitch-0.4.0",
        }
        with patch.dict(os.environ, {"BASIC_PITCH_URL": "http://basic-pitch:8090"}), \
             patch("app.basic_pitch_evidence._request_evidence", return_value=evidence), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": 4.0}):
            report = analyze_and_refine_line_onsets(
                lines, np.zeros(32000, dtype=np.float32),
                np.zeros(32000, dtype=np.float32))

        self.assertEqual(.92, lines[0].words[0]["start"])
        self.assertEqual("raw-onset", report["details"][0]["candidate_source"])

    def test_independent_pitch_release_can_adjust_final_word(self):
        lines = [SimpleNamespace(timestamp=1.0, words=[
            {"word": "Hello", "start": 1.0, "end": 1.5,
             "syllables": [{"text": "Hello", "start": 1.0, "end": 1.5}]},
        ])]
        evidence = {
            "notes": [{"start": 1.0, "end": 1.62, "pitch": 60, "amplitude": .9}],
            "onsets": [], "service": "spotify/basic-pitch-0.4.0",
        }
        with patch.dict(os.environ, {"BASIC_PITCH_URL": "http://basic-pitch:8090"}), \
             patch("app.basic_pitch_evidence._request_evidence", return_value=evidence), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": -4.0}):
            report = analyze_and_refine_line_onsets(
                lines, np.zeros(32000, dtype=np.float32),
                np.zeros(32000, dtype=np.float32))

        self.assertEqual(1, report["applied_releases"])
        self.assertEqual(1.62, lines[0].words[-1]["end"])
        self.assertEqual(1.62, lines[0].words[-1]["syllables"][-1]["end"])

    def test_robust_drift_requires_song_span_and_consistent_change(self):
        details = [{"old": float(time), "shift_ms": 10 + time}
                   for time in range(0, 141, 20)]
        drift = _fit_drift(details)

        self.assertTrue(drift["supported"])
        self.assertGreater(drift["change_ms"], 100)

    def test_vocal_pitch_range_rejects_remote_harmonic_outliers(self):
        notes = [{"pitch": 52 + index % 5, "amplitude": .8}
                 for index in range(30)]
        notes.append({"pitch": 104, "amplitude": .8})

        pitch_range = _robust_vocal_pitch_range(notes, .25)

        self.assertTrue(pitch_range["supported"])
        self.assertLess(pitch_range["maximum_midi"], 90)

    def test_consonant_lead_precedes_pitch_onset_only_with_band_evidence(self):
        def measured(_audio, timestamp, **_kwargs):
            strength = 5.0 if abs(timestamp - .88) < .006 else .2
            return {"independent_db": strength, "bands": {"sibilance": {}}}

        with patch("app.basic_pitch_evidence.banded_boundary_step", side_effect=measured):
            result = _consonant_lead(
                np.zeros(32000, dtype=np.float32), .92, 1.0,
                LeakageReference(np.zeros(32000, dtype=np.float32)), 16000, 1.5)

        self.assertIsNotNone(result)
        self.assertAlmostEqual(.88, result[0], places=2)
        self.assertGreater(result[1]["consonant_lead_ms"], 30)

    def test_repeated_lines_get_relative_pitch_fingerprints(self):
        lines = [
            SimpleNamespace(text="Unser Refrain", words=[
                {"word": "Unser", "start": 1.0, "end": 1.5},
                {"word": "Refrain", "start": 1.6, "end": 2.4}]),
            SimpleNamespace(text="Unser Refrain", words=[
                {"word": "Unser", "start": 5.0, "end": 5.5},
                {"word": "Refrain", "start": 5.6, "end": 6.4}]),
        ]
        notes = [
            {"start": 1.1, "pitch": 60}, {"start": 1.5, "pitch": 62},
            {"start": 2.0, "pitch": 64}, {"start": 5.1, "pitch": 65},
            {"start": 5.5, "pitch": 67}, {"start": 6.0, "pitch": 69},
        ]

        report = _repetition_fingerprints(lines, notes)

        self.assertEqual(1, report["repeated_groups"])
        self.assertEqual(1.0, report["comparisons"][0]["similarity"])
        self.assertTrue(report["groups"][0]["placement_supported"])

    def test_internal_word_boundary_needs_pitch_and_independent_attack(self):
        lines = [SimpleNamespace(words=[
            {"word": "hello", "start": 1.0, "end": 1.5},
            {"word": "world", "start": 1.5, "end": 2.0},
        ])]
        summary = {"applied_internal_boundaries": 0,
                   "internal_boundary_details": []}
        onsets = [{"time": 1.55, "pitch": 64, "confidence": .8}]
        with patch.dict(os.environ, {"BASIC_PITCH_INTERNAL_WORD_MODE": "select"}), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": 4.2}):
            _refine_internal_word_boundaries(
                lines, onsets, np.zeros(48000, dtype=np.float32),
                LeakageReference(np.zeros(48000, dtype=np.float32)), 16000, summary)

        self.assertEqual(1, summary["applied_internal_boundaries"])
        self.assertEqual(1.55, lines[0].words[0]["end"])
        self.assertEqual(1.55, lines[0].words[1]["start"])

    def test_internal_word_boundary_is_shadow_only_by_default(self):
        lines = [SimpleNamespace(words=[
            {"word": "hello", "start": 1.0, "end": 1.5},
            {"word": "world", "start": 1.5, "end": 2.0},
        ])]
        summary = {"applied_internal_boundaries": 0,
                   "internal_boundary_details": []}
        with patch.dict(os.environ, {}, clear=True), \
             patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": 8.0}):
            _refine_internal_word_boundaries(
                lines, [{"time": 1.55, "pitch": 64, "confidence": .9}],
                np.zeros(48000, dtype=np.float32),
                LeakageReference(np.zeros(48000, dtype=np.float32)), 16000, summary)

        self.assertEqual(0, summary["applied_internal_boundaries"])
        self.assertEqual(1, summary["supported_internal_boundaries"])
        self.assertEqual(1.5, lines[0].words[1]["start"])

    def test_nearby_pitch_attack_confirms_existing_word_boundary(self):
        lines = [SimpleNamespace(words=[
            {"word": "hello", "start": 1.0, "end": 1.5},
            {"word": "world", "start": 1.5, "end": 2.0},
        ])]
        summary = {"applied_internal_boundaries": 0,
                   "internal_boundary_details": []}
        with patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": 6.0}):
            _refine_internal_word_boundaries(
                lines, [{"time": 1.515, "pitch": 64, "confidence": .9}],
                np.zeros(48000, dtype=np.float32),
                LeakageReference(np.zeros(48000, dtype=np.float32)), 16000, summary)

        self.assertEqual(1, summary["confirmed_internal_boundaries"])
        self.assertEqual("confirmed-existing",
                         summary["internal_boundary_details"][0]["application"])
        self.assertEqual(1.5, lines[0].words[1]["start"])

    def test_pitch_change_without_acoustic_attack_does_not_split_word(self):
        lines = [SimpleNamespace(words=[
            {"word": "melisma", "start": 1.0, "end": 2.0,
             "syllables": [
                 {"text": "me", "start": 1.0, "end": 1.5},
                 {"text": "lisma", "start": 1.5, "end": 2.0},
             ]},
        ])]
        notes = [
            {"start": 1.1, "end": 1.48, "pitch": 60, "amplitude": .8},
            {"start": 1.54, "end": 1.9, "pitch": 64, "amplitude": .8},
        ]
        summary = {"applied_syllable_boundaries": 0,
                   "syllable_boundary_details": []}
        with patch("app.basic_pitch_evidence.banded_boundary_step",
                   return_value={"independent_db": .4}):
            _refine_syllable_boundaries(
                lines, notes, np.zeros(48000, dtype=np.float32),
                LeakageReference(np.zeros(48000, dtype=np.float32)), 16000, summary)

        self.assertEqual(0, summary["applied_syllable_boundaries"])
        self.assertEqual(1.5, lines[0].words[0]["syllables"][1]["start"])

    def test_pitch_timeline_assigns_notes_to_lines(self):
        lines = [SimpleNamespace(words=[
            {"word": "hello", "start": 1.0, "end": 2.0},
        ])]
        timeline = _pitch_timeline(lines, [
            {"start": 1.2, "end": 1.8, "pitch": 64, "amplitude": .8},
            {"start": 3.0, "end": 3.2, "pitch": 60, "amplitude": .1},
        ])

        self.assertEqual(1, timeline["event_count"])
        self.assertEqual(1, timeline["events"][0]["line"])
        self.assertEqual("quantized-note-events-not-continuous-f0",
                         timeline["representation"])

    def test_word_pitch_evidence_reports_coverage_and_sustain(self):
        lines = [SimpleNamespace(words=[
            {"word": "hold", "start": 1.0, "end": 1.8},
        ])]
        report = _word_pitch_evidence(lines, [
            {"start": 1.05, "end": 1.75, "pitch": 64, "amplitude": .8},
        ])

        self.assertEqual(1, report["words_with_pitch"])
        self.assertEqual(1, report["sustained_words"])
        self.assertEqual(64, report["entries"][0]["dominant_midi"])
        self.assertGreater(report["entries"][0]["voiced_coverage"], .8)

    def test_alignment_confidence_flags_large_accepted_shift(self):
        report = _alignment_confidence({
            "details": [{"line": 2, "accepted": True, "shift_ms": -170}],
            "release_details": [], "internal_boundary_details": [],
            "repetition_fingerprints": {"comparisons": []},
            "pitch_timeline": {"events": [{"line": 2}]},
        })

        self.assertEqual(1, report["measured_lines"])
        self.assertEqual("independent-support-strength-not-ground-truth-accuracy",
                         report["meaning"])
        self.assertEqual("large-onset-shift", report["review_lines"][0]["reasons"][0])


if __name__ == "__main__":
    unittest.main()
