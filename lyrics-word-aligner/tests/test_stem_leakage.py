import unittest

import numpy as np

from app.models import LrcLine
from app.stem_leakage import recover_complementary_stem_lines


class ComplementaryStemRecoveryTests(unittest.TestCase):
    @staticmethod
    def _line() -> LrcLine:
        return LrcLine(8.8, "There we were standing around", "",
                       source_timestamp=10.0, words=[
            {"word": word, "start": 8.8 + index * 0.01,
             "end": 8.805 + index * 0.01,
             "timing_source": "sofa-singing-alignment", "sofa_confidence": 0.43}
            for index, word in enumerate(("There", "we", "were", "standing", "around"))
        ])

    @staticmethod
    def _recognized() -> dict:
        return {
            "text": "There we were standing around We had not much to say",
            "words": [
                {"word": "There", "start": .42, "end": .70, "probability": .7},
                {"word": "we", "start": .70, "end": 1.00, "probability": .99},
                {"word": "were", "start": 1.00, "end": 1.34, "probability": .99},
                {"word": "standing", "start": 1.34, "end": 1.92, "probability": .85},
                {"word": "around", "start": 1.92, "end": 2.76, "probability": .98},
                {"word": "We", "start": 3.72, "end": 4.10, "probability": .9},
                {"word": "had", "start": 4.10, "end": 4.32, "probability": .9},
            ],
        }

    def test_recovers_complete_line_from_instrumental_near_trusted_lrc_cue(self):
        line = self._line()
        following = LrcLine(14.0, "We had not much to say", "", source_timestamp=14.0)
        vocal = np.zeros(20 * 16000, dtype=np.float32)
        instrumental = np.full(20 * 16000, .03, dtype=np.float32)

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line, following], "en", "cpu",
            transcribe_fn=lambda _audio, _language, _device: self._recognized())

        self.assertEqual(1, result["candidate_lines"])
        self.assertEqual(1, result["recovered_lines"])
        self.assertAlmostEqual(10.04, line.words[0]["start"], places=3)
        self.assertAlmostEqual(12.38, line.words[-1]["end"], places=3)
        self.assertTrue(all(word["timing_source"] == "instrumental-leakage-stable-ts"
                            for word in line.words))
        self.assertEqual("There", line.words[0]["word"])

    def test_does_not_touch_line_when_vocal_stem_contains_signal(self):
        line = self._line()
        vocal = np.full(20 * 16000, .02, dtype=np.float32)
        instrumental = np.full(20 * 16000, .03, dtype=np.float32)

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line], "en", "cpu",
            transcribe_fn=lambda _audio, _language, _device: self._recognized())

        self.assertEqual(0, result["candidate_lines"])
        self.assertEqual(8.8, line.words[0]["start"])

    def test_unrelated_complementary_audio_is_reported_but_not_applied(self):
        line = self._line()
        vocal = np.zeros(20 * 16000, dtype=np.float32)
        instrumental = np.full(20 * 16000, .03, dtype=np.float32)
        unrelated = {
            "text": "completely different instrumental noise",
            "words": [
                {"word": word, "start": .5 + index * .3,
                 "end": .7 + index * .3, "probability": .95}
                for index, word in enumerate(
                    ("completely", "different", "instrumental", "noise", "today"))
            ],
        }

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line], "en", "cpu",
            transcribe_fn=lambda _audio, _language, _device: unrelated)

        self.assertEqual(0, result["recovered_lines"])
        self.assertEqual("no-complete-local-match", result["diagnostics"][0]["status"])
        self.assertEqual(8.8, line.words[0]["start"])

    def test_recovers_premature_successor_on_original_mix_before_overlap_repair(self):
        line = self._line()
        following_text = "We had not much to say our eyes were on the ground"
        following = LrcLine(11.4, following_text, "", source_timestamp=14.0, words=[
            {"word": word, "start": 11.4 + index * .2,
             "end": 11.58 + index * .2,
             "timing_source": "sofa-singing-alignment", "sofa_confidence": .43}
            for index, word in enumerate(following_text.split())
        ])
        vocal = np.zeros(24 * 16000, dtype=np.float32)
        vocal[int(13.9 * 16000):] = .02
        instrumental = np.full(24 * 16000, .03, dtype=np.float32)
        original_mix = np.full(24 * 16000, .04, dtype=np.float32)
        calls = 0

        def transcribe(_audio, _language, _device):
            nonlocal calls
            calls += 1
            if calls == 1:
                return self._recognized()
            words = following_text.split()
            return {
                "text": following_text,
                "words": [
                    {"word": word, "start": .46 + index * .28,
                     "end": .70 + index * .28, "probability": .95}
                    for index, word in enumerate(words)
                ],
            }

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line, following], "en", "cpu",
            transcribe_fn=transcribe, recognition_audio=original_mix,
            recognition_audio_source="original-mix")

        self.assertEqual(2, result["candidate_lines"])
        self.assertEqual(2, result["recovered_lines"])
        self.assertEqual("premature-overlap-successor",
                         result["diagnostics"][1]["candidate_reason"])
        self.assertAlmostEqual(14.08, following.words[0]["start"], places=3)
        self.assertTrue(all(word["stem_leakage_audio_source"] == "original-mix"
                            for word in following.words))

    def test_recovers_delayed_low_confidence_successor_near_trusted_cue(self):
        line = self._line()
        following_text = "We had not much to say our eyes were on the ground"
        following = LrcLine(15.55, following_text, "", source_timestamp=14.0, words=[
            {"word": word, "start": 15.55 + index * .02,
             "end": 15.565 + index * .02,
             "timing_source": "sofa-singing-alignment", "sofa_confidence": .43}
            for index, word in enumerate(following_text.split())
        ])
        vocal = np.zeros(24 * 16000, dtype=np.float32)
        vocal[int(13.9 * 16000):] = .02
        instrumental = np.full(24 * 16000, .03, dtype=np.float32)
        original_mix = np.full(24 * 16000, .04, dtype=np.float32)
        calls = 0

        def transcribe(_audio, _language, _device):
            nonlocal calls
            calls += 1
            if calls == 1:
                return self._recognized()
            words = following_text.split()
            return {
                "text": following_text,
                "words": [
                    {"word": word, "start": .46 + index * .28,
                     "end": .70 + index * .28, "probability": .95}
                    for index, word in enumerate(words)
                ],
            }

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line, following], "en", "cpu",
            transcribe_fn=transcribe, recognition_audio=original_mix,
            recognition_audio_source="original-mix")

        self.assertEqual(2, result["candidate_lines"])
        self.assertEqual(2, result["recovered_lines"])
        self.assertEqual("low-confidence-displaced-successor",
                         result["diagnostics"][1]["candidate_reason"])
        self.assertAlmostEqual(14.08, following.words[0]["start"], places=3)

    def test_prompt_is_allowed_only_after_unprompted_near_match_on_alternative_stem(self):
        text = "The tire hum made our burdens slip away"
        line = LrcLine(11.0, text, "", source_timestamp=10.0, words=[
            {"word": word, "start": 11.0 + index * .01,
             "end": 11.005 + index * .01,
             "timing_source": "sofa-singing-alignment", "sofa_confidence": .43}
            for index, word in enumerate(text.split())
        ])
        vocal = np.zeros(20 * 16000, dtype=np.float32)
        instrumental = np.full(20 * 16000, .03, dtype=np.float32)
        alternative = np.full(20 * 16000, .02, dtype=np.float32)
        unprompted_calls = 0
        prompted_calls = 0

        def transcribe(chunk, _language, _device):
            nonlocal unprompted_calls
            unprompted_calls += 1
            if float(np.mean(chunk)) < .01:
                return {"text": "unrelated noise", "words": []}
            words = "The tire made a burden slip away".split()
            return {"text": " ".join(words), "words": [
                {"word": word, "start": .4 + index * .3,
                 "end": .65 + index * .3, "probability": .9}
                for index, word in enumerate(words)
            ]}

        def prompted(_chunk, _language, _device, prompt):
            nonlocal prompted_calls
            prompted_calls += 1
            self.assertEqual(text, prompt)
            return {"text": text, "words": [
                {"word": word, "start": .4 + index * .3,
                 "end": .65 + index * .3, "probability": .9}
                for index, word in enumerate(text.split())
            ]}

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line], "en", "cpu",
            transcribe_fn=transcribe, prompted_transcribe_fn=prompted,
            recognition_candidates=[("original-mix", np.zeros_like(vocal)),
                                    ("alternative-vocals", alternative)])

        self.assertEqual(2, unprompted_calls)
        self.assertEqual(1, prompted_calls)
        self.assertEqual(1, result["recovered_lines"])
        self.assertEqual("alternative-vocals",
                         line.words[0]["stem_leakage_audio_source"])
        self.assertEqual("canonical-prompt-target-crop-after-near-match-local",
                         line.words[0]["stem_leakage_recognition_mode"])

    def test_alternative_stem_retries_with_bounded_phrase_context(self):
        text = "The tire hum made our burdens slip away"
        line = LrcLine(11.0, text, "", source_timestamp=10.0, words=[
            {"word": word, "start": 11.0 + index * .01,
             "end": 11.005 + index * .01,
             "timing_source": "sofa-singing-alignment", "sofa_confidence": .43}
            for index, word in enumerate(text.split())
        ])
        vocal = np.zeros(24 * 16000, dtype=np.float32)
        instrumental = np.full(24 * 16000, .03, dtype=np.float32)
        alternative = np.full(24 * 16000, .02, dtype=np.float32)
        unprompted_lengths = []

        def transcribe(chunk, _language, _device):
            unprompted_lengths.append(len(chunk) / 16000)
            if len(chunk) < 10 * 16000:
                return {"text": "unrelated noise", "words": []}
            prefix = [
                {"word": word, "start": .1 + index * .3,
                 "end": .3 + index * .3, "probability": .9}
                for index, word in enumerate("said a word before".split())
            ]
            words = "The tire made a burden slip away".split()
            target = [
                {"word": word, "start": 3.2 + index * .3,
                 "end": 3.45 + index * .3, "probability": .9}
                for index, word in enumerate(words)
            ]
            suffix = [
                {"word": word, "start": 6.0 + index * .3,
                 "end": 6.2 + index * .3, "probability": .9}
                for index, word in enumerate("and we were free".split())
            ]
            all_words = prefix + target + suffix
            return {"text": " ".join(word["word"] for word in all_words),
                    "words": all_words}

        prompted_lengths = []

        def prompted(prompt_chunk, _language, _device, _prompt):
            prompted_lengths.append(len(prompt_chunk) / 16000)
            prompted_words = text.split()[1:]
            return {"text": " ".join(prompted_words), "words": [
                {"word": word, "start": .59 + index * .3,
                 "end": .84 + index * .3, "probability": .1}
                for index, word in enumerate(prompted_words)
            ]}

        result = recover_complementary_stem_lines(
            vocal, instrumental, [line], "en", "cpu",
            transcribe_fn=transcribe, prompted_transcribe_fn=prompted,
            recognition_candidates=[("alternative-vocals", alternative)])

        self.assertEqual(2, len(unprompted_lengths))
        self.assertLess(unprompted_lengths[0], 10)
        self.assertGreater(unprompted_lengths[1], 10)
        self.assertLess(prompted_lengths[0], unprompted_lengths[0])
        self.assertEqual(1, result["recovered_lines"])
        self.assertEqual("canonical-prompt-target-crop-after-near-match-expanded-context",
                         line.words[0]["stem_leakage_recognition_mode"])
        attempt = result["diagnostics"][0]["source_attempts"][-1]
        self.assertTrue(attempt["fused_with_unprompted_near_match"])
        self.assertEqual("The", line.words[0]["word"])
        self.assertLessEqual(line.words[0]["end"], line.words[1]["start"])
        self.assertLessEqual(line.words[1]["end"], line.words[2]["start"])

    def test_default_does_not_silently_stop_after_four_candidates(self):
        texts = [f"Known lyric phrase number {index}" for index in range(6)]
        lines = [LrcLine(2.0 + index * 3.0, text, "",
                         source_timestamp=2.0 + index * 3.0, words=[
                {"word": word, "start": 1.0 + index * 3.0 + offset * .01,
                 "end": 1.005 + index * 3.0 + offset * .01,
                 "timing_source": "sofa-singing-alignment", "sofa_confidence": .43}
                for offset, word in enumerate(text.split())])
                 for index, text in enumerate(texts)]
        vocal = np.zeros(24 * 16000, dtype=np.float32)
        instrumental = np.full(24 * 16000, .03, dtype=np.float32)

        result = recover_complementary_stem_lines(
            vocal, instrumental, lines, "en", "cpu",
            transcribe_fn=lambda _audio, _language, _device:
                {"text": "unrelated noise", "words": []})

        self.assertEqual(6, result["candidate_lines"])
        self.assertIsNone(result["candidate_limit"])


if __name__ == "__main__":
    unittest.main()
