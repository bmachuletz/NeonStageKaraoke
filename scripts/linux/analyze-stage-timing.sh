#!/usr/bin/env bash
set -Eeuo pipefail

server_url="${NEONSTAGE_SERVER_URL:-http://127.0.0.1:5274}"
take=1800

while (($#)); do
  case "$1" in
    --server) server_url=${2:?Server-URL fehlt}; shift 2 ;;
    --take) take=${2:?Anzahl fehlt}; shift 2 ;;
    -h|--help)
      echo "Verwendung: $0 [--server URL] [--take ANZAHL]"
      exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; exit 2 ;;
  esac
done

for command in curl jq; do
  command -v "$command" >/dev/null || { echo "Fehlendes Programm: $command" >&2; exit 1; }
done

curl -fsS "$server_url/api/diagnostics/stage-timing?take=$take" | jq -r '
  def stats(values):
    (values | if length == 0 then [0] else . end) as $v |
    { min: ($v|min), max: ($v|max), avg: ($v|add/length), spread: (($v|max)-($v|min)) };
  if length == 0 then
    "Noch keine Stage-Timing-Daten vorhanden. Server und neuen Stage-Build starten und einen Song abspielen."
  else
    [.[] | select(.playing == true and .masterSamplePositionSeconds > 0.1)] |
    if length == 0 then
      "Noch keine laufenden Audio-Samples vorhanden."
    else sort_by(.songId) | group_by(.songId)[] |
    . as $samples |
    stats([$samples[] | 1000 * ((.lyricsPositionSeconds + (.appliedOutputLatencySeconds // 0)) - .masterSamplePositionSeconds)]) as $clock |
    stats([$samples[] | 1000 * (.appliedOutputLatencySeconds // 0)]) as $latency |
    stats([$samples[] | 1000 * (.estimatedOutputLatencySeconds // 0)]) as $estimatedLatency |
    stats([$samples[] | 1000 * .sampleClockCorrectionSeconds]) as $correction |
    stats([$samples[] | 1000 * .stemDifferenceSeconds]) as $stems |
    "Song: \($samples[0].songId)\n" +
    "  Samples: \($samples|length), Gerät: \($samples[0].deviceId)\n" +
    "  Rohuhr-Abweichung (vor Lyrics-Kompensation): Ø \($clock.avg|round) ms, \($clock.min|round)…\($clock.max|round) ms, Schwankung \($clock.spread|round) ms\n" +
    "  Angewandte Audioausgabelatenz: Ø \($latency.avg|round) ms (DSP-Schätzung Ø \($estimatedLatency.avg|round) ms)\n" +
    "  Sampleclock-Korrektur: Ø \($correction.avg|round) ms, \($correction.min|round)…\($correction.max|round) ms\n" +
    "  Vocals minus Instrumental: Ø \($stems.avg|round) ms, \($stems.min|round)…\($stems.max|round) ms\n"
    end
  end'
