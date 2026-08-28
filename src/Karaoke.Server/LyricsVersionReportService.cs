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
        AddStringMetric(text, technical, "alignment_profile",
            german ? "Alignment-Profil" : "Alignment profile");
        if (TryObject(technical, "research_shadow_input", out var shadowInput) &&
            TryBool(shadowInput, "enabled", out var shadowEnabled) && shadowEnabled)
        {
            AddStringMetric(text, shadowInput, "method",
                german ? "Clean-Room-Eingabe" : "Clean-room input");
            AddIntegerMetric(text, shadowInput, "removed_word_timing_records",
                german ? "Verworfene vorhandene Wortzeiten" : "Discarded existing word timings");
            AddIntegerMetric(text, shadowInput, "removed_editor_headers",
                german ? "Verworfene Editor-Metadaten" : "Discarded editor metadata records");
            Bullet(text, german
                ? "Manuelle Editor-Timings hatten in diesem Lauf keine Autorität."
                : "Manual editor timings had no authority in this run.");
        }
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
        if (TryObject(technical, "multiple_singing_voices", out var multipleVoices))
        {
            Section(text, german ? "Mehrstimmen-Analyse" : "Multi-voice analysis");
            AddStringMetric(text, multipleVoices, "mode", german ? "Modus" : "Mode");
            AddStringMetric(text, multipleVoices, "method", german ? "Methode" : "Method");
            AddIntegerMetric(text, multipleVoices, "analyzed_windows",
                german ? "Untersuchte Mehrstimmen-Fenster" : "Analyzed multi-voice windows");
            AddIntegerMetric(text, multipleVoices, "overlap_candidates",
                german ? "Fenster mit gleichzeitigem Gesang" : "Windows with concurrent singing");
            AddIntegerMetric(text, multipleVoices, "accepted_lane_proposals",
                german ? "Automatisch zugeordnete Backing-Phrasen" : "Automatically assigned backing phrases");
            AddIntegerMetric(text, multipleVoices, "automatic_backing_source",
                german ? "Songweit bestätigte Backing-Quelle" : "Song-wide confirmed backing source");
            if (TryObject(multipleVoices, "solo_voice_clustering", out var soloVoices))
            {
                AddStringMetric(text, soloVoices, "reason",
                    german ? "Ergebnis der Sänger-Clusterung" : "Singer clustering outcome");
                AddIntegerMetric(text, soloVoices, "analyzed_lines",
                    german ? "Auf Sängerwechsel untersuchte Zeilen" : "Lines analyzed for singer changes");
                AddIntegerMetric(text, soloVoices, "accepted_lines",
                    german ? "Sicher einer zweiten Stimme zugeordnete Zeilen" :
                    "Lines reliably assigned to a second singer");
            }
            if (TryArray(multipleVoices, "manual_lane_anchors", out var anchors))
            {
                var acceptedAnchors = anchors.EnumerateArray().Count(anchor =>
                    TryBool(anchor, "accepted", out var accepted) && accepted);
                Metric(text, german ? "Belastbare manuelle Stimmenanker" : "Reliable manual voice anchors",
                    acceptedAnchors.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (TryObject(technical, "analysis_stem_selection", out var analysisStem))
        {
            Section(text, german ? "Analyse-Audiospur" : "Analysis audio stem");
            AddStringMetric(text, analysisStem, "model", german ? "Separator-Modell" : "Separator model");
            AddStringMetric(text, analysisStem, "selected_candidate",
                german ? "Gewählter Kandidat" : "Selected candidate");
            AddStringMetric(text, analysisStem, "reason", german ? "Auswahlgrund" : "Selection reason");
            AddDoubleMetric(text, analysisStem, "candidate_score",
                german ? "Kandidatenbewertung" : "Candidate score");
        }
        if (TryObject(technical, "stage_stem_selection", out var stageStem))
        {
            Section(text, german ? "Stage-Audiospuren" : "Stage audio stems");
            AddStringMetric(text, stageStem, "separator_model",
                german ? "Karaoke-Separator" : "Karaoke separator");
            AddStringMetric(text, stageStem, "reason", german ? "Auswahlgrund" : "Selection reason");
        }
        if (TryObject(technical, "evidence_fusion", out var evidenceFusion))
        {
            Section(text, german ? "Evidenzfusion" : "Evidence fusion");
            AddStringMetric(text, evidenceFusion, "mode", german ? "Modus" : "Mode");
            AddIntegerMetric(text, evidenceFusion, "candidate_boundaries",
                german ? "Geprüfte Silbengrenzen" : "Evaluated syllable boundaries");
            AddIntegerMetric(text, evidenceFusion, "selected_boundaries",
                german ? "Übernommene Silbengrenzen" : "Selected syllable boundaries");
        }
        if (TryObject(technical, "note_alignment", out var noteAlignment))
        {
            AddIntegerMetric(text, noteAlignment, "note_events",
                german ? "Musikalische Notenereignisse" : "Musical note events");
            AddIntegerMetric(text, noteAlignment, "assignments",
                german ? "Noten-/Silben-Zuordnungen" : "Note/syllable assignments");
        }

        if (TryObject(technical, "basic_pitch_evidence", out var basicPitch))
        {
            Section(text, german ? "Basic-Pitch-Evidenz" : "Basic Pitch evidence");
            AddStringMetric(text, basicPitch, "service", german ? "Dienst" : "Service");
            AddStringMetric(text, basicPitch, "reason", german ? "Ergebnis" : "Outcome");
            AddIntegerMetric(text, basicPitch, "note_events",
                german ? "Erkannte Notenereignisse" : "Detected note events");
            AddIntegerMetric(text, basicPitch, "raw_onset_peaks",
                german ? "Roh erkannte Toneinsätze" : "Raw pitch onsets");
            AddIntegerMetric(text, basicPitch, "applied_onsets",
                german ? "Übernommene Phraseneinsätze" : "Applied phrase onsets");
            AddIntegerMetric(text, basicPitch, "applied_releases",
                german ? "Übernommene Ausklänge" : "Applied releases");
            AddStringMetric(text, basicPitch, "internal_word_mode",
                german ? "Innere Wortgrenzen" : "Internal word boundaries");
            AddIntegerMetric(text, basicPitch, "supported_internal_boundaries",
                german ? "Akustisch unterstützte innere Wortgrenzen" : "Acoustically supported internal word boundaries");
            AddIntegerMetric(text, basicPitch, "confirmed_internal_boundaries",
                german ? "Unverändert bestätigte Wortgrenzen" : "Unchanged confirmed word boundaries");
            AddIntegerMetric(text, basicPitch, "applied_internal_boundaries",
                german ? "Übernommene innere Wortgrenzen" : "Applied internal word boundaries");
            AddStringMetric(text, basicPitch, "syllable_mode",
                german ? "Silbengrenzen" : "Syllable boundaries");
            AddIntegerMetric(text, basicPitch, "confirmed_syllable_boundaries",
                german ? "Unverändert bestätigte Silbengrenzen" : "Unchanged confirmed syllable boundaries");
            AddIntegerMetric(text, basicPitch, "applied_syllable_boundaries",
                german ? "Übernommene Silbengrenzen" : "Applied syllable boundaries");
            if (TryObject(basicPitch, "pitch_timeline", out var pitchTimeline))
            {
                AddIntegerMetric(text, pitchTimeline, "event_count",
                    german ? "Noten in der Editor-Pitch-Spur" : "Notes in editor pitch track");
                AddIntegerMetric(text, pitchTimeline, "midi_min",
                    german ? "Tiefste erkannte MIDI-Note" : "Lowest detected MIDI note");
                AddIntegerMetric(text, pitchTimeline, "midi_max",
                    german ? "Höchste erkannte MIDI-Note" : "Highest detected MIDI note");
            }
            if (TryObject(basicPitch, "word_pitch_evidence", out var wordPitch))
            {
                AddIntegerMetric(text, wordPitch, "words_with_pitch",
                    german ? "Wörter mit Pitch-Abdeckung" : "Words with pitch coverage");
                AddIntegerMetric(text, wordPitch, "sustained_words",
                    german ? "Erkannte gehaltene Wörter" : "Detected sustained words");
            }
            if (TryObject(basicPitch, "alignment_confidence", out var pitchConfidence))
            {
                AddIntegerMetric(text, pitchConfidence, "measured_lines",
                    german ? "Zeilen mit Pitch-Evidenz" : "Lines with pitch evidence");
                AddIntegerMetric(text, pitchConfidence, "strongly_supported_lines",
                    german ? "Mehrfach stark bestätigte Zeilen" : "Strongly supported lines");
                if (TryArray(pitchConfidence, "review_lines", out var pitchReviewLines))
                    Metric(text, german ? "Pitch-Prüfhinweise" : "Pitch review notes",
                        pitchReviewLines.GetArrayLength().ToString(CultureInfo.InvariantCulture));
            }
            if (TryObject(basicPitch, "repetition_fingerprints", out var repetitions))
            {
                AddIntegerMetric(text, repetitions, "repeated_groups",
                    german ? "Verglichene Wiederholungsgruppen" : "Compared repetition groups");
                if (TryArray(repetitions, "groups", out var repetitionGroups))
                {
                    foreach (var group in repetitionGroups.EnumerateArray().Take(12))
                    {
                        var label = String(group, "text");
                        var occurrences = Integer(group, "occurrences");
                        var similarity = Number(group, "median_similarity");
                        Bullet(text, german
                            ? $"Wiederholung „{label}“: {occurrences} Vorkommen, mediane Melodieähnlichkeit {similarity:P1}."
                            : $"Repetition “{label}”: {occurrences} occurrences, median melody similarity {similarity:P1}.");
                    }
                }
            }
            Bullet(text, german
                ? "Pitch-Confidence beschreibt unabhängige Unterstützung, nicht automatisch die musikalische Wahrheit. Shadow-Kandidaten verändern keine Lyrics-Zeit."
                : "Pitch confidence describes independent support, not automatic musical truth. Shadow candidates do not alter lyric timing.");
        }

        Section(text, german ? "Automatische Korrekturen" : "Automatic corrections");
        AddNestedIntegerMetric(text, technical, "stage_vocal_boundaries", "release_corrections",
            german ? "Wortenden an Vocal-Ausklang korrigiert" : "Word endings corrected to vocal release");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "stem_contrast_release_repairs",
            german ? "Instrumental-Übersprechen aus Wortausklängen entfernt" : "Word releases trimmed against instrumental bleed");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "cross_line_transition_repairs",
            german ? "Leise Zeilenübergänge nach Refrains repariert" : "Quiet post-chorus line transitions repaired");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "isolated_internal_onset_repairs",
            german ? "Starke innere Worteinsätze einzeln übernommen" : "Strong internal word onsets retained individually");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "coherent_late_phrase_repairs",
            german ? "Verspätete vollständige Phrasen per IPA repariert" : "Complete delayed phrases repaired by IPA");
        AddNestedIntegerMetric(text, technical, "stage_vocal_boundaries", "phrase_onset_corrections",
            german ? "Phraseneinsätze nach echten Gesangspausen korrigiert" : "Phrase onsets corrected after real vocal pauses");
        AddNestedIntegerMetric(text, technical, "stage_vocal_boundaries", "silent_prefix_recovery",
            "corrected_lines",
            german ? "Vollständig in Stille platzierte Zeilen verschoben" : "Lines placed entirely in silence relocated");
        AddNestedIntegerMetric(text, technical, "stage_vocal_boundaries", "silent_prefix_recovery",
            "rejected_lines",
            german ? "Verworfene Zeilenverschiebungen" : "Rejected line relocations");
        AddNestedIntegerMetric(text, technical, "collapsed_line_repair", "repaired_lines",
            german ? "Unmöglich gestauchte Zeilen neu verteilt" : "Impossibly compressed lines redistributed");
        AddNestedIntegerMetric(text, technical, "collapsed_line_repair", "collapsed_runs",
            german ? "Erkannte gestauchte Zeilenblöcke" : "Detected compressed line runs");
        if (TryObject(technical, "stable_ts", out var stableTs) &&
            TryObject(stableTs, "coverage", out var stableCoverage) &&
            TryDouble(stableCoverage, "uncovered_seconds", out var uncovered) && uncovered > 0.5)
        {
            Metric(text, german ? "Gesang ohne erkannten Text" : "Singing without recognized text",
                $"{uncovered.ToString("0.0", CultureInfo.InvariantCulture)} s");
        }
        if (TryObject(technical, "stable_ts", out var stableRuns) &&
            TryObject(stableRuns, "hallucination", out var hallucination))
        {
            AddIntegerMetric(text, hallucination, "hallucinated_words",
                german ? "Erfundene Transkriptwörter verworfen" : "Invented transcript words discarded");
        }
        if (TryObject(technical, "language_reconciliation", out var languageCheck) &&
            TryBool(languageCheck, "applied", out var languageApplied) && languageApplied)
        {
            Metric(text, german ? "Sprache anhand der Lyrics korrigiert" : "Language corrected from lyrics",
                $"{String(languageCheck, "detected")} → {String(languageCheck, "winner")}");
        }
        AddNestedIntegerMetric(text, technical, "final_repetition_anchors", "rescaled_following_tail_words",
            german ? "Textwörter nach Refrainankern neu verteilt" : "Lexical tail words rescaled after refrain anchors");
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
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "local_duration_inversion_words_repaired",
            german ? "Vertauschte Nachbarwort-Dauern lokal per IPA repariert" : "Swapped neighboring word durations repaired locally by IPA");
        AddNestedIntegerMetric(text, technical, "phoneme_ctc_alignment", "connected_ipa_blank_repairs",
            german ? "Kurze CTC-Lücken in verbundenem Gesang überbrückt" : "Short CTC blanks bridged in connected singing");
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
