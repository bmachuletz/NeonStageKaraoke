import unittest
from pathlib import Path

from app.alignment_profiles import ALIGNMENT_PROFILES
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

    def test_alignment_worker_receives_research_shadow_profile(self):
        command = _worker_command(
            "job", Path("audio.mp3"), Path("lyrics.lrc"), Path("output"),
            "auto", True, "cuda", alignment_profile="research-shadow")

        self.assertIn("--alignment-profile", command)
        self.assertEqual(
            "research-shadow", command[command.index("--alignment-profile") + 1])

    def test_editor_guided_profile_is_part_of_shared_worker_contract(self):
        command = _worker_command(
            "job", Path("audio.mp3"), Path("lyrics.lrc"), Path("output"),
            "auto", True, "cuda", alignment_profile="editor-guided")

        self.assertIn("editor-guided", ALIGNMENT_PROFILES)
        self.assertEqual(
            "editor-guided", command[command.index("--alignment-profile") + 1])

    def test_worker_receives_optional_canonical_lyrics(self):
        command = _transcription_worker_command(
            "job", Path("audio.mp3"), Path("output"), "auto", True, "cuda",
            Path("canonical.lrc"))

        self.assertIn("--canonical", command)
        self.assertEqual("canonical.lrc", command[command.index("--canonical") + 1])
        self.assertIn("--separate", command)


if __name__ == "__main__":
    unittest.main()
