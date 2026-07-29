import unittest
from array import array

from app.fragment_recovery import recover_deleted_fragments
from app.models import LrcLine


class FakeAligner:
    def align_text(self, _audio, text, _language):
        words = text.split()
        return [{"word": word, "start": .25 + index * .3, "end": .5 + index * .3}
                for index, word in enumerate(words)]


class FragmentRecoveryTests(unittest.TestCase):
    def test_recovers_chorus_fragment_swallowed_by_next_asr_word(self):
        line = LrcLine(54.4, "Einem Volk verkündet Wir ham die Scheiße satt", "",
                       source_timestamp=54.4, words=[
                           {"word": word, "start": 55 + index * .25, "end": 55.2 + index * .25}
                           for index, word in enumerate(
                               "Einem Volk verkündet Wir ham die Scheiße satt".split())
                       ])
        following = LrcLine(60.21, "Faxen dicke", "", source_timestamp=60.21,
                            words=[{"word": "Faxen", "start": 59.8, "end": 60.2},
                                   {"word": "dicke", "start": 60.3, "end": 60.7}])
        stable = [
            {"word": "Einem", "start": 54.8, "end": 55.2},
            {"word": "Volk", "start": 55.2, "end": 55.6},
            {"word": "verkündet", "start": 56.7, "end": 57.6},
            # Whisper labels the missing chorus audio as the following word.
            {"word": "Faxen", "start": 57.6, "end": 59.9},
            {"word": "dicke", "start": 59.9, "end": 60.3},
        ]
        operations = [
            {"type": "match", "expected_index": 0, "recognized_index": 0},
            {"type": "match", "expected_index": 1, "recognized_index": 1},
            {"type": "match", "expected_index": 2, "recognized_index": 2},
            *({"type": "delete", "expected_index": index} for index in range(3, 8)),
            {"type": "match", "expected_index": 8, "recognized_index": 3},
            {"type": "match", "expected_index": 9, "recognized_index": 4},
        ]

        result = recover_deleted_fragments(
            array("f", [0.0]) * (70 * 16000), [line, following], stable,
            {"operations": operations}, FakeAligner(), "de")

        self.assertEqual(1, result["recovered_fragments"])
        self.assertEqual(57.73, line.words[3]["start"])
        self.assertEqual(59.18, line.words[7]["end"])
        self.assertTrue(all(word["timing_source"] == "targeted-deleted-fragment-qwen"
                            for word in line.words[3:8]))


if __name__ == "__main__":
    unittest.main()
