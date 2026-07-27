using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

public sealed class StageLyricsEngine
{
    private readonly List<Line> _lines = new();
    private readonly List<Section> _sections = new();
    private const double SingingPauseSeconds = 0.8;
    private const double CountdownPauseSeconds = 3.0;
    private const int MaxSectionLines = 3;
    private const int MaxSectionCharacters = 125;
    private StageLyricsView? _view;
    private int _shownSection = -1;

    public void Initialize(GameObject host) => _view = new StageLyricsView(host);

    public async Task LoadAsync(string server, string songId)
    {
        _lines.Clear();
        _sections.Clear();
        _shownSection = -1;
        using var request = UnityWebRequest.Get($"{server}/api/songs/{songId}/lyrics");
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) return;
        var lyrics = JsonUtility.FromJson<LyricsDto>(request.downloadHandler.text);
        if (lyrics?.lines == null) return;
        foreach (var source in lyrics.lines)
        {
            var words = new List<Word>();
            foreach (var word in source.words ?? Array.Empty<LyricsWordDto>())
            {
                var syllables = new List<Syllable>();
                foreach (var syllable in word.syllables ?? Array.Empty<LyricsSyllableDto>())
                    syllables.Add(new Syllable(Seconds(syllable.start), Seconds(syllable.end), syllable.text, syllable.confidence));
                words.Add(new Word(Seconds(word.start), Seconds(word.end), word.text, syllables, word.syllableConfidence));
            }
            _lines.Add(new Line(Seconds(source.start), Seconds(source.end), source.text, words,
                source.holdAfterMilliseconds >= 0 ? source.holdAfterMilliseconds / 1000d : (double?)null,
                source.stageEffect));
        }
        BuildSections();
    }

    public void Update(double position, float audioImpact)
    {
        if (_view == null || _sections.Count == 0) return;
        _view.TickEffects(Time.unscaledDeltaTime);
        var sectionIndex = VisibleSection(position);
        var section = _sections[sectionIndex];
        if (_shownSection != sectionIndex)
        {
            var count = section.LastLine - section.FirstLine + 1;
            var texts = new string[Math.Min(count, 8)];
            for (var index = 0; index < texts.Length; index++) texts[index] = _lines[section.FirstLine + index].Text;
            _view.Show(texts);
            _shownSection = sectionIndex;
        }
        var fade = 1f;
        if (sectionIndex > 0)
            fade = Mathf.Clamp01((float)((position - TransitionTime(sectionIndex - 1)) / 0.28));
        if (sectionIndex + 1 < _sections.Count)
        {
            var transition = TransitionTime(sectionIndex);
            if (position >= transition - 0.28)
                fade = Math.Min(fade, Mathf.Clamp01((float)((transition - position) / 0.28)));
        }
        _view.SetAlpha(fade);
        for (var index = section.FirstLine; index <= Math.Min(section.LastLine, section.FirstLine + 7); index++)
        {
            var line = _lines[index];
            _view.SetProgress(index - section.FirstLine, LineProgress(line, position), SingingPace(line, position), audioImpact, line.StageEffect);
        }
        // Draw the cue after progress so its final glow can flow into the first
        // glyph instead of being disabled again by zero line progress.
        var cueRemaining = section.Start - position;
        _view.SetEntryCue(
            cueRemaining,
            section.HasPauseBefore || sectionIndex == 0,
            section.PauseBeforeSeconds >= CountdownPauseSeconds || sectionIndex == 0);
    }

    private static float LineProgress(Line line, double position)
    {
        if (line.Words.Count == 0)
            return Mathf.Clamp01((float)((position - line.Start) / Math.Max(.05, line.End - line.Start)));
        var total = 0;
        foreach (var word in line.Words) total += Math.Max(1, word.Text.Length) + 1;
        var completed = 0f;
        foreach (var word in line.Words)
        {
            var weight = Math.Max(1, word.Text.Length) + 1;
            var progress = WordProgress(word, position);
            completed += weight * progress;
            if (progress < 1) break;
        }
        return completed / Math.Max(1, total);
    }

    private static float WordProgress(Word word, double position)
    {
        if (word.SyllableConfidence < .62f || word.Syllables.Count < 2)
            return Mathf.Clamp01((float)((position - word.Start) / Math.Max(.02, word.End - word.Start)));
        var total = 0;
        foreach (var syllable in word.Syllables) total += Math.Max(1, syllable.Text.Length);
        var completed = 0f;
        foreach (var syllable in word.Syllables)
        {
            var weight = Math.Max(1, syllable.Text.Length);
            completed += weight * Mathf.Clamp01((float)((position - syllable.Start) / Math.Max(.02, syllable.End - syllable.Start)));
            if (position < syllable.End) break;
        }
        return completed / Math.Max(1, total);
    }

    private static float SingingPace(Line line, double position)
    {
        if (line.Words.Count == 0) return .35f;
        var word = line.Words[^1];
        foreach (var candidate in line.Words)
        {
            word = candidate;
            if (position <= candidate.End) break;
        }
        var duration = Math.Max(.04, word.End - word.Start);
        if (word.SyllableConfidence >= .62f && word.Syllables.Count > 1)
            foreach (var syllable in word.Syllables)
                if (position <= syllable.End)
                {
                    duration = Math.Max(.04, syllable.End - syllable.Start);
                    break;
                }
        // 0 = lang gezogener Ton, 1 = sehr schnell gesungenes Wort.
        return Mathf.Clamp01((float)((1.2 - duration) / 1.08));
    }

    public void Draw(Rect area, double position)
    {
        if (_sections.Count == 0) return;
        var sectionIndex = VisibleSection(position);
        var section = _sections[sectionIndex];
        var lineHeight = area.height / Math.Max(1, section.LastLine - section.FirstLine + 1);
        var previousColor = GUI.color;
        var fade = sectionIndex > 0 && position >= _sections[sectionIndex - 1].VocalEnd
            ? Mathf.Clamp01((float)((position - _sections[sectionIndex - 1].VocalEnd) / 0.35))
            : 1f;
        GUI.color = new Color(1, 1, 1, fade);
        for (var lineIndex = section.FirstLine; lineIndex <= section.LastLine; lineIndex++)
        {
            var lineArea = new Rect(area.x, area.y + (lineIndex - section.FirstLine) * lineHeight, area.width, lineHeight);
            DrawLine(_lines[lineIndex], lineArea, position, true, 1f);
        }
        GUI.color = previousColor;

        var firstLineArea = new Rect(area.x, area.y, area.width, lineHeight);
        if (section.HasPauseBefore) DrawEntryCue(_lines[section.FirstLine], firstLineArea, position);
        if (sectionIndex == 0 && position < section.Start)
        {
            var countdown = Math.Ceiling(section.Start - position);
            if (countdown is > 1 and <= 10) DrawCountdown(new Rect(area.x, area.y - 45, area.width, 55), (int)countdown);
        }
    }

    private void BuildSections()
    {
        if (_lines.Count == 0) return;
        var first = 0;
        for (var index = 1; index < _lines.Count; index++)
        {
            var previousVocalEnd = VocalEnd(_lines[index - 1]);
            var manualBreak = _lines[index - 1].HoldAfterSeconds.HasValue;
            if (!manualBreak && _lines[index].Start - previousVocalEnd < SingingPauseSeconds) continue;
            var pauseBefore = first == 0 ? _lines[first].Start : _lines[first].Start - VocalEnd(_lines[first - 1]);
            AddSectionPages(first, index - 1, first > 0 || pauseBefore >= SingingPauseSeconds, pauseBefore);
            first = index;
        }
        var finalPause = first == 0 ? _lines[first].Start : _lines[first].Start - VocalEnd(_lines[first - 1]);
        AddSectionPages(first, _lines.Count - 1, first > 0 || finalPause >= SingingPauseSeconds, finalPause);
    }

    private void AddSectionPages(int first, int last, bool hasPauseBefore, double pauseBefore)
    {
        var pageFirst = first;
        var characters = 0;
        for (var index = first; index <= last; index++)
        {
            var nextCharacters = characters + _lines[index].Text.Length;
            var exceeds = index > pageFirst &&
                (index - pageFirst >= MaxSectionLines || nextCharacters > MaxSectionCharacters);
            if (exceeds)
            {
                _sections.Add(new Section(pageFirst, index - 1, _lines[pageFirst].Start, VocalEnd(_lines[index - 1]),
                    pageFirst == first && hasPauseBefore, pageFirst == first ? pauseBefore : 0));
                pageFirst = index;
                characters = 0;
            }
            characters += _lines[index].Text.Length;
        }
        _sections.Add(new Section(pageFirst, last, _lines[pageFirst].Start, VocalEnd(_lines[last]),
            pageFirst == first && hasPauseBefore, pageFirst == first ? pauseBefore : 0));
    }

    private int VisibleSection(double position)
    {
        for (var index = 0; index < _sections.Count - 1; index++)
            if (position < TransitionTime(index)) return index;
        return _sections.Count - 1;
    }

    private double TransitionTime(int sectionIndex)
    {
        var section = _sections[sectionIndex];
        if (sectionIndex + 1 >= _sections.Count) return section.VocalEnd;
        var manualHold = _lines[section.LastLine].HoldAfterSeconds;
        if (manualHold.HasValue)
            return Math.Min(section.VocalEnd + manualHold.Value, _sections[sectionIndex + 1].Start);
        var instrumentalGap = _sections[sectionIndex + 1].Start - section.VocalEnd;
        if (instrumentalGap < 2) return section.VocalEnd;
        // Den fertigen Text kurz wirken lassen, aber mindestens 1,15 Sekunden
        // Vorbereitung für den nächsten Einsatzabsatz bewahren.
        return section.VocalEnd + Math.Min(0.8, instrumentalGap - 1.15);
    }

    private static double VocalEnd(Line line) => line.Words.Count > 0 ? line.Words[^1].End : line.End;

    private static void DrawEntryCue(Line line, Rect area, double position)
    {
        const double cueDuration = 1.15;
        var remaining = line.Start - position;
        if (remaining < 0 || remaining > cueDuration) return;
        var progress = 1f - (float)(remaining / cueDuration);
        var sizeStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold };
        var textWidth = Math.Min(area.width - 90, sizeStyle.CalcSize(new GUIContent(line.Text)).x);
        var x = area.center.x - textWidth * .5f - 24;
        var centerY = area.center.y;
        var glow = new Color(1f, 0.25f, 0.75f, 0.18f + progress * 0.55f);
        DrawBeam(new Rect(x - 7, centerY - 26, 14, 52), glow);
        DrawBeam(new Rect(x - 2, centerY - 20, 4, 40), new Color(0.87f, 1f, 0.05f, 0.55f + progress * 0.45f));
        DrawBeam(new Rect(x, centerY - 2, 10 + progress * 18, 4), new Color(0.87f, 1f, 0.05f, 0.45f + progress * 0.55f));
    }

    private static void DrawBeam(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static void DrawLine(Line line, Rect area, double position, bool active, float alpha)
    {
        var fontSize = FitFont(line, area, active ? 42 : 34);
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            richText = false
        };
        style.normal.textColor = new Color(0.9f, 0.84f, 0.95f, alpha);
        if (!active || line.Words.Count == 0)
        {
            style.wordWrap = true;
            GUI.Label(area, line.Text, style);
            return;
        }

        DrawTimedWords(line.Words, area, position, style);
    }

    private static void DrawTimedWords(IReadOnlyList<Word> words, Rect area, double position, GUIStyle style)
    {
        var space = style.CalcSize(new GUIContent(" ")).x;
        var rows = new List<List<(Word Word, float Width)>> { new() };
        var rowWidths = new List<float> { 0 };
        foreach (var word in words)
        {
            var width = style.CalcSize(new GUIContent(word.Text)).x;
            var row = rows.Count - 1;
            var required = width + (rows[row].Count > 0 ? space : 0);
            if (rowWidths[row] + required > area.width && rows[row].Count > 0)
            {
                rows.Add(new List<(Word, float)>());
                rowWidths.Add(0);
                row++;
                required = width;
            }
            rows[row].Add((word, width));
            rowWidths[row] += required;
        }

        // GUIStyle.lineHeight ist bei dynamischen Unity-Fonts teilweise 0. Eine
        // daraus gebildete Group schneidet dann nur wenige Pixel des Wortes aus.
        var lineHeight = Math.Max(style.fontSize * 1.25f, style.CalcSize(new GUIContent("Ägj")).y + 8);
        var y = area.center.y - rows.Count * lineHeight * .5f;
        for (var row = 0; row < rows.Count; row++)
        {
            var x = area.center.x - rowWidths[row] * .5f;
            foreach (var item in rows[row])
            {
                var wordRect = new Rect(x, y, item.Width, lineHeight);
                style.alignment = TextAnchor.MiddleLeft;
                style.normal.textColor = new Color(0.94f, 0.9f, 0.97f);
                GUI.Label(wordRect, item.Word.Text, style);
                var progress = WordProgress(item.Word, position);
                if (progress > 0)
                {
                    GUI.BeginGroup(new Rect(wordRect.x, wordRect.y, wordRect.width * progress, wordRect.height));
                    style.normal.textColor = new Color(0.87f, 1f, 0.05f);
                    GUI.Label(new Rect(0, 0, wordRect.width, wordRect.height), item.Word.Text, style);
                    GUI.EndGroup();
                }
                x += item.Width + space;
            }
            y += lineHeight;
        }
    }

    private static int FitFont(Line line, Rect area, int preferred)
    {
        for (var size = preferred; size >= 22; size -= 2)
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = FontStyle.Bold, wordWrap = true };
            if (style.CalcHeight(new GUIContent(line.Text), area.width) <= area.height - 8) return size;
        }
        return 22;
    }

    private static void DrawCountdown(Rect area, int seconds)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 76, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(1f, 0.25f, 0.75f);
        GUI.Label(area, seconds.ToString(CultureInfo.InvariantCulture), style);
    }

    private static double Seconds(string value) => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
        ? parsed.TotalSeconds
        : 0;

    private sealed class Line
    {
        public Line(double start, double end, string text, IReadOnlyList<Word> words, double? holdAfterSeconds, string stageEffect)
        {
            Start = start; End = end; Text = text; Words = words; HoldAfterSeconds = holdAfterSeconds; StageEffect = stageEffect;
        }
        public double Start { get; }
        public double End { get; }
        public string Text { get; }
        public IReadOnlyList<Word> Words { get; }
        public double? HoldAfterSeconds { get; }
        public string StageEffect { get; }
    }

    private sealed class Word
    {
        public Word(double start, double end, string text, IReadOnlyList<Syllable> syllables, float syllableConfidence)
        {
            Start = start; End = end; Text = text; Syllables = syllables; SyllableConfidence = syllableConfidence;
        }
        public double Start { get; }
        public double End { get; }
        public string Text { get; }
        public IReadOnlyList<Syllable> Syllables { get; }
        public float SyllableConfidence { get; }
    }

    private sealed class Syllable
    {
        public Syllable(double start, double end, string text, float confidence)
        {
            Start = start; End = end; Text = text; Confidence = confidence;
        }
        public double Start { get; }
        public double End { get; }
        public string Text { get; }
        public float Confidence { get; }
    }

    private sealed class Section
    {
        public Section(int firstLine, int lastLine, double start, double vocalEnd, bool hasPauseBefore, double pauseBeforeSeconds)
        {
            FirstLine = firstLine; LastLine = lastLine; Start = start; VocalEnd = vocalEnd; HasPauseBefore = hasPauseBefore; PauseBeforeSeconds = pauseBeforeSeconds;
        }
        public int FirstLine { get; }
        public int LastLine { get; }
        public double Start { get; }
        public double VocalEnd { get; }
        public bool HasPauseBefore { get; }
        public double PauseBeforeSeconds { get; }
    }
}
}
