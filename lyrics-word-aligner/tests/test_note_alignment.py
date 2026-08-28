import unittest
from types import SimpleNamespace

from app.note_alignment import align_notes_to_syllables


class NoteAlignmentTests(unittest.TestCase):
    def test_polyphonic_notes_are_preserved_without_singer_inference(self):
        lines = [SimpleNamespace(words=[{
            "word": "duet", "start": 1.0, "end": 2.0,
            "syllables": [{"text": "duet", "start": 1.0, "end": 2.0}],
        }])]
        evidence = {"enabled": True, "notes": [
            {"start": 1.1, "end": 1.8, "midi": 60, "confidence": .8,
             "track_id": None, "singer_id": None},
            {"start": 1.2, "end": 1.9, "midi": 67, "confidence": .9,
             "track_id": None, "singer_id": None},
        ]}
        report = align_notes_to_syllables(lines, evidence)
        notes = lines[0].words[0]["syllables"][0]["notes"]
        self.assertEqual(2, len(notes))
        self.assertTrue(all(note["singer_id"] is None for note in notes))
        self.assertTrue(lines[0].words[0]["syllables"][0]
                        ["note_alignment"]["polyphony_preserved"])
        self.assertFalse(report["pitch_range_used_for_identity"])

    def test_multiple_melisma_notes_attach_to_one_existing_syllable(self):
        lines = [SimpleNamespace(words=[{
            "word": "love", "start": 1.0, "end": 2.0,
            "syllables": [{"text": "love", "start": 1.0, "end": 2.0}],
        }])]
        evidence = {"enabled": True, "notes": [
            {"start": 1.0, "end": 1.4, "pitch": 60, "amplitude": .8},
            {"start": 1.4, "end": 2.0, "pitch": 62, "amplitude": .8},
        ]}
        align_notes_to_syllables(lines, evidence)
        self.assertEqual(1, len(lines[0].words[0]["syllables"]))
        self.assertEqual(2, len(lines[0].words[0]["syllables"][0]["notes"]))


if __name__ == "__main__":
    unittest.main()
