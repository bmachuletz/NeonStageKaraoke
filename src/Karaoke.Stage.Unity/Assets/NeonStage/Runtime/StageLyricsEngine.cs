using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NeonStage.Presentation;
using NeonStage.Timing;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

/// <summary>
/// Unity adapter around the shared presentation engine. Networking and drawing
/// remain platform-specific; every timing and pagination decision is shared
/// byte-for-byte with the editor preview.
/// </summary>
public sealed class StageLyricsEngine
{
    private StageLyricsView? _view;
    private StagePresentationEngine? _presentation;
    private int _shownPage = -1;

    public void Initialize(GameObject host) => _view = new StageLyricsView(host);

    public void SetStageTheme(string? stageThemeId) =>
        _view?.SetPresentationStyle(StageBackgroundShaderCatalog.ResolveLyricsStyle(stageThemeId));

    public void SetVideoBackground(bool active) => _view?.SetVideoBackground(active);

    public void SetVideoPerformanceMode(bool active) => _view?.SetVideoPerformanceMode(active);

    public async Task LoadAsync(string server, string songId)
    {
        _presentation = null;
        _shownPage = -1;
        _view?.Show(Array.Empty<string>(), Array.Empty<int>());

        using var request = UnityWebRequest.Get($"{server}/api/songs/{songId}/lyrics");
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) return;
        var lyrics = JsonUtility.FromJson<LyricsDto>(request.downloadHandler.text);
        if (lyrics?.lines == null) return;

        var beatTimes = new List<double>();
        if (!lyrics.hasUltraStarTimingHeritage)
        {
            using var visualizationRequest = UnityWebRequest.Get($"{server}/api/songs/{songId}/visualization");
            await visualizationRequest.SendWebRequest();
            if (visualizationRequest.result == UnityWebRequest.Result.Success)
            {
                var visualization = JsonUtility.FromJson<SongVisualizationDto>(
                    visualizationRequest.downloadHandler.text);
                foreach (var frame in visualization?.frames ?? Array.Empty<VisualizationFrameDto>())
                    if (frame.beat) beatTimes.Add(frame.timeSeconds);
            }
        }

        Load(lyrics, beatTimes, clearView: true);
    }

    public void LoadJson(string lyricsJson, IReadOnlyList<double>? beatTimes = null, bool clearView = true)
    {
        var lyrics = JsonUtility.FromJson<LyricsDto>(lyricsJson);
        if (lyrics?.lines == null) return;
        Load(lyrics, beatTimes ?? Array.Empty<double>(), clearView);
    }

    public void Clear()
    {
        _presentation = null;
        _shownPage = -1;
        _view?.Show(Array.Empty<string>(), Array.Empty<int>());
    }

    private void Load(LyricsDto lyrics, IReadOnlyList<double> beatTimes, bool clearView)
    {
        _presentation = null;
        _shownPage = -1;
        if (clearView) _view?.Show(Array.Empty<string>(), Array.Empty<int>());
        var lines = new List<StagePresentationLine>();
        foreach (var line in lyrics.lines)
        {
            // Enhanced-LRC display boundaries have a timestamp but no text and
            // intentionally do not consume a stage row.
            if (string.IsNullOrWhiteSpace(line.text)) continue;
            var words = new List<StagePresentationWord>();
            foreach (var word in line.words ?? Array.Empty<LyricsWordDto>())
            {
                var syllables = new List<StagePresentationSyllable>();
                foreach (var syllable in word.syllables ?? Array.Empty<LyricsSyllableDto>())
                {
                    var notes = new List<StagePresentationNote>();
                    foreach (var note in syllable.notes ?? Array.Empty<LyricsNoteEvidenceDto>())
                        notes.Add(new StagePresentationNote(
                            Seconds(note.start), Seconds(note.end), note.midi, note.confidence));
                    syllables.Add(new StagePresentationSyllable(
                        Seconds(syllable.start), Seconds(syllable.end), syllable.text, syllable.confidence,
                        syllable.karaokeTimingLocked, notes));
                }
                words.Add(new StagePresentationWord(
                    Seconds(word.start), Seconds(word.end), word.text, syllables, word.syllableConfidence,
                    word.karaokeTimingLocked));
            }
            lines.Add(new StagePresentationLine(
                Seconds(line.start),
                Seconds(line.end),
                line.text,
                words,
                line.holdAfterMilliseconds >= 0 ? line.holdAfterMilliseconds / 1000d : (double?)null,
                line.stageEffect,
                line.voiceLane,
                line.voiceLabel,
                line.karaokeTimingLocked));
        }
        _presentation = new StagePresentationEngine(lines,
            lyrics.hasUltraStarTimingHeritage ? 0 : StagePresentationEngine.PerceptualHighlightLeadSeconds,
            karaokeTimingEnabled: !lyrics.hasUltraStarTimingHeritage,
            beatTimes: beatTimes,
            highlightOptions: lyrics.musicalHighlight.enabled && !lyrics.hasUltraStarTimingHeritage
                ? KaraokeHighlightTimelineOptions.Default
                : null);
    }

    public void Update(StageClockFrame stageTime, float audioImpact)
    {
        if (_view == null || _presentation == null) return;
        _view.SetSongTime(stageTime.PositionSeconds);
        _view.TickEffects((float)stageTime.DeltaSeconds);
        var frame = _presentation.Evaluate(stageTime.LyricsPositionSeconds);
        if (frame.PageIndex < 0) return;

        if (_shownPage != frame.PageIndex)
        {
            var texts = new string[frame.Lines.Count];
            var lanes = new int[frame.Lines.Count];
            for (var index = 0; index < frame.Lines.Count; index++)
            {
                texts[index] = frame.Lines[index].Text;
                lanes[index] = frame.Lines[index].VoiceLane;
            }
            _view.Show(texts, lanes);
            _shownPage = frame.PageIndex;
        }

        _view.SetAlpha((float)frame.Alpha);
        for (var index = 0; index < frame.Lines.Count; index++)
        {
            var line = frame.Lines[index];
            _view.SetProgress(index, (float)line.Progress, (float)line.Pace, audioImpact, line.StageEffect);
        }
        // Draw the cue last so its final scan line flows into the first glyph.
        _view.SetEntryCue(frame.EntryCueRemainingSeconds, frame.ShowEntryCue, frame.ShowEntryCountdown);
    }

    private static double Seconds(string value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed.TotalSeconds : 0;
}

}
