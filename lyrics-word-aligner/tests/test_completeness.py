import unittest

from app.completeness import (apply_completeness_gate, apply_targeted_gap_results,
                              assess_lyric_completeness,
                              constrain_lyrics_before_nonlexical_vocalizations)
from app.models import LrcLine


def line(start: float, end: float, text: str = "known words") -> LrcLine:
    value = LrcLine(timestamp=start, text=text, original="", source_timestamp=start)
    value.words = [{"word": word, "start": start, "end": end} for word in text.split()]
    return value


class CompletenessTests(unittest.TestCase):
    def test_rejects_asr_backed_vocal_gap_between_lyrics(self):
        lines = [line(0.0, 2.0), line(10.0, 12.0)]
        words = [
            {"word": "missing", "start": 5.0, "end": 5.4},
            {"word": "chorus", "start": 5.5, "end": 5.9},
            {"word": "here", "start": 6.0, "end": 6.4},
        ]
        result = assess_lyric_completeness(lines, [(4.9, 6.5)], words, 12.0)
        self.assertFalse(result["complete"])
        summary = {"quality": {"score": 100.0, "grade": "excellent", "publishable": True}}
        apply_completeness_gate(summary, result)
        self.assertEqual(79.9, summary["quality"]["score"])
        self.assertFalse(summary["quality"]["publishable"])


    def test_ignores_instrumental_gap_without_recognized_words(self):
        result = assess_lyric_completeness(
            [line(0.0, 2.0), line(10.0, 12.0)], [(4.9, 6.5)], [], 12.0
        )
        self.assertTrue(result["complete"])
        self.assertTrue(result["requires_targeted_reanalysis"])
        self.assertEqual(1, len(result["investigation_regions"]))

    def test_targeted_nonlexical_adlibs_do_not_fail_lyrics_completeness(self):
        completeness = {
            "complete": True, "suspicious_gaps": [],
            "investigation_regions": [{"start": 4.9, "end": 6.5}],
        }
        reanalysis = {"unresolved_regions": [
            {"start": 4.9, "end": 5.4, "targeted_transcript": "Hey."},
            {"start": 5.5, "end": 6.5, "targeted_transcript": "Ah, ooh, ooh."},
        ]}

        apply_targeted_gap_results(completeness, reanalysis)

        self.assertTrue(completeness["complete"])
        self.assertEqual([], completeness["suspicious_gaps"])
        self.assertEqual(2, len(completeness["vocalization_regions"]))

    def test_targeted_semantic_phrase_still_fails_lyrics_completeness(self):
        completeness = {"complete": True, "suspicious_gaps": []}
        reanalysis = {"unresolved_regions": [
            {"start": 4.9, "end": 6.5,
             "targeted_transcript": "A completely missing lyric line."},
        ]}

        apply_targeted_gap_results(completeness, reanalysis)

        self.assertFalse(completeness["complete"])
        self.assertEqual(1, len(completeness["suspicious_gaps"]))

    def test_nonlexical_adlib_does_not_extend_previous_lyric_word(self):
        lyrics = [line(1.0, 5.7, "play")]
        lyrics[0].words[0]["acoustic_end"] = 3.22
        reanalysis = {"unresolved_regions": [{
            "start": 3.22, "end": 8.0,
            "targeted_transcript": "Ah ah ah ah.",
        }]}

        result = constrain_lyrics_before_nonlexical_vocalizations(
            lyrics, reanalysis)

        self.assertEqual(1, result["adjusted_words"])
        self.assertEqual(3.285, lyrics[0].words[0]["end"])

    def test_semantic_following_phrase_never_trims_a_word(self):
        lyrics = [line(1.0, 5.7, "play")]
        lyrics[0].words[0]["acoustic_end"] = 3.22

        result = constrain_lyrics_before_nonlexical_vocalizations(
            lyrics, {"unresolved_regions": [{
                "start": 3.22, "end": 8.0,
                "targeted_transcript": "A missing lyric phrase.",
            }]})

        self.assertEqual(0, result["adjusted_words"])
        self.assertEqual(5.7, lyrics[0].words[0]["end"])


if __name__ == "__main__":
    unittest.main()
