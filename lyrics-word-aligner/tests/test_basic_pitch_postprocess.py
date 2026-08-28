import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

from app.basic_pitch_postprocess import run


class BasicPitchPostprocessTests(unittest.TestCase):
    def test_treatment_is_derived_from_control_and_keeps_baseline_report(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            lrc = root / "control.lrc"
            lrc.write_text("[00:01.000]<00:01.000,00:01.500>Hello\n", encoding="utf-8")
            baseline = root / "control.alignment.json"
            baseline.write_text(json.dumps({"baseline_marker": 42}), encoding="utf-8")
            output = root / "output"
            evidence = {"enabled": True, "applied": 0,
                        "repetition_fingerprints": {"comparisons": []}}
            with patch("app.basic_pitch_postprocess.ffmpeg_to_mono16k",
                       side_effect=lambda source, target: target), \
                 patch("app.basic_pitch_postprocess.load_audio",
                       return_value=np.zeros(32000, dtype=np.float32)), \
                 patch("app.basic_pitch_postprocess.analyze_and_refine_line_onsets",
                       return_value=evidence):
                result = run(
                    root / "song.flac", lrc, output, language="en", separator=True,
                    device="cuda", provided_vocals=root / "vocals.ogg",
                    provided_instrumental=root / "instrumental.ogg",
                    baseline_report=baseline)

            report = json.loads((output / result["output_report"]).read_text())
            self.assertEqual(42, report["baseline_marker"])
            self.assertTrue(report["ab_baseline"]["shared"])
            self.assertFalse(report["ab_baseline"]["stochastic_models_rerun"])
            self.assertEqual("basic-pitch-postprocess", report["alignment_profile"])


if __name__ == "__main__":
    unittest.main()
