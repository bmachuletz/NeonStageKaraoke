import unittest

try:
    import numpy as np
    from app.models import LrcLine
    from app.onset_validation import apply_supported_onset_refinements, validate_line_onsets
except ImportError:
    np = None


@unittest.skipIf(np is None, "NumPy ist nur in der Audio-/GPU-Umgebung installiert")
class OnsetValidationTests(unittest.TestCase):
    def test_finds_energy_change_near_line_start(self):
        audio = np.zeros(32000, dtype=np.float32)
        audio[16000:24000] = np.sin(np.arange(8000) * 0.25).astype(np.float32)
        line = LrcLine(1.0, "Start", "", words=[{"word": "Start", "start": 1.0, "end": 1.4}])

        result = validate_line_onsets(audio, [line])

        self.assertEqual(1, result["checked_lines"])
        self.assertLessEqual(abs(result["measurements"][0]["delta_ms"]), 30)

    def test_applies_only_onset_not_owned_by_previous_line(self):
        previous = LrcLine(.5, "Vorher", "", words=[{"word": "Vorher", "start": .5, "end": .75}])
        current = LrcLine(1.0, "Start", "", words=[{"word": "Start", "start": 1.0, "end": 1.4}])
        report = {"measurements": [{"line": 2, "detected": .84, "delta_ms": -160,
                                     "prominence": 5.0}]}

        result = apply_supported_onset_refinements([previous, current], report)

        self.assertEqual(1, result["applied"])
        self.assertEqual(.84, current.words[0]["start"])


if __name__ == "__main__":
    unittest.main()
