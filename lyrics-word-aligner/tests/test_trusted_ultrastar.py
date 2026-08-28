import base64
import json
import tempfile
import unittest
from pathlib import Path

from app.lrc import parse_lrc
from app.trusted_ultrastar import (apply_rigid_offset, choose_rigid_offset,
                                   resolve_recording_offset)


class TrustedUltraStarTests(unittest.TestCase):
    def test_small_perceptual_lead_is_preserved(self):
        offset, diagnostic = choose_rigid_offset(10.0, [(10.18, 11.0)])
        self.assertEqual(0.0, offset)
        self.assertEqual("ultrastar-perceptual-lead-preserved", diagnostic["reason"])

    def test_large_recording_offset_is_rigid(self):
        offset, diagnostic = choose_rigid_offset(10.0, [(12.85, 13.7)])
        self.assertAlmostEqual(2.85, offset)
        self.assertTrue(diagnostic["applied"])

    def test_server_edge_measurement_survives_missing_first_vocal_detection(self):
        offset, diagnostic = resolve_recording_offset(
            ["[neon-usdb-recording-offset:2.850]"], 0.0,
            {"applied": False, "reason": "no-nearby-vocal-onset"})
        self.assertAlmostEqual(2.85, offset)
        self.assertEqual("server-verified-edge-silence-offset", diagnostic["reason"])

    def test_conflicting_independent_offsets_fail_closed(self):
        with self.assertRaises(ValueError):
            resolve_recording_offset(
                ["[neon-usdb-recording-offset:2.850]"], 1.2,
                {"applied": True, "reason": "recording-level-leading-offset"})

    def test_rigid_shift_preserves_word_and_syllable_durations(self):
        payload = {
            "Line": 0, "Word": 0, "Text": "away", "Start": 10.0, "End": 11.0,
            "WordManuallyAdjusted": False,
            "Syllables": [
                {"Text": "a", "Start": 10.0, "End": 10.3, "ManuallyAdjusted": False},
                {"Text": "way", "Start": 10.3, "End": 11.0, "ManuallyAdjusted": False},
            ],
        }
        token = base64.urlsafe_b64encode(json.dumps(payload).encode()).decode().rstrip("=")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "chart.lrc"
            path.write_text(
                f"[neon-editor-syllables:{token}]\n"
                "[00:10.000]<00:10.000,00:11.000>away\n", encoding="utf-8")
            headers, lines = parse_lrc(path)
        shifted_headers = apply_rigid_offset(headers, lines, 2.85)
        word = lines[0].words[0]
        self.assertAlmostEqual(12.85, word["start"])
        self.assertAlmostEqual(13.85, word["end"])
        self.assertAlmostEqual(1.0, word["end"] - word["start"])
        encoded = shifted_headers[0].split(":", 1)[1][:-1]
        decoded = json.loads(base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4)))
        self.assertAlmostEqual(12.85, decoded["Start"])
        self.assertAlmostEqual(13.15, decoded["Syllables"][0]["End"])


if __name__ == "__main__":
    unittest.main()
