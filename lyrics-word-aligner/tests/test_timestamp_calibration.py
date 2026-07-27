import unittest

from app.models import LrcLine
from app.timestamp_calibration import calibrate_source_timestamps
from app.transcript_match import compare_transcripts


class TimestampCalibrationTests(unittest.TestCase):
    def test_applies_trimmed_intro_offset_to_all_hints(self):
        lines = [
            LrcLine(float(index * 10), f"line {index} words here", "", words=[
                {"word": "line", "start": float(index * 10), "end": float(index * 10) + .2}
            ], source_timestamp=float(index * 10))
            for index in range(5)
        ]
        recognized = []
        for index in range(5):
            for word_index, word in enumerate(("line", str(index), "words", "here")):
                start = index * 10 + 17.5 + word_index * .25
                recognized.append({"word": word, "start": start, "end": start + .2})
        expected = " ".join(line.text for line in lines)
        transcript = " ".join(word["word"] for word in recognized)

        result = calibrate_source_timestamps(
            lines, recognized, compare_transcripts(expected, transcript)
        )

        self.assertTrue(result["applied"])
        self.assertAlmostEqual(17.5, result["offset"], places=2)
        self.assertAlmostEqual(17.5, lines[0].source_timestamp, places=2)
        self.assertAlmostEqual(17.7, lines[0].words[0]["end"], places=2)

    def test_does_not_move_already_matching_timestamps(self):
        lines = [LrcLine(float(index * 10), f"known phrase {index}", "",
                         source_timestamp=float(index * 10)) for index in range(5)]
        words = []
        for index in range(5):
            for offset, word in enumerate(("known", "phrase", str(index))):
                words.append({"word": word, "start": index * 10 + offset * .2,
                              "end": index * 10 + offset * .2 + .1})
        result = calibrate_source_timestamps(
            lines, words,
            compare_transcripts(" ".join(line.text for line in lines),
                                " ".join(word["word"] for word in words)),
        )
        self.assertFalse(result["applied"])


if __name__ == "__main__":
    unittest.main()
