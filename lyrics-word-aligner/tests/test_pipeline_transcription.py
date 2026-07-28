import unittest
from unittest.mock import patch

import numpy as np

from app.pipeline import _transcribe_for_verification


class _EmptyWholeTrackSession:
    def __init__(self):
        self.lengths = []

    def transcribe(self, audio, _language, prompt=None, *, max_new_tokens=None):
        self.lengths.append(len(audio))
        text = "" if len(audio) > 20 * 16000 else f"block {len(self.lengths)}"
        return {"model": "fake", "language": "German", "text": text}


class PipelineTranscriptionTests(unittest.TestCase):
    def test_empty_whole_track_verification_retries_in_bounded_windows(self):
        session = _EmptyWholeTrackSession()
        audio = np.zeros(60 * 16000, dtype=np.float32)
        with patch.dict("os.environ", {
            "LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS": "300",
            "LRC_TRANSCRIPTION_CHUNK_SECONDS": "20",
            "LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS": "2",
        }):
            result = _transcribe_for_verification(session, audio, "auto", None)

        self.assertEqual(60 * 16000, session.lengths[0])
        self.assertTrue(all(length <= 20 * 16000 for length in session.lengths[1:]))
        self.assertTrue(result["chunked"])
        self.assertEqual(4, result["chunks"])
        self.assertEqual(4, result["nonempty_chunks"])


if __name__ == "__main__":
    unittest.main()
