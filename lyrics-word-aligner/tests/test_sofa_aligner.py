import unittest

from app.models import LrcLine
from app.sofa_aligner import (_exact_sequence_mapping, _karaoke_word_bounds, _line_can_be_replaced,
                              _phones_for_bounds, _word_groups, _words)


class Interval:
    def __init__(self, start: float, end: float, mark: str = ""):
        self.minTime = start
        self.maxTime = end
        self.mark = mark


class SofaAlignerTests(unittest.TestCase):
    def test_normalizes_english_lyrics_for_dictionary_lookup(self):
        self.assertEqual(["won't", "you", "take", "me", "home"],
                         _words("Won't you—take me home?"))

    def test_preserves_measured_spans_and_floors_sub_frame_words(self):
        bounds = _karaoke_word_bounds([
            Interval(1.0, 1.01), Interval(1.2, 1.7), Interval(1.8, 1.81)
        ])
        self.assertEqual([(1.0, 1.04), (1.2, 1.7), (1.8, 1.84)], bounds)

    def test_short_display_window_never_crosses_next_acoustic_onset(self):
        bounds = _karaoke_word_bounds([Interval(1.0, 1.002), Interval(1.02, 1.5)])
        self.assertEqual((1.0, 1.02), bounds[0])

    def test_rejects_partial_line_replacement_when_dictionary_omits_digits(self):
        line = LrcLine(1.0, "666 the beast", "", words=[
            {"word": "666"}, {"word": "the"}, {"word": "beast"},
        ])

        self.assertTrue(_line_can_be_replaced(line, len(_word_groups(line.text))))
        self.assertEqual(["six", "six", "six", "the", "beast"], _words(line.text))
        self.assertTrue(_line_can_be_replaced(line, 3))

    def test_maps_complete_tokens_around_an_isolated_oov_gap(self):
        mapping = _exact_sequence_mapping(
            ["jump", "around", "unlisted", "steady", "on", "the", "right"],
            ["jump", "around", "steady", "on", "the", "right"],
        )
        self.assertEqual(0, mapping[0])
        self.assertEqual(1, mapping[1])
        self.assertNotIn(2, mapping)
        self.assertEqual(2, mapping[3])
        self.assertEqual(5, mapping[6])

    def test_extracts_phone_tier_inside_a_word(self):
        phones = _phones_for_bounds([
            Interval(0.9, 1.0, "SP"), Interval(1.0, 1.1, "l"),
            Interval(1.1, 1.6, "iy"), Interval(1.6, 1.7, "f"),
            Interval(1.7, 1.8, "SP"),
        ], 1.0, 1.7)

        self.assertEqual(["l", "iy", "f"], [phone["phone"] for phone in phones])
        self.assertEqual(1.1, phones[1]["start"])


if __name__ == "__main__":
    unittest.main()
