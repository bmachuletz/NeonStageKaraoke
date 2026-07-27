import unittest

from app.gap_recovery import best_lyric_block, recover_vocal_gap_lines
from app.models import LrcLine


def line(start: float, text: str) -> LrcLine:
    value = LrcLine(timestamp=start, text=text, original="", source_timestamp=start)
    value.words = [{"word": word, "start": start, "end": start + .3}
                   for word in text.split()]
    return value


class FakeTranscriber:
    def transcribe(self, _audio, _language, prompt=None):
        if prompt is not None:
            raise AssertionError("Vocal-gap windows must not receive a whole-song prompt")
        return {"model": "fake", "text": "we sing the chorus"}


class FakeAligner:
    def align_text(self, _audio, transcript, _language):
        return [{"word": word, "start": .22 + index * .3, "end": .48 + index * .3}
                for index, word in enumerate(transcript.split())]


class SourceLineAligner:
    def align_text(self, _audio, transcript, _language):
        return [{"word": word, "start": .5 + index * .35, "end": .8 + index * .35}
                for index, word in enumerate(transcript.split())]


class PickupAligner:
    def align_text(self, _audio, transcript, _language):
        words = transcript.split()
        if len(words) == 1:
            return [{"word": words[0], "start": .18, "end": .5}]
        return [{"word": word, "start": 1.8 + index * .3, "end": 2.05 + index * .3}
                for index, word in enumerate(words)]


class GapRecoveryTests(unittest.TestCase):
    def test_finds_short_known_line_without_whole_song_context(self):
        lines = [line(2, "this is a verse"), line(8, "we sing the chorus"), line(14, "another verse")]
        match = best_lyric_block(lines, "we sing the chorus")
        self.assertIsNotNone(match)
        self.assertEqual(1, match["first_line"])
        self.assertEqual(1, match["line_count"])

    def test_recovers_known_chorus_inside_vocal_only_gap(self):
        lines = [line(2, "this is a verse"), line(8, "we sing the chorus")]
        audio = [0.0] * (20 * 16000)
        result = recover_vocal_gap_lines(
            audio, lines, [{"start": 12.0, "end": 13.7, "vocal_activity_seconds": 1.5}],
            "en", "cpu", prompt="a deliberately huge song prompt",
            transcribe_session=FakeTranscriber(), align_session=FakeAligner())
        self.assertEqual(1, result["recovered_regions"])
        self.assertEqual(1, result["recovered_lines"])
        self.assertEqual(3, len(lines))
        self.assertEqual("targeted-vocal-gap-recovery", lines[-1].words[0]["timing_source"])

    def test_realigns_delayed_existing_line_at_trusted_source_cue(self):
        lines = [line(8, "the previous line"), line(12.2, "and then we continue"), line(17, "last line")]
        lines[0].words[-1]["end"] = 10.8
        for word in lines[1].words:
            word["start"], word["end"] = 13.5, 14.0
        result = recover_vocal_gap_lines(
            [0.0] * (20 * 16000), lines,
            [{"start": 12.0, "end": 12.9, "vocal_activity_seconds": .9}],
            "en", "cpu", transcribe_session=FakeTranscriber(), align_session=SourceLineAligner())
        self.assertEqual(1, result["realigned_existing_lines"])
        self.assertLess(lines[1].words[0]["start"], 13.0)
        self.assertEqual("targeted-vocal-gap-source-line", lines[1].words[0]["timing_source"])

    def test_recovers_short_pickup_when_full_sentence_stays_late(self):
        lines = [line(8, "the previous line"), line(12.2, "and then we continue"), line(17, "last line")]
        lines[0].words[-1]["end"] = 10.8
        for word in lines[1].words:
            word["start"], word["end"] = 13.5, 14.0
        result = recover_vocal_gap_lines(
            [0.0] * (20 * 16000), lines,
            [{"start": 12.0, "end": 12.9, "vocal_activity_seconds": .9}],
            "en", "cpu", transcribe_session=FakeTranscriber(), align_session=PickupAligner())
        self.assertEqual(1, result["realigned_existing_lines"])
        self.assertEqual("targeted-vocal-gap-pickup", lines[1].words[0]["timing_source"])


if __name__ == "__main__":
    unittest.main()
