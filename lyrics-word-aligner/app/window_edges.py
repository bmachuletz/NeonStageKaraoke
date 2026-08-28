from __future__ import annotations


def mark_leading_window_edge_fallback(words: list[dict], *, line_timestamp: float,
                                      window_base: float, pre_roll: float,
                                      tolerance: float = 0.035) -> bool:
    """Mark a leading forced-alignment token emitted at the pre-roll edge."""
    if (not words or pre_roll <= 0.05 or line_timestamp - window_base <= 0.05
            or abs(float(words[0]["start"]) - window_base) > tolerance):
        return False
    words[0]["window_edge_fallback"] = True
    words[0]["window_edge_base"] = round(window_base, 3)
    words[0]["window_edge_distance_ms"] = round(
        (float(words[0]["start"]) - window_base) * 1000, 1)
    return True
