import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

from app.analysis_stems import build_analysis_candidates
from app.separator import StemPaths
from app.stem_roles import SeparationConfig, StemPurpose, separator_family


class StemRoleTests(unittest.TestCase):
    def test_analysis_candidate_directory_exists_before_first_conversion(self):
        with tempfile.TemporaryDirectory() as root:
            temporary_directory = Path(root) / "new-analysis-directory"

            def convert(_source, output):
                self.assertTrue(output.parent.is_dir())
                return np.zeros(16000)

            config = SeparationConfig(
                analysis_enabled=False,
                analysis_models=(),
                a_b_models=(),
                stage_model="stage.ckpt",
                original_mix_mode="off",
                original_mix_ratio=0.15,
                keep_candidates=False,
            )
            with patch("app.analysis_stems._mono", side_effect=convert):
                result = build_analysis_candidates(
                    Path(root) / "song.flac", temporary_directory, None, config,
                    minimum_rms_ratio=0.05,
                )

            self.assertTrue(temporary_directory.is_dir())
            self.assertEqual("legacy-analysis-fallback", result.candidates[0].id)

    def test_models_and_purposes_are_configurable(self):
        with patch.dict(os.environ, {
            "LRC_ANALYSIS_SEPARATOR_ENABLED": "true",
            "LRC_ANALYSIS_SEPARATOR_MODELS": "analysis-a.ckpt,analysis-b.ckpt",
            "LRC_SEPARATOR_A_B_MODELS": "analysis-b.ckpt,ab-candidate.ckpt",
            "LRC_STAGE_SEPARATOR_MODEL": "stage.ckpt",
            "LRC_ORIGINAL_MIX_BLEND_MODE": "candidate",
            "LRC_ORIGINAL_MIX_BLEND_RATIO": ".12",
        }, clear=True):
            config = SeparationConfig.from_environment()
        self.assertEqual(
            ("analysis-a.ckpt", "analysis-b.ckpt", "ab-candidate.ckpt"),
            config.analysis_models)
        self.assertEqual(("ab-candidate.ckpt",), config.a_b_models)
        self.assertEqual("stage.ckpt", config.stage_model)
        self.assertEqual("candidate", config.original_mix_mode)
        self.assertNotEqual(StemPurpose.ANALYSIS, StemPurpose.STAGE)

    def test_separator_family_is_metadata_not_business_logic(self):
        self.assertEqual("bs-roformer", separator_family("model_bs_roformer_x.ckpt"))
        self.assertEqual("mel-band-roformer",
                         separator_family("model_mel_band_roformer_x.ckpt"))

    def test_optional_analysis_separator_failure_keeps_stage_pair_untouched(self):
        config = SeparationConfig(True, ("broken.ckpt", "good.ckpt"), (),
                                  "stage.ckpt", "off", .15, False)
        stage = StemPaths(Path("stage-vocals.wav"), Path("stage-instrumental.wav"))
        good = StemPaths(Path("analysis-vocals.wav"), Path("analysis-rest.wav"))

        def separate(_audio, _output, *, model_name):
            if model_name == "broken.ckpt":
                raise RuntimeError("optional model failed")
            return good

        with patch("app.analysis_stems.separate_stems", side_effect=separate), \
             patch("app.analysis_stems._mono", return_value=np.zeros(16000)):
            result = build_analysis_candidates(
                Path("song.wav"), Path("temporary"), stage, config,
                minimum_rms_ratio=.05)

        self.assertEqual(1, len(result.errors))
        self.assertEqual("good.ckpt", result.candidates[0].metadata["model"])
        self.assertTrue(result.candidates[0].legacy)
        self.assertIs(good, result.stem_pairs[result.candidates[0].id])
        self.assertEqual(Path("stage-vocals.wav"), stage.vocals)


if __name__ == "__main__":
    unittest.main()
