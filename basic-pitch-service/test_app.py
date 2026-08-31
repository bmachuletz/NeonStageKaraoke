import os
from unittest.mock import patch

import app


def test_prediction_settings_use_basic_pitch_seconds_parameter():
    environment = {"BASIC_PITCH_MINIMUM_NOTE_LENGTH_MS": "70"}
    with patch.dict(os.environ, environment):
        settings = app.prediction_settings()

    assert settings == {
        "onset_threshold": 0.45,
        "frame_threshold": 0.25,
        "minimum_note_length": 0.07,
    }
