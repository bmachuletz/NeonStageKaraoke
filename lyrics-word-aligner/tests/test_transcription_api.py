import unittest
from pathlib import Path

from app.main import _transcription_worker_command


class TranscriptionApiTests(unittest.TestCase):
    def test_worker_receives_optional_canonical_lyrics(self):
        command = _transcription_worker_command(
            "job", Path("audio.mp3"), Path("output"), "auto", True, "cuda",
            Path("canonical.lrc"))

        self.assertIn("--canonical", command)
        self.assertEqual("canonical.lrc", command[command.index("--canonical") + 1])
        self.assertIn("--separate", command)


if __name__ == "__main__":
    unittest.main()
