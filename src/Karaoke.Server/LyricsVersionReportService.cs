using System.Globalization;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

/// <summary>Creates a stable, human-readable view of an immutable lyrics revision.</summary>
internal sealed class LyricsVersionReportService(
    LyricsVersionRepository versions,
    LibraryRepository library)
{
    public async Task<LyricsVersionReportDto?> GetAsync(Guid songId, Guid versionId, bool german,
        CancellationToken cancellationToken)
    {
        var version = await versions.GetAsync(songId, versionId, cancellationToken);
        if (version is null) return null;
        var song = await library.GetAsync(songId, cancellationToken);
        var title = song is null ? $"Song {songId}" : $"{song.Title} · {song.Artist}";
        var content = Build(version, title, german, out var outcome);
        return new(version.Id, version.SongId, version.Revision,
            german ? $"Alignment-Bericht · Revision {version.Revision}" : $"Alignment report · revision {version.Revision}",
            outcome, content, version.AlignmentReportJson is not null, version.CreatedAt);
    }

    private static string Build(LyricsVersionDto version, string songTitle, bool german, out string outcome)
    {
        using var editor = JsonDocument.Parse(version.DocumentJson);
        using var alignment = ParseOptional(version.AlignmentReportJson);
        var root = editor.RootElement;
        var report = alignment?.RootElement;
        var quality = report is { } reportRoot && TryObject(reportRoot, "quality", out var qualityObject)
            ? qualityObject : (JsonElement?)null;
        var score = quality is { } q && TryDouble(q, "score", out var scoreValue) ? scoreValue : (double?)null;
        var publishable = quality is { } publishQuality && TryBool(publishQuality, "publishable", out var publishableValue)
            ? publishableValue : (bool?)null;
        outcome = score is null
            ? (german ? "Version ohne technischen Pipeline-Bericht" : "Version without a technical pipeline report")
            : publishable == true
                ? (german ? $"Pipeline-Gate bestanden · {score:0.#}/100" : $"Pipeline gate passed · {score:0.#}/100")
                : (german ? $"Prüfung empfohlen · {score:0.#}/100" : $"Review recommended · {score:0.#}/100");

        var lines = EnumerateLines(root).ToArray();
        var allSegments = lines.SelectMany(EnumerateTree).ToArray();
        var words = allSegments.Count(item => PropertyEquals(item, "type", "Word"));
        var syllables = allSegments.Count(item => PropertyEquals(item, "type", "Syllable"));
        var manual = allSegments.Count(item =>
            PropertyEquals(item, "origin", "ManuallyCreated") ||
            PropertyEquals(item, "origin", "ManuallyAdjusted") ||
            TryBool(item, "isManuallyAdjusted", out var adjusted) && adjusted);
        var reviewed = allSegments.Count(item => TryBool(item, "isReviewed", out var value) && value);

        var text = new StringBuilder();
        Heading(text, german ? "ALIGNMENT-BERICHT" : "ALIGNMENT REPORT");
        Row(text, german ? "Song" : "Song", songTitle);
        Row(text, german ? "Version" : "Version", $"{version.Revision} · {Status(version.Status, german)}");
        Row(text, german ? "Erstellt" : "Created", version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Row(text, german ? "Ergebnis" : "Outcome", outcome);
        if (!string.IsNullOrWhiteSpace(version.AnalysisRunId))
            Row(text, german ? "Analyse-Lauf" : "Analysis run", version.AnalysisRunId!);

        Section(text, german ? "Inhalt der Version" : "Version content");
        Bullet(text, german ? $"{lines.Length} Zeilen, {words} Wörter und {syllables} Silben"
            : $"{lines.Length} lines, {words} words, and {syllables} syllables");
        Bullet(text, german ? $"{manual} manuell angelegte oder veränderte Segmente"
            : $"{manual} manually created or adjusted segments");
        Bullet(text, german ? $"{reviewed} Segmente als geprüft markiert"
            : $"{reviewed} segments marked as reviewed");
        AddDocumentMetadata(text, root, german);

        if (report is not { } technical)
        {
            Section(text, german ? "Technischer Bericht" : "Technical report");
            Bullet(text, german
                ? "Für diese ältere oder manuell erstellte Version wurde kein technischer Pipeline-Bericht gespeichert."
                : "No technical pipeline report was stored for this older or manually created version.");
            return text.ToString();
        }

        Section(text, german ? "Qualität" : "Quality");
        if (score is not null) Metric(text, german ? "Gesamtwert" : "Overall score", $"{score:0.#}/100");
        AddPercentMetric(text, quality, "word_duration_coverage", german ? "Wörter mit plausibler Dauer" : "Words with plausible duration");
        AddPercentMetric(text, quality, "acoustically_aligned_word_coverage", german ? "Akustisch ausgerichtete Wörter" : "Acoustically aligned words");
        AddIntegerMetric(text, quality, "heuristically_placed_words", german ? "Heuristisch platzierte Wörter" : "Heuristically placed words");
        AddIntegerMetric(text, quality, "geometrically_repaired_lines", german ? "Geometrisch reparierte Zeilen" : "Geometrically repaired lines");

        Section(text, german ? "Verwendete Analyse" : "Analysis used");
        AddStringMetric(text, technical, "alignment_selected", german ? "Gewählter Pfad" : "Selected path");
        AddStringMetric(text, technical, "alignment_mode", german ? "Alignment-Modus" : "Alignment mode");
        AddStringMetric(text, technical, "alignment_device", german ? "Rechengerät" : "Compute device");
        AddStringMetric(text, technical, "separation", german ? "Stem-Trennung" : "Stem separation");
        if (TryObject(technical, "stage_stem_selection", out var stem))
        {
            AddStringMetric(text, stem, "selected_candidate", german ? "Stage-Vocal-Stem" : "Stage vocal stem");
            AddStringMetric(text, stem, "separator_model", german ? "Separator-Modell" : "Separator model");
        }
        if (TryObject(technical, "lyrics_engine_v2", out var engine))
        {
            AddStringMetric(text, engine, "mode", german ? "Lyrics-Engine-Modus" : "Lyrics engine mode");
            AddIntegerMetric(text, engine, "selected_nonbaseline_lines",
                german ? "Verbesserte Alternativen gewählt" : "Improved alternatives selected");
            AddIntegerMetric(text, engine, "review_lines", german ? "Von der Engine markierte Prüfzeilen" : "Engine review lines");
        }
        if (TryObject(technical, "phoneme_ctc_alignment", out var phoneme) &&
            TryObject(phoneme, "micro_boundary_refinement", out var micro))
        {
            AddStringMetric(text, micro, "mode", german ? "IPA-Mikroanalyse" : "IPA micro analysis");
            AddIntegerMetric(text, micro, "attempted_words",
                german ? "Mikroanalytisch untersuchte Wörter" : "Words inspected by micro analysis");
            AddIntegerMetric(text, micro, "candidate_words",
                german ? "Messbar bessere Mikro-Kandidaten" : "Measurably better micro candidates");
        }
        if (TryObject(technical, "micro_voicing_analysis", out var voicing))
        {
            AddStringMetric(text, voicing, "method", german ? "Pitch-/Voicing-Methode" : "Pitch/voicing method");
            AddIntegerMetric(text, voicing, "windows",
                german ? "Untersuchte Ausklangfenster" : "Analyzed release windows");
            AddDoubleMetric(text, voicing, "analyzed_seconds",
                german ? "Analysierte Ausklangdauer" : "Analyzed release duration", " s");
        }

        Section(text, german ? "Automatische Korrekturen" : "Automatic corrections");
        AddNestedIntegerMetric(text, technical, "stage_vocal_boundaries", "release_corrections",
            german ? "Wortenden an Vocal-Ausklang korrigiert" : "Word endings corrected to vocal release");
        AddNestedIntegerMetric(text, technical, "sustain_refinement", "adjusted_words",
            german ? "Lang gehaltene Wörter verfeinert" : "Sustained words refined");
        AddNestedIntegerMetric(text, technical, "lyrics_completeness", "targeted_reanalysis", "recovered_lines",
            german ? "Fehlende Zeilen gezielt wiederhergestellt" : "Missing lines recovered by targeted analysis");
        AddNestedIntegerMetric(text, technical, "syllable_alignment", "syllables",
            german ? "Erzeugte Silben" : "Generated syllables");
        if (TryObject(technical, "phoneme_ctc_alignment", out var phonemeCorrections) &&
            TryObject(phonemeCorrections, "micro_boundary_refinement", out var microCorrections))
        {
            AddIntegerMetric(text, microCorrections, "applied_words",
                german ? "Wörter mit verbessertem Phonempfad" : "Words with refined phoneme path");
            AddIntegerMetric(text, microCorrections, "moved_phone_onsets",
                german ? "Akustisch verschobene Phonemeinsätze" : "Acoustically moved phoneme onsets");
        }
        AddNestedIntegerMetric(text, technical, "micro_sustain_refinement", "applied_words",
            german ? "Mit Pitch/Voicing korrigierte Ausklänge" : "Releases corrected by pitch/voicing");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "collapsed_ipa_words_repaired",
            german ? "Kollabierte Wörter gemeinsam per IPA repariert" : "Collapsed words jointly repaired by IPA");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "ipa_vocal_hole_words_repaired",
            german ? "Akustisch belegte Wortlücken per IPA geschlossen" : "Acoustically occupied word gaps closed by IPA");
        AddNestedIntegerMetric(text, technical, "repeated_phrase_refinement", "refined_words",
            german ? "Wörter in identischen Refrainrufen einzeln verankert" : "Words independently anchored in identical chorus calls");

        if (TryObject(technical, "timing_reference_comparison", out var timingReference) &&
            TryBool(timingReference, "comparable", out var comparable) && comparable)
        {
            Section(text, german ? "Vergleich mit dem Editor-Ausgangsstand" : "Comparison with editor input");
            AddDoubleMetric(text, timingReference, "median_absolute_start_delta_ms",
                german ? "Median der Einsatzänderungen" : "Median onset change", " ms");
            AddDoubleMetric(text, timingReference, "p95_absolute_start_delta_ms",
                german ? "95%-Grenze der Einsatzänderungen" : "95th percentile onset change", " ms");
            AddDoubleMetric(text, timingReference, "p95_absolute_end_delta_ms",
                german ? "95%-Grenze der Endänderungen" : "95th percentile release change", " ms");
            AddIntegerMetric(text, timingReference, "boundaries_changed_over_20ms",
                german ? "Grenzen mit mehr als 20 ms Änderung" : "Boundaries changed by more than 20 ms");
            Bullet(text, german
                ? "Dieser Vergleich misst Veränderungen; er bewertet nicht automatisch, ob Ausgangsstand oder Neuberechnung musikalisch richtiger ist."
                : "This comparison measures movement; it does not automatically decide whether the input or new timing is musically more accurate.");
        }

        Section(text, german ? "Prüfhinweise" : "Review notes");
        var warningCount = 0;
        warningCount += AddConflict(text, quality, "line_overlap_conflicts", german ? "Zeilenüberlappungen" : "Line overlaps");
        warningCount += AddConflict(text, quality, "word_overlap_conflicts", german ? "Wortüberlappungen" : "Word overlaps");
        warningCount += AddConflict(text, quality, "compressed_word_runs",
            german ? "Unplausibel komprimierte Wortfolgen" : "Implausibly compressed word runs");
        warningCount += AddConflict(text, quality, "stage_vocal_onset_conflicts", german ? "Unklare Vocal-Einsätze" : "Uncertain vocal onsets");
        if (TryObject(technical, "stage_vocal_boundaries", out var boundaries) &&
            TryArray(boundaries, "onset_details", out var onsetDetails))
        {
            foreach (var detail in onsetDetails.EnumerateArray().Take(20))
            {
                var line = Integer(detail, "line");
                var word = String(detail, "word");
                var delta = Number(detail, "delta_ms");
                Bullet(text, german
                    ? $"Zeile {line}, „{word}“: Vocal-Kandidat weicht um {delta:0} ms ab."
                    : $"Line {line}, “{word}”: vocal candidate differs by {delta:0} ms.");
            }
        }
        if (warningCount == 0)
            Bullet(text, german ? "Keine strukturellen Timing-Konflikte erkannt." : "No structural timing conflicts detected.");

        Section(text, german ? "Einordnung" : "Interpretation");
        Bullet(text, german
            ? "Der Bericht beschreibt genau den Pipeline-Lauf, der dieser Version zugrunde liegt. Spätere manuelle Änderungen bleiben im Versionsinhalt erkennbar."
            : "This report describes the exact pipeline run underlying this version. Later manual edits remain visible in the version content.");
        Bullet(text, publishable == true
            ? (german ? "Die technischen Gates wurden bestanden; eine musikalische Hörprüfung bleibt sinnvoll."
                : "Technical gates passed; a musical listening review is still recommended.")
            : (german ? "Mindestens ein Qualitäts-Gate empfiehlt eine gezielte Hörprüfung im Editor."
                : "At least one quality gate recommends a focused listening review in the editor."));
        return text.ToString();
    }

    private static JsonDocument? ParseOptional(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> EnumerateLines(JsonElement root) =>
        TryArray(root, "lines", out var lines) ? lines.EnumerateArray() : [];

    private static IEnumerable<JsonElement> EnumerateTree(JsonElement segment)
    {
        yield return segment;
        if (!TryArray(segment, "children", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in EnumerateTree(child)) yield return descendant;
    }

    private static void AddDocumentMetadata(StringBuilder text, JsonElement root, bool german)
    {
        if (TryString(root, "pipelineVersion", out var pipeline)) Metric(text, german ? "Pipeline-Version" : "Pipeline version", pipeline);
        if (TryString(root, "modelVersion", out var model)) Metric(text, german ? "Modell-Version" : "Model version", model);
    }

    private static int AddConflict(StringBuilder text, JsonElement? quality, string property, string label)
    {
        if (quality is not { } value || !TryInt(value, property, out var count) || count <= 0) return 0;
        Bullet(text, $"{label}: {count}");
        return count;
    }

    private static void AddPercentMetric(StringBuilder text, JsonElement? source, string property, string label)
    {
        if (source is { } value && TryDouble(value, property, out var number)) Metric(text, label, $"{number:P1}");
    }

    private static void AddIntegerMetric(StringBuilder text, JsonElement? source, string property, string label)
    {
        if (source is { } value && TryInt(value, property, out var number)) Metric(text, label, number.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddDoubleMetric(StringBuilder text, JsonElement source, string property,
        string label, string suffix = "")
    {
        if (TryDouble(source, property, out var number))
            Metric(text, label, $"{number:0.#}{suffix}");
    }

    private static void AddStringMetric(StringBuilder text, JsonElement source, string property, string label)
    {
        if (TryString(source, property, out var value)) Metric(text, label, value);
    }

    private static void AddNestedIntegerMetric(StringBuilder text, JsonElement source, string parent,
        string property, string label)
    {
        if (TryObject(source, parent, out var nested)) AddIntegerMetric(text, nested, property, label);
    }

    private static void AddNestedIntegerMetric(StringBuilder text, JsonElement source, string parent,
        string child, string property, string label)
    {
        if (TryObject(source, parent, out var nested) && TryObject(nested, child, out var inner))
            AddIntegerMetric(text, inner, property, label);
    }

    private static bool TryObject(JsonElement source, string property, out JsonElement value)
    {
        value = default;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }
    private static bool TryArray(JsonElement source, string property, out JsonElement value)
    {
        value = default;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }
    private static bool TryString(JsonElement source, string property, out string value)
    {
        value = string.Empty;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.String && (value = item.GetString() ?? string.Empty).Length > 0;
    }
    private static bool TryDouble(JsonElement source, string property, out double value)
    {
        value = 0;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var item) &&
               item.TryGetDouble(out value);
    }
    private static bool TryInt(JsonElement source, string property, out int value)
    {
        value = 0;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var item) &&
               item.TryGetInt32(out value);
    }
    private static bool TryBool(JsonElement source, string property, out bool value)
    {
        value = false;
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }
    private static bool PropertyEquals(JsonElement source, string property, string expected) =>
        TryString(source, property, out var value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static int Integer(JsonElement source, string property) => TryInt(source, property, out var value) ? value : 0;
    private static double Number(JsonElement source, string property) => TryDouble(source, property, out var value) ? value : 0;
    private static string String(JsonElement source, string property) => TryString(source, property, out var value) ? value : "?";

    private static string Status(LyricsVersionStatus status, bool german) => german ? status switch
    {
        LyricsVersionStatus.Generated => "Generiert", LyricsVersionStatus.NeedsReview => "Prüfung nötig",
        LyricsVersionStatus.InReview => "In Prüfung", LyricsVersionStatus.Reviewed => "Geprüft",
        LyricsVersionStatus.Approved => "Freigegeben", LyricsVersionStatus.Published => "Veröffentlicht",
        LyricsVersionStatus.Rejected => "Abgelehnt", LyricsVersionStatus.Superseded => "Archiviert",
        _ => status.ToString()
    } : status.ToString();

    private static void Heading(StringBuilder text, string value) => text.AppendLine(value).AppendLine(new string('═', value.Length));
    private static void Section(StringBuilder text, string value) => text.AppendLine().AppendLine(value).AppendLine(new string('─', value.Length));
    private static void Row(StringBuilder text, string label, string value) => text.Append(label).Append(": ").AppendLine(value);
    private static void Metric(StringBuilder text, string label, string value) => text.Append("• ").Append(label).Append(": ").AppendLine(value);
    private static void Bullet(StringBuilder text, string value) => text.Append("• ").AppendLine(value);
}
