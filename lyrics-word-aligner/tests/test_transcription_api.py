import unittest
from pathlib import Path

from app.main import _transcription_worker_command, _worker_command


class TranscriptionApiTests(unittest.TestCase):
    def test_alignment_worker_receives_existing_stage_stems(self):
        command = _worker_command(
            "job", Path("audio.mp3"), Path("lyrics.lrc"), Path("output"),
            "auto", True, "cuda", Path("vocals.flac"),
            Path("instrumental.flac"))

        self.assertIn("--provided-vocals", command)
        self.assertEqual("vocals.flac", command[command.index("--provided-vocals") + 1])
        self.assertIn("--provided-instrumental", command)
        self.assertEqual(
            "instrumental.flac", command[command.index("--provided-instrumental") + 1])
        self.assertIn("--separate", command)

    def test_alignment_worker_has_no_selectable_profile(self):
        command = _worker_command(
            "job", Path("audio.mp3"), Path("lyrics.lrc"), Path("output"),
            "auto", True, "cuda")

        self.assertNotIn("--alignment-profile", command)

    def test_worker_receives_optional_canonical_lyrics(self):
        command = _transcription_worker_command(
            "job", Path("audio.mp3"), Path("output"), "auto", True, "cuda",
            Path("canonical.lrc"))

        self.assertIn("--canonical", command)
        self.assertEqual("canonical.lrc", command[command.index("--canonical") + 1])
        self.assertIn("--separate", command)


if __name__ == "__main__":
    unittest.main()
