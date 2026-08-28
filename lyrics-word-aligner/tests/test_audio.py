import unittest
import tempfile
from pathlib import Path

import numpy as np
import soundfile as sf

from app.audio import load_native_audio, select_alignment_audio


class AlignmentAudioSelectionTests(unittest.TestCase):
    def test_uses_mix_when_separator_removed_nearly_all_signal(self):
        vocals = np.full(16000, 0.001, dtype=np.float32)
        mix = np.full(16000, 0.1, dtype=np.float32)

        selected, report = select_alignment_audio(vocals, mix)

        self.assertIs(selected, mix)
        self.assertTrue(report["fallback_used"])
        self.assertEqual("original-mix", report["source"])

    def test_keeps_healthy_vocal_stem(self):
        vocals = np.full(16000, 0.03, dtype=np.float32)
        mix = np.full(16000, 0.1, dtype=np.float32)

        selected, report = select_alignment_audio(vocals, mix)

        self.assertIs(selected, vocals)
        self.assertFalse(report["fallback_used"])
        self.assertEqual("separated-vocals", report["source"])

    def test_native_boundary_audio_preserves_export_sample_rate(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stage-vocals.flac"
            sf.write(path, np.linspace(-.1, .1, 4410, dtype=np.float32), 44100)

            audio, sample_rate = load_native_audio(path)

        self.assertEqual(44100, sample_rate)
        self.assertEqual(4410, len(audio))
