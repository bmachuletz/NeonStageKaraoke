using Karaoke.Contracts;
using Karaoke.Editor.Core;
using NeonStage.Presentation;

var viewport = new TimelineViewport(100, TimeSpan.FromSeconds(10));
Assert(viewport.TimeToPixel(TimeSpan.FromSeconds(12)) == 200, "Zeit wird korrekt in Pixel umgerechnet.");
Assert(viewport.PixelToTime(250) == TimeSpan.FromSeconds(12.5), "Pixel werden korrekt in Zeit umgerechnet.");
var anchor = viewport.PixelToTime(300);
viewport.ZoomAt(2, 300);
Assert(viewport.PixelToTime(300) == anchor, "Zoom hält die Zeit unter dem Mauszeiger stabil.");
viewport.Reset();
Assert(viewport.Offset == TimeSpan.Zero && viewport.PixelsPerSecond == 115,
    "Ein Songwechsel setzt Scrollposition und Zoom der Timeline zurück.");
viewport.ShowWindow(TimeSpan.FromSeconds(7.92), TimeSpan.FromSeconds(30), 1200);
Assert(viewport.Offset == TimeSpan.FromSeconds(7.92) && viewport.PixelsPerSecond == 40 &&
       viewport.VisibleRange(1200).End == TimeSpan.FromSeconds(37.92),
    "Die initiale Editoransicht beginnt am ersten Vocal und zeigt exakt 30 Sekunden.");

var trackedLoopDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var trackedLoopLine = Segment("Loop me", LyricSegmentType.Line, 10, 13);
var trackedLoopWord = Segment("Loop", LyricSegmentType.Word, 10.25, 11.1, trackedLoopLine.Id);
trackedLoopLine.Children.Add(trackedLoopWord);
trackedLoopDocument.Lines.Add(trackedLoopLine);
var trackedWordLoop = new TrackedWordLoop();
Assert(trackedWordLoop.Bind(trackedLoopWord) &&
       trackedWordLoop.TryGetRange(trackedLoopDocument, out var initialWordLoop) &&
       initialWordLoop == (TimeSpan.FromSeconds(10.25), TimeSpan.FromSeconds(11.1)),
    "Ein Wort kann als dynamische Loop-Grenze gebunden werden.");
trackedLoopWord.Start = TimeSpan.FromSeconds(10.5);
trackedLoopWord.End = TimeSpan.FromSeconds(11.75);
Assert(trackedWordLoop.TryGetRange(trackedLoopDocument, out var editedWordLoop) &&
       editedWordLoop == (trackedLoopWord.Start, trackedLoopWord.End),
    "Die Loop-Grenze folgt dem Verschieben und Skalieren des gebundenen Wortes.");
var replacementWord = new LyricSegment
{
    Id = trackedLoopWord.Id, ParentId = trackedLoopLine.Id, Type = LyricSegmentType.Word, Text = "Loop",
    Start = TimeSpan.FromSeconds(10.75), End = TimeSpan.FromSeconds(12),
    OriginalStart = TimeSpan.FromSeconds(10.75), OriginalEnd = TimeSpan.FromSeconds(12), OriginalText = "Loop"
};
trackedLoopLine.Children[0] = replacementWord;
Assert(trackedWordLoop.TryGetRange(trackedLoopDocument, out var replacedWordLoop) &&
       replacedWordLoop == (replacementWord.Start, replacementWord.End),
    "Die Loop-Bindung bleibt auch nach einem Dokument-Snapshot anhand der Wort-ID erhalten.");
trackedLoopLine.Children.Clear();
Assert(!trackedWordLoop.TryGetRange(trackedLoopDocument, out _) && !trackedWordLoop.IsBound,
    "Beim Löschen des gebundenen Wortes wird die dynamische Loop-Bindung aufgehoben.");

var parent = Segment("Wort", LyricSegmentType.Word, 1, 3);
var left = Segment("Sil", LyricSegmentType.Syllable, 1, 2, parent.Id);
var right = Segment("be", LyricSegmentType.Syllable, 2, 3, parent.Id);
parent.Children.AddRange([left, right]);
var history = new CommandHistory();
history.Execute(new MoveSharedBoundaryCommand(left, right, TimeSpan.FromSeconds(2.25), TimeSpan.FromMilliseconds(40)));
Assert(left.End == right.Start && left.End == TimeSpan.FromSeconds(2.25), "Gemeinsame Grenze bleibt lückenlos.");
history.Undo();
Assert(left.End == right.Start && left.End == TimeSpan.FromSeconds(2), "Undo stellt beide Grenzseiten wieder her.");
Assert(!left.IsManuallyAdjusted && left.Origin == SegmentOrigin.ImportedLineLyrics,
    "Undo stellt auch Herkunft und manuellen Änderungsstatus wieder her.");
history.Redo();
Assert(left.End == TimeSpan.FromSeconds(2.25), "Redo führt die fachliche Änderung erneut aus.");
history.Execute(new SetReviewStateCommand(left, true));
Assert(left.IsReviewed && !left.RequiresReview, "Ein Segment kann nachvollziehbar als geprüft markiert werden.");
history.Undo();
Assert(!left.IsReviewed && left.RequiresReview, "Der Prüfstatus ist vollständig rückgängig machbar.");

var markerSource = new LyricsDto(Guid.NewGuid(),
[
    new(TimeSpan.FromSeconds(10), "Verse 1", TimeSpan.FromSeconds(12), 0,
        [new(TimeSpan.FromSeconds(10), "Verse", TimeSpan.FromSeconds(11), 0)]),
    new(TimeSpan.FromSeconds(12), "Das ist echter Text", TimeSpan.FromSeconds(15), 1)
]);
var markerDocument = LyricsDocumentImporter.Import(markerSource);
Assert(markerDocument.Lines[0].Text == string.Empty && markerDocument.Lines[0].Children.Count == 0 &&
       markerDocument.Lines[1].Text == "Das ist echter Text",
    "Strukturmarker werden als unsichtbare Zeitgrenze statt als singbarer Text importiert.");
var legacyMarker = Segment("[Refrain]", LyricSegmentType.Line, 20, 21);
legacyMarker.Children.Add(Segment("Refrain", LyricSegmentType.Word, 20, 21, legacyMarker.Id));
markerDocument.Lines.Add(legacyMarker);
Assert(LyricsDocumentImporter.IgnoreStructureMarkers(markerDocument) == 1 &&
       legacyMarker.Text == string.Empty && legacyMarker.Children.Count == 0,
    "Bereits gespeicherte Review-Dokumente werden beim Laden von Strukturmarkern bereinigt.");

var scalableWord = Segment("Hallo", LyricSegmentType.Word, 10, 12);
var firstSyllable = Segment("Hal", LyricSegmentType.Syllable, 10, 11, scalableWord.Id);
var secondSyllable = Segment("lo", LyricSegmentType.Syllable, 11, 12, scalableWord.Id);
scalableWord.Children.AddRange([firstSyllable, secondSyllable]);
TimelineEditing.ResizeWord(scalableWord, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), TimeSpan.FromMilliseconds(35));
Assert(firstSyllable.End == TimeSpan.FromSeconds(12) && secondSyllable.Start == TimeSpan.FromSeconds(12),
    "Beim Verlängern eines Wortes werden seine Silben proportional skaliert.");
TimelineEditing.ResizeWord(scalableWord, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), TimeSpan.FromMilliseconds(35));
Assert(firstSyllable.End == TimeSpan.FromSeconds(10.5) && secondSyllable.End == TimeSpan.FromSeconds(11),
    "Beim Verkürzen eines Wortes werden seine Silben proportional komprimiert.");
TimelineEditing.ResizeSyllable(secondSyllable, scalableWord, secondSyllable.Start,
    TimeSpan.FromSeconds(11.4), TimeSpan.FromMilliseconds(25));
Assert(scalableWord.End == TimeSpan.FromSeconds(11.4),
    "Eine verlängerte äußere Silbe verlängert unmittelbar ihr Wort.");
var treeHistory = new CommandHistory();
var inserted = Segment("neu", LyricSegmentType.Syllable, 11.4, 11.7, scalableWord.Id);
treeHistory.Execute(new EditSegmentTreeCommand(scalableWord, "Silbe einfügen", () => scalableWord.Children.Add(inserted)));
Assert(scalableWord.Children.Count == 3, "Silben können atomar eingefügt werden.");
treeHistory.Undo();
Assert(scalableWord.Children.Count == 2, "Das Einfügen einer Silbe ist vollständig rückgängig machbar.");
Assert(treeHistory.Redo() && scalableWord.Children.Count == 3, "Der Vorwärts-Befehl stellt eine Strukturänderung wieder her.");
treeHistory.Undo();
var secondWord = Segment("Welt", LyricSegmentType.Word, 12, 13);
var forestHistory = new CommandHistory();
forestHistory.Execute(new EditSegmentForestCommand([scalableWord, secondWord], "Gruppe verschieben", () =>
{
    TimelineEditing.MoveWithChildren(scalableWord, TimeSpan.FromMilliseconds(100));
    TimelineEditing.MoveWithChildren(secondWord, TimeSpan.FromMilliseconds(100));
}));
Assert(scalableWord.Start == TimeSpan.FromSeconds(10.1) && secondWord.Start == TimeSpan.FromSeconds(12.1),
    "Mehrere ausgewählte Segmente werden gemeinsam verschoben.");
Assert(forestHistory.Undo() && scalableWord.Start == TimeSpan.FromSeconds(10) && secondWord.Start == TimeSpan.FromSeconds(12),
    "Rückgängig stellt eine vollständige Mehrfachverschiebung wieder her.");
Assert(forestHistory.Redo() && scalableWord.Start == TimeSpan.FromSeconds(10.1) && secondWord.Start == TimeSpan.FromSeconds(12.1),
    "Vorwärts wiederholt eine vollständige Mehrfachverschiebung.");

var offsetLine = Segment("Global", LyricSegmentType.Line, 5, 8);
var offsetWord = Segment("Global", LyricSegmentType.Word, 5.2, 7.8, offsetLine.Id);
var offsetSyllable = Segment("Global", LyricSegmentType.Syllable, 5.2, 7.8, offsetWord.Id);
offsetWord.Children.Add(offsetSyllable);
offsetLine.Children.Add(offsetWord);
var offsetDocument = new LyricsEditorDocument { SongId = Guid.NewGuid(), Lines = { offsetLine } };
var offsetHistory = new CommandHistory();
offsetHistory.Execute(new EditSegmentForestCommand(offsetDocument.Lines, "Global verschieben",
    () => TimelineEditing.ShiftDocument(offsetDocument, TimeSpan.FromMilliseconds(-200),
        TimeSpan.FromSeconds(20))));
Assert(offsetLine.Start == TimeSpan.FromSeconds(4.8) && offsetWord.Start == TimeSpan.FromSeconds(5) &&
       offsetSyllable.End == TimeSpan.FromSeconds(7.6),
    "Ein globaler Versatz verschiebt Zeilen, Wörter und Silben um exakt denselben Betrag.");
Assert(offsetHistory.Undo() && offsetLine.Start == TimeSpan.FromSeconds(5) &&
       offsetSyllable.End == TimeSpan.FromSeconds(7.8),
    "Der globale Lyrics-Versatz ist ein atomarer Undo-Schritt.");
Assert(offsetHistory.Redo() && offsetWord.Start == TimeSpan.FromSeconds(5),
    "Der globale Lyrics-Versatz kann vollständig wiederholt werden.");
try
{
    TimelineEditing.ShiftDocument(offsetDocument, TimeSpan.FromSeconds(-10));
    throw new InvalidOperationException("Test fehlgeschlagen: Negative Timings hätten abgewiesen werden müssen.");
}
catch (InvalidOperationException)
{
    Console.WriteLine("OK: Ein globaler Versatz darf keine Lyrics vor den Songanfang schieben.");
}

var transformFirstLine = Segment("Erste Zeile", LyricSegmentType.Line, 4, 6);
var transformFirstWord = Segment("Erste", LyricSegmentType.Word, 4.5, 5.5, transformFirstLine.Id);
var transformFirstSyllable = Segment("Erste", LyricSegmentType.Syllable, 4.5, 5.5, transformFirstWord.Id);
transformFirstWord.Children.Add(transformFirstSyllable);
transformFirstLine.Children.Add(transformFirstWord);
var transformSecondLine = Segment("Zweite Zeile", LyricSegmentType.Line, 8, 10);
var transformSecondWord = Segment("Zweite", LyricSegmentType.Word, 8.5, 9.5, transformSecondLine.Id);
transformSecondLine.Children.Add(transformSecondWord);
var transformDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
transformDocument.Lines.AddRange([transformFirstLine, transformSecondLine]);
Assert(TimelineEditing.ShiftSelection(transformDocument, transformDocument.Lines,
           TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)) == 2 &&
       transformFirstLine.Start == TimeSpan.FromSeconds(5) &&
       transformFirstSyllable.Start == TimeSpan.FromSeconds(5.5) &&
       transformSecondLine.End == TimeSpan.FromSeconds(11),
    "Eine Mehrfachauswahl wird mit allen Unterelementen um einen gemeinsamen Offset verschoben.");
Assert(TimelineEditing.ScaleSelection(transformDocument, transformDocument.Lines, 0.5,
           SelectionScaleAnchor.Start, TimeSpan.FromSeconds(30)) == 2 &&
       transformFirstLine.Start == TimeSpan.FromSeconds(5) &&
       transformFirstWord.Start == TimeSpan.FromSeconds(5.25) &&
       transformSecondLine.Start == TimeSpan.FromSeconds(7) &&
       transformSecondLine.End == TimeSpan.FromSeconds(8),
    "Mehrere Zeilen, Abstände, Wörter und Silben werden gemeinsam proportional um den festen Anfang skaliert.");
var centeredStart = transformFirstLine.Start;
var centeredEnd = transformSecondLine.End;
TimelineEditing.ScaleSelection(transformDocument, transformDocument.Lines, 2,
    SelectionScaleAnchor.Center, TimeSpan.FromSeconds(30));
Assert(transformFirstLine.Start == centeredStart - TimeSpan.FromSeconds(1.5) &&
       transformSecondLine.End == centeredEnd + TimeSpan.FromSeconds(1.5),
    "Beim Skalieren um die Mitte bleibt der Mittelpunkt der Auswahl fest.");

var syncDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var syncLine = Segment("Wir singen heute", LyricSegmentType.Line, 20, 26);
var syncFirstWord = Segment("Wir", LyricSegmentType.Word, 20, 21, syncLine.Id);
var syncSecondWord = Segment("singen", LyricSegmentType.Word, 22, 24, syncLine.Id);
var syncThirdWord = Segment("heute", LyricSegmentType.Word, 25, 26, syncLine.Id);
syncSecondWord.Children.AddRange([
    Segment("sin", LyricSegmentType.Syllable, 22, 23, syncSecondWord.Id),
    Segment("gen", LyricSegmentType.Syllable, 23, 24, syncSecondWord.Id)
]);
syncLine.Children.AddRange([syncFirstWord, syncSecondWord, syncThirdWord]);
syncDocument.Lines.Add(syncLine);
var synchronized = TimelineEditing.FitSelectionToRange(syncDocument, [syncFirstWord, syncSecondWord],
    TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(25));
Assert(synchronized == 2 && syncFirstWord.Start == TimeSpan.FromSeconds(21) &&
       syncSecondWord.End == TimeSpan.FromSeconds(25) && syncSecondWord.Children[0].Start == TimeSpan.FromSeconds(23),
    "Mehrere Wörter werden gemeinsam und proportional exakt in den markierten Waveform-Bereich eingepasst.");
Assert(syncThirdWord.Start == TimeSpan.FromSeconds(25),
    "Nicht ausgewählte nachfolgende Wörter bleiben beim Synchronisieren unverändert.");
var beforeRejectedSync = syncFirstWord.Start;
try
{
    TimelineEditing.FitSelectionToRange(syncDocument, [syncFirstWord],
        TimeSpan.FromSeconds(24.5), TimeSpan.FromSeconds(25.5));
    throw new InvalidOperationException("Test fehlgeschlagen: Eine Kollision hätte abgelehnt werden müssen.");
}
catch (InvalidOperationException)
{
    Assert(syncFirstWord.Start == beforeRejectedSync,
        "Eine kollidierende Synchronisierung wird vollständig und ohne Teiländerung verworfen.");
}

var syllableDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var syllableLine = Segment("Hallo", LyricSegmentType.Line, 8, 12);
var syllableWord = Segment("Hallo", LyricSegmentType.Word, 9, 11, syllableLine.Id);
var syllableLeft = Segment("Hal", LyricSegmentType.Syllable, 9, 10, syllableWord.Id);
var syllableRight = Segment("lo", LyricSegmentType.Syllable, 10, 11, syllableWord.Id);
syllableWord.Children.AddRange([syllableLeft, syllableRight]);
syllableLine.Children.Add(syllableWord);
syllableDocument.Lines.Add(syllableLine);
TimelineEditing.FitSelectionToRange(syllableDocument, [syllableLeft],
    TimeSpan.FromSeconds(8.75), TimeSpan.FromSeconds(9.75));
Assert(syllableLeft.Start == TimeSpan.FromSeconds(8.75) && syllableLeft.End == TimeSpan.FromSeconds(9.75) &&
       syllableWord.Start == TimeSpan.FromSeconds(8.75) && syllableWord.End == TimeSpan.FromSeconds(11),
    "Eine einzelne Silbe übernimmt den Bereich exakt und aktualisiert ihre Wortgrenze ohne die Nachbarsilbe zu verschieben.");

var syncLineDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var coarseLine = Segment("Grobe Zeile", LyricSegmentType.Line, 30, 34);
var coarseWord = Segment("Grobe", LyricSegmentType.Word, 31, 33, coarseLine.Id);
coarseLine.Children.Add(coarseWord);
var untouchedLine = Segment("Danach", LyricSegmentType.Line, 40, 42);
syncLineDocument.Lines.AddRange([coarseLine, untouchedLine]);
var syncHistory = new CommandHistory();
syncHistory.Execute(new EditSegmentTreeCommand(coarseLine, "Zeile synchronisieren", () =>
    TimelineEditing.FitSelectionToRange(syncLineDocument, [coarseLine],
        TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(39))));
Assert(coarseLine.Start == TimeSpan.FromSeconds(35) && coarseLine.End == TimeSpan.FromSeconds(39) &&
       coarseWord.Start == TimeSpan.FromSeconds(36) && coarseWord.End == TimeSpan.FromSeconds(38) &&
       untouchedLine.Start == TimeSpan.FromSeconds(40),
    "Eine Zeile wird mitsamt Inhalt grob proportional eingepasst, ohne die Folgezeile zu verschieben.");
Assert(syncHistory.Undo() && coarseLine.Start == TimeSpan.FromSeconds(30) && coarseWord.Start == TimeSpan.FromSeconds(31),
    "Das Synchronisieren eines Textbereichs ist als ein atomarer Schritt rückgängig machbar.");
Assert(syncHistory.Redo() && coarseLine.Start == TimeSpan.FromSeconds(35) && coarseWord.Start == TimeSpan.FromSeconds(36),
    "Das Synchronisieren eines Textbereichs ist vollständig wiederholbar.");

var acousticDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var acousticLine = Segment("hello world", LyricSegmentType.Line, 0, 6);
var acousticFirstWord = Segment("hello", LyricSegmentType.Word, 1, 2, acousticLine.Id);
var acousticSecondWord = Segment("world", LyricSegmentType.Word, 2, 3, acousticLine.Id);
acousticFirstWord.Children.AddRange([
    Segment("hel", LyricSegmentType.Syllable, 1, 1.55, acousticFirstWord.Id),
    Segment("lo", LyricSegmentType.Syllable, 1.55, 2, acousticFirstWord.Id)
]);
acousticSecondWord.Children.AddRange([
    Segment("wor", LyricSegmentType.Syllable, 2, 2.55, acousticSecondWord.Id),
    Segment("ld", LyricSegmentType.Syllable, 2.55, 3, acousticSecondWord.Id)
]);
acousticLine.Children.AddRange([acousticFirstWord, acousticSecondWord]);
acousticDocument.Lines.Add(acousticLine);
var acousticSamples = new float[6000];
Array.Fill(acousticSamples, .7f, 1000, 1350);
Array.Fill(acousticSamples, .65f, 2650, 2350);
var acousticWaveform = WaveformPyramid.Create(acousticSamples, 1000, 1);
var acousticResult = TimelineEditing.FitSelectionToWaveformRange(acousticDocument,
    [acousticFirstWord, acousticSecondWord], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), acousticWaveform);
Assert(acousticResult.UsedWaveform && acousticResult.AcousticBoundaries >= 1,
    "Synchronisieren nutzt deutliche Aktivitätsübergänge der Vocal-Waveform.");
Assert(acousticFirstWord.End < TimeSpan.FromSeconds(2.85) &&
       acousticSecondWord.Start < TimeSpan.FromSeconds(2.85) &&
       acousticSecondWord.Start >= acousticFirstWord.End,
    "Die Wortgrenze wird vom rein proportionalen Mittelpunkt in das akustische Tal verschoben.");
Assert(acousticFirstWord.Children.All(syllable => syllable.Start >= acousticFirstWord.Start &&
       syllable.End <= acousticFirstWord.End) &&
       acousticSecondWord.Children.All(syllable => syllable.Start >= acousticSecondWord.Start &&
       syllable.End <= acousticSecondWord.End),
    "Silben bleiben nach der akustischen Wortanpassung vollständig in ihrem Wort.");

var flatDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var flatLine = Segment("flat signal", LyricSegmentType.Line, 0, 6);
var flatFirst = Segment("flat", LyricSegmentType.Word, 1, 2, flatLine.Id);
var flatSecond = Segment("signal", LyricSegmentType.Word, 2, 3, flatLine.Id);
flatLine.Children.AddRange([flatFirst, flatSecond]);
flatDocument.Lines.Add(flatLine);
var flatSamples = Enumerable.Repeat(.4f, 6000).ToArray();
var flatResult = TimelineEditing.FitSelectionToWaveformRange(flatDocument,
    [flatFirst, flatSecond], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5),
    WaveformPyramid.Create(flatSamples, 1000, 1));
Assert(!flatResult.UsedWaveform && flatFirst.End == TimeSpan.FromSeconds(3) &&
       flatSecond.Start == TimeSpan.FromSeconds(3),
    "Ohne eindeutige Waveform-Dynamik bleibt die sichere proportionale Einpassung erhalten.");

var clipboardLine = Segment("Hallo Welt", LyricSegmentType.Line, 50, 54);
clipboardLine.StageEffect = StageLineEffect.EmberBurst;
var clipboardWord = Segment("Hallo", LyricSegmentType.Word, 50.5, 52.5, clipboardLine.Id);
var clipboardSyllable = Segment("Hal", LyricSegmentType.Syllable, 50.5, 51.5, clipboardWord.Id);
clipboardWord.Children.Add(clipboardSyllable);
clipboardLine.Children.Add(clipboardWord);
var clipboardPayload = LyricsSegmentClipboard.Create([clipboardLine, clipboardWord]);
Assert(clipboardPayload.Segments.Count == 1 && clipboardPayload.SegmentType == LyricSegmentType.Line,
    "Bei gemeinsamer Auswahl eines Elternsegments und seines Kindes wird nur der vollständige Elternblock kopiert.");
var clipboardText = LyricsSegmentClipboard.Serialize(clipboardPayload);
var restoredPayload = LyricsSegmentClipboard.Deserialize(clipboardText);
var pastedLines = LyricsSegmentClipboard.Instantiate(restoredPayload, TimeSpan.FromSeconds(70), null);
Assert(pastedLines[0].Start == TimeSpan.FromSeconds(70) && pastedLines[0].End == TimeSpan.FromSeconds(74) &&
       pastedLines[0].Children[0].Start == TimeSpan.FromSeconds(70.5) &&
       pastedLines[0].Children[0].ParentId == pastedLines[0].Id &&
       pastedLines[0].Children[0].Children[0].ParentId == pastedLines[0].Children[0].Id,
    "Kopierte Zeilen behalten beim Einfügen ihre relativen Wort- und Silbenzeiten sowie eine neue gültige Hierarchie.");
Assert(pastedLines[0].Id != clipboardLine.Id && pastedLines[0].Children[0].Id != clipboardWord.Id &&
       pastedLines[0].StageEffect == StageLineEffect.EmberBurst,
    "Eingefügte Lyrics erhalten neue IDs und behalten ihre Stage-Darstellung.");
var secondClipboardWord = Segment("Welt", LyricSegmentType.Word, 53, 54, clipboardLine.Id);
var multiWordPayload = LyricsSegmentClipboard.Create([clipboardWord, secondClipboardWord]);
var pastedWords = LyricsSegmentClipboard.Instantiate(multiWordPayload, TimeSpan.FromSeconds(80), Guid.NewGuid());
Assert(pastedWords[0].Start == TimeSpan.FromSeconds(80) && pastedWords[1].Start == TimeSpan.FromSeconds(82.5),
    "Eine Mehrfachauswahl behält beim Einfügen ihre relativen Abstände.");
var editableLines = new List<LyricSegment> { Segment("A", LyricSegmentType.Line, 1, 2) };
var insertedLine = Segment("B", LyricSegmentType.Line, 2, 3);
var lineHistory = new CommandHistory();
lineHistory.Execute(new EditLineCollectionCommand(editableLines, "Zeile einfügen", () =>
{
    if (!editableLines.Contains(insertedLine)) editableLines.Add(insertedLine);
}));
Assert(editableLines.Count == 2 && lineHistory.Undo() && editableLines.Count == 1,
    "Rückgängig entfernt einen neu eingefügten Zeilenblock vollständig.");
Assert(lineHistory.Redo() && editableLines.Count == 2,
    "Vorwärts fügt den Zeilenblock erneut ein.");

var lineBlock = Segment("Hallo Welt", LyricSegmentType.Line, 20, 24);
var blockWord = Segment("Hallo", LyricSegmentType.Word, 20, 22, lineBlock.Id);
var blockSyllable = Segment("Hal", LyricSegmentType.Syllable, 20, 21, blockWord.Id);
blockWord.Children.Add(blockSyllable);
lineBlock.Children.Add(blockWord);
TimelineEditing.ResizeWithDescendants(lineBlock, TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(29),
    TimeSpan.FromMilliseconds(100));
Assert(blockWord.Start == TimeSpan.FromSeconds(21) && blockWord.End == TimeSpan.FromSeconds(25) &&
       blockSyllable.Start == TimeSpan.FromSeconds(21) && blockSyllable.End == TimeSpan.FromSeconds(23),
    "Beim Skalieren eines Zeilenblocks werden Wörter und Silben proportional mit skaliert.");
var followingLine = Segment("Danach", LyricSegmentType.Line, 30, 32);
TimelineEditing.MoveWithChildren(lineBlock, TimeSpan.FromSeconds(-2));
Assert(lineBlock.Start == TimeSpan.FromSeconds(19) && blockWord.Start == TimeSpan.FromSeconds(19) &&
       followingLine.Start == TimeSpan.FromSeconds(30),
    "Ein Zeilenblock wird mitsamt Inhalt verschoben, ohne nachkommende Zeilen zu beeinflussen.");

var containerLine = Segment("Freie Ränder", LyricSegmentType.Line, 10, 20);
var containerFirst = Segment("Freie", LyricSegmentType.Word, 12, 14, containerLine.Id);
var containerLast = Segment("Ränder", LyricSegmentType.Word, 16, 18, containerLine.Id);
var containerSyllable = Segment("Rän", LyricSegmentType.Syllable, 16, 17, containerLast.Id);
containerLast.Children.Add(containerSyllable);
containerLine.Children.AddRange([containerFirst, containerLast]);
TimelineEditing.ResizeLineContainer(containerLine, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(22),
    TimeSpan.FromMilliseconds(100));
Assert(containerFirst.Start == TimeSpan.FromSeconds(12) && containerLast.End == TimeSpan.FromSeconds(18) &&
       containerSyllable.Start == TimeSpan.FromSeconds(16),
    "Beim Vergrößern einer Zeile bleiben Wörter und Silben unverändert.");
TimelineEditing.ResizeLineContainer(containerLine, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(19),
    TimeSpan.FromMilliseconds(100));
Assert(containerFirst.Start == TimeSpan.FromSeconds(12) && containerLast.End == TimeSpan.FromSeconds(18),
    "Beim Verkleinern innerhalb freier Zeilenränder bleiben Kinder unverändert.");
TimelineEditing.ResizeLineContainer(containerLine, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(17),
    TimeSpan.FromMilliseconds(100));
Assert(containerFirst.Start == TimeSpan.FromSeconds(12.5) && containerLast.End == TimeSpan.FromSeconds(17) &&
       containerSyllable.Start > TimeSpan.FromSeconds(15),
    "Erst bei einer Kollision mit dem Wortbereich werden Unterelemente proportional angepasst.");

var lineSequence = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var earlierLine = Segment("Eins", LyricSegmentType.Line, 1, 3);
var laterLine = Segment("Zwei", LyricSegmentType.Line, 4, 6);
lineSequence.Lines.AddRange([earlierLine, laterLine]);
TimelineEditing.MoveLineWithinNeighbors(laterLine, TimeSpan.FromSeconds(-5), earlierLine.End, null);
Assert(laterLine.Start == earlierLine.End && TimelineEditing.ValidateLineSequence(lineSequence).Count == 0,
    "Eine verschobene Zeile stoppt exakt an der vorherigen Zeilengrenze.");
TimelineEditing.ResizeLineWithinNeighbors(earlierLine, earlierLine.Start, TimeSpan.FromSeconds(5),
    TimeSpan.Zero, laterLine.Start, TimeSpan.FromMilliseconds(100));
Assert(earlierLine.End == laterLine.Start && TimelineEditing.ValidateLineSequence(lineSequence).Count == 0,
    "Eine vergrößerte Zeile stoppt exakt am Beginn der nächsten Zeile.");
laterLine.Start = earlierLine.End - TimeSpan.FromMilliseconds(20);
Assert(TimelineEditing.ValidateLineSequence(lineSequence).Count == 1,
    "Eine vorhandene Zeilenüberschneidung wird als nicht speicherbarer Konflikt erkannt.");
laterLine.VoiceLane = 1;
Assert(TimelineEditing.ValidateLineSequence(lineSequence).Count == 0,
    "Zeitgleicher Gesang ist in einer unabhängigen zweiten Gesangsspur zulässig.");
laterLine.VoiceLane = 0;
laterLine.Start = earlierLine.End;
var overflowingWord = Segment("Ausklang", LyricSegmentType.Word, 2.8, 4.2, earlierLine.Id);
earlierLine.Children.Add(overflowingWord);
Assert(TimelineEditing.ValidateLineSequence(lineSequence).Count == 1,
    "Auch ein Wort außerhalb des Zeilenrahmens darf nicht in die nächste Zeile ragen.");
earlierLine.Children.Clear();

var voiceMoveDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var voiceMoveLine = Segment("Eins Zwei Drei", LyricSegmentType.Line, 10, 13);
var voiceMoveOne = Segment("Eins", LyricSegmentType.Word, 10, 11, voiceMoveLine.Id);
var voiceMoveTwo = Segment("Zwei", LyricSegmentType.Word, 11, 12, voiceMoveLine.Id);
var voiceMoveThree = Segment("Drei", LyricSegmentType.Word, 12, 13, voiceMoveLine.Id);
voiceMoveLine.Children.AddRange([voiceMoveOne, voiceMoveTwo, voiceMoveThree]);
voiceMoveDocument.Lines.Add(voiceMoveLine);
var movedVoiceWords = Array.Empty<LyricSegment>();
var voiceMoveHistory = new CommandHistory();
voiceMoveHistory.Execute(new EditLyricsStructureCommand(voiceMoveDocument,
    "Wort zur anderen Stimme", () => movedVoiceWords =
        TimelineEditing.MoveToOtherVoice(voiceMoveDocument, [voiceMoveTwo]).ToArray()));
var backingLine = voiceMoveDocument.Lines.Single(line => line.VoiceLane == 1);
Assert(backingLine.Text == "Zwei" && backingLine.Children.Single() == voiceMoveTwo &&
       voiceMoveTwo.ParentId == backingLine.Id && voiceMoveLine.Text == "Eins Drei",
    "Ein Wort wird als echte Hierarchie in die zweite Gesangsspur verschoben.");
Assert(voiceMoveHistory.Undo() && voiceMoveDocument.Lines.Count == 1 &&
       voiceMoveLine.Children.SequenceEqual([voiceMoveOne, voiceMoveTwo, voiceMoveThree]) &&
       voiceMoveTwo.ParentId == voiceMoveLine.Id,
    "Das Verschieben eines Wortes zur anderen Stimme ist vollständig rückgängig machbar.");
Assert(voiceMoveHistory.Redo() && voiceMoveDocument.Lines.Count == 2 &&
       voiceMoveDocument.Lines.Single(line => line.VoiceLane == 1).Children.Contains(voiceMoveTwo),
    "Das Verschieben eines Wortes zur anderen Stimme kann wiederholt werden.");
var wholeVoiceDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var movedBackingLine = Segment("Komplette Stimme", LyricSegmentType.Line, 20, 22);
wholeVoiceDocument.Lines.Add(movedBackingLine);
TimelineEditing.MoveToOtherVoice(wholeVoiceDocument, [movedBackingLine]);
Assert(movedBackingLine.VoiceLane == 1,
    "Eine vollständige Zeile kann zwischen Stimme 1 und Stimme 2 umgeschaltet werden.");

var songId = Guid.NewGuid();
var dto = new LyricsDto(songId, [new LyricsLineDto(TimeSpan.FromSeconds(1), "Hallo", TimeSpan.FromSeconds(2), 0,
    [new LyricsWordDto(TimeSpan.FromSeconds(1), "Hallo", TimeSpan.FromSeconds(2), 0,
        [new LyricsSyllableDto(TimeSpan.FromSeconds(1), "Hal", TimeSpan.FromSeconds(1.5), 0, .8),
         new LyricsSyllableDto(TimeSpan.FromSeconds(1.5), "lo", TimeSpan.FromSeconds(2), 1, .9)], .85)])]);
var imported = LyricsDocumentImporter.Import(dto, "run-1", "model-1");
Assert(imported.Lines[0].Children[0].Children.Count == 2, "KI-Hierarchie wird vollständig importiert.");
Assert(imported.Segments.All(segment => segment.OriginalStart == segment.Start), "KI-Originalzeiten bleiben erhalten.");
var voiceDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var voiceLine = Segment("Woho", LyricSegmentType.Line, 10, 12);
voiceLine.VoiceLane = 1;
voiceLine.VoiceLabel = "Zweite Stimme";
voiceLine.Children.Add(Segment("Woho", LyricSegmentType.Word, 10, 12, voiceLine.Id));
voiceDocument.Lines.Add(voiceLine);
var voiceLrc = LyricsDocumentLrcExporter.ToEnhancedLrc(voiceDocument);
Assert(voiceLrc.Contains("[neon-voice:1:", StringComparison.Ordinal) &&
       voiceLrc.Contains("<00:10.000,00:12.000>Woho", StringComparison.Ordinal),
    "Mehrstimmen-Metadaten werden verlustfrei in Enhanced LRC exportiert.");
Assert(TimelineEditing.ValidateHierarchy(imported).Count == 0, "Importierte Hierarchie ist gültig.");
var stagePreview = new StageLyricsPreview(imported);
var beforeEntry = stagePreview.Evaluate(TimeSpan.FromMilliseconds(500));
Assert(beforeEntry.Lines.Count == 1 && beforeEntry.ShowEntryCue,
    "Die Editor-Vorschau verwendet die Einsatzlogik der Unity-Stage.");
var duringWord = stagePreview.Evaluate(TimeSpan.FromMilliseconds(1500));
Assert(duringWord.Lines[0].Progress is > .35 and < .65,
    "Die Editor-Vorschau verwendet den wort- und silbengenauen Stage-Fortschritt.");
var middleOfSyllable = stagePreview.Evaluate(TimeSpan.FromMilliseconds(1250));
Assert(Math.Abs(middleOfSyllable.Lines[0].Progress - .3) < .001,
    "Die Stage-Markierung ist auch mitten in einer Silbe millisekundengenau reproduzierbar.");

var insertedWord = Segment("toys", LyricSegmentType.Word, 2, 3, imported.Lines[0].Id);
insertedWord.Children.Add(Segment("toys", LyricSegmentType.Syllable, 2, 3, insertedWord.Id));
imported.Lines[0].Children.Add(insertedWord);
var insertedPreview = new StageLyricsPreview(imported).Evaluate(TimeSpan.FromSeconds(2.5));
Assert(insertedPreview.Lines[0].Text == "Hallo toys" && insertedPreview.Lines[0].Progress is > .7 and < .9,
    "Ein eingefügtes Wort erscheint sofort in Text und fortlaufender Karaoke-Markierung.");

var presentationWindowLines = new[]
{
    new StagePresentationLine(1, 2, "Erste Zeile",
        [new StagePresentationWord(1, 2, "Erste Zeile", [], .9)], null, "Automatic", 0, "Lead"),
    new StagePresentationLine(5, 6, "Zweite Zeile",
        [new StagePresentationWord(5, 6, "Zweite Zeile", [], .9)], null, "Automatic", 0, "Lead")
};
var automaticWindow = new StagePresentationEngine(presentationWindowLines);
Assert(automaticWindow.Evaluate(2.4).Lines[0].Text == "Erste Zeile" &&
       automaticWindow.Evaluate(2.4).Alpha > 0 && automaticWindow.Evaluate(2.9).Alpha == 0,
    "Nach dem letzten Wort bleibt eine Zeile für die Wahrnehmungszeit stehen und blendet danach aus.");
Assert(automaticWindow.Evaluate(3.4).Lines[0].Text == "Zweite Zeile" &&
       automaticWindow.Evaluate(3.4).Alpha is > 0 and < 1,
    "Die nächste Zeile wird automatisch vor ihrem ersten Wort eingeblendet.");
var extendedWindow = new StagePresentationEngine(
[
    new StagePresentationLine(1, 3.1, "Erste Zeile",
        [new StagePresentationWord(1, 2, "Erste Zeile", [], .9)], null, "Automatic", 0, "Lead"),
    presentationWindowLines[1]
]);
Assert(extendedWindow.Evaluate(2.8).Alpha > 0,
    "Ein nach rechts verlängerter Zeilenbalken übersteuert den automatischen Nachlauf.");
var earlyWindow = new StagePresentationEngine(
[
    presentationWindowLines[0],
    new StagePresentationLine(3, 6, "Zweite Zeile",
        [new StagePresentationWord(5, 6, "Zweite Zeile", [], .9)], null, "Automatic", 0, "Lead")
]);
Assert(earlyWindow.Evaluate(3.05).Lines[0].Text == "Zweite Zeile",
    "Ein nach links verlängerter Zeilenbalken blendet die nächste Zeile entsprechend früher ein.");
var protectedPageBreak = new StagePresentationEngine(
[
    new StagePresentationLine(0, 1, "Erste laufende Zeile",
        [new StagePresentationWord(0, 1, "Erste laufende Zeile", [], .9)], null, "Automatic", 0, "Lead"),
    new StagePresentationLine(1, 2, "Zweite laufende Zeile",
        [new StagePresentationWord(1, 2, "Zweite laufende Zeile", [], .9)], null, "Automatic", 0, "Lead"),
    new StagePresentationLine(2, 3, "Gefährdete Schlusszeile",
        [new StagePresentationWord(2, 3, "Gefährdete Schlusszeile", [], .9)], null, "Automatic", 0, "Lead"),
    new StagePresentationLine(3.05, 4, "Erste Zeile der Folgeseite",
        [new StagePresentationWord(3.05, 4, "Erste Zeile der Folgeseite", [], .9)], null, "Automatic", 0, "Lead")
]);
Assert(protectedPageBreak.Evaluate(1.9).Lines.Any(line => line.Text == "Zweite laufende Zeile"),
    "Der Vorlauf einer Folgeseite darf die noch gesungene aktuelle Seite niemals abschneiden.");
var carriedPage = protectedPageBreak.Evaluate(2.1);
Assert(carriedPage.Lines.Any(line => line.Text == "Gefährdete Schlusszeile") &&
       carriedPage.Lines.Any(line => line.Text == "Erste Zeile der Folgeseite"),
    "Eine gefährdete Schlusszeile wird bei ausreichendem Platz auf die Folgeseite umgehängt.");

var sharedPresentation = new StagePresentationEngine(
[
    new StagePresentationLine(1, 3, "Hallo Welt",
    [
        new StagePresentationWord(1, 2, "Hallo",
        [
            new StagePresentationSyllable(1, 1.5, "Hal", .9),
            new StagePresentationSyllable(1.5, 2, "lo", .9)
        ], .9),
        new StagePresentationWord(2.2, 3, "Welt", [], .9)
    ], null, "Pulse", 0, "Lead"),
    new StagePresentationLine(1.5, 2.7, "Zweite Stimme",
    [
        new StagePresentationWord(1.5, 2, "Zweite", [], .8),
        new StagePresentationWord(2.1, 2.7, "Stimme", [], .8)
    ], null, "Automatic", 1, "Backing"),
    new StagePresentationLine(5, 6, "Nach der Pause", [], .25, "Automatic", 0, "Lead"),
    new StagePresentationLine(6.4, 7, "Danach", [], null, "Automatic", 0, "Lead")
]);
var sharedCue = sharedPresentation.Evaluate(0);
Assert(sharedCue.ShowEntryCue && sharedCue.ShowEntryCountdown &&
       Math.Abs(sharedCue.EntryCueRemainingSeconds - 1) < .001,
    "Editor und Unity erhalten denselben gemeinsamen Einsatz- und Countdown-Zustand.");
var perceptualPresentation = new StagePresentationEngine(
[
    new StagePresentationLine(1, 2, "Einsatz", [new StagePresentationWord(1, 2, "Einsatz", [], .9)],
        null, "Automatic", 0, "Lead")
], StagePresentationEngine.PerceptualHighlightLeadSeconds);
var exactPresentation = new StagePresentationEngine(
[
    new StagePresentationLine(1, 2, "Einsatz", [new StagePresentationWord(1, 2, "Einsatz", [], .9)],
        null, "Automatic", 0, "Lead")
]);
var karaokePresentation = new StagePresentationEngine(
[
    new StagePresentationLine(1, 2, "Einsatz", [new StagePresentationWord(1, 2, "Einsatz", [], .9)],
        null, "Automatic", 0, "Lead")
], StagePresentationEngine.PerceptualHighlightLeadSeconds, karaokeTimingEnabled: true);
Assert(exactPresentation.Evaluate(.96).Lines[0].Progress == 0 &&
       perceptualPresentation.Evaluate(.96).Lines[0].Progress > 0 &&
       perceptualPresentation.Evaluate(.96).ShowEntryCue == exactPresentation.Evaluate(.96).ShowEntryCue,
    "Der Wahrnehmungs-Vorlauf verschiebt nur die sichtbare Wortfüllung, nicht Einsatzsignal oder Lyrics-Timing.");
Assert(exactPresentation.Evaluate(.86).Lines[0].Progress == 0 &&
       perceptualPresentation.Evaluate(.86).Lines[0].Progress == 0 &&
       karaokePresentation.Evaluate(.86).Lines[0].Progress > 0 &&
       karaokePresentation.Evaluate(.86).ShowEntryCue == exactPresentation.Evaluate(.86).ShowEntryCue,
    "Das adaptive Karaoke-Timing bereitet einen Phraseneinsatz früher vor, ohne Einsatzsignal oder kanonische Zeit zu verschieben.");

var sustainedWord = new StagePresentationWord(10, 11, "Haaaaallo",
    [
        new StagePresentationSyllable(10, 10.3, "Haa", .9, false,
            [new StagePresentationNote(10, 11, 67, .9)]),
        new StagePresentationSyllable(10.3, 11, "aaallo", .9)
    ], .9);
var sustainedTimeline = KaraokeHighlightTimeline.Create(sustainedWord, KaraokeHighlightTimelineOptions.Default);
Assert(sustainedTimeline?.Segments.All(segment => segment.Type == KaraokeHighlightSegmentType.Advance) == true,
    "Ein durchgehender Ton bleibt ohne HOLD/SNAP kontinuierlich.");

var separatedWord = new StagePresentationWord(12, 13.2, "Hallo",
    [
        new StagePresentationSyllable(12, 12.25, "Hal", .9, false,
            [new StagePresentationNote(12, 12.25, 67, .9)]),
        new StagePresentationSyllable(12.5, 13.2, "lo", .9, false,
            [new StagePresentationNote(12.5, 13.2, 69, .9)])
    ], .9);
var separatedTimeline = KaraokeHighlightTimeline.Create(separatedWord, KaraokeHighlightTimelineOptions.Default);
var separatedTypes = separatedTimeline?.Segments.Select(segment => segment.Type).ToArray();
Assert(separatedTypes is not null &&
       separatedTypes.SequenceEqual(new[]
       {
           KaraokeHighlightSegmentType.Advance, KaraokeHighlightSegmentType.Hold,
           KaraokeHighlightSegmentType.Snap, KaraokeHighlightSegmentType.Advance
       }),
    "Eine klare Gesangspause erzeugt ADVANCE, HOLD, SNAP und danach wieder ADVANCE.");
Assert(separatedTimeline is not null &&
       separatedTimeline.Evaluate(12.4) == separatedTimeline.Evaluate(12.25),
    "Während einer erkannten Pause bleibt der sichtbare Fortschritt stehen.");

var tinyGapWord = new StagePresentationWord(15, 15.55, "Hallo",
    [
        new StagePresentationSyllable(15, 15.25, "Hal", .9, false,
            [new StagePresentationNote(15, 15.25, 67, .9)]),
        new StagePresentationSyllable(15.29, 15.55, "lo", .9, false,
            [new StagePresentationNote(15.29, 15.55, 69, .9)])
    ], .9);
var tinyGapTimeline = KaraokeHighlightTimeline.Create(tinyGapWord, KaraokeHighlightTimelineOptions.Default);
Assert(tinyGapTimeline?.Segments.All(segment => segment.Type == KaraokeHighlightSegmentType.Advance) == true,
    "Ein nur 40 ms langes Decoderloch wird als Legato behandelt.");

var melismaWord = new StagePresentationWord(17, 18.2, "Loooooove",
    [new StagePresentationSyllable(17, 18.2, "Loooooove", .9, false,
         [new StagePresentationNote(17, 17.5, 67, .9),
          new StagePresentationNote(17.55, 18.2, 70, .9)])], .9);
var melismaTimeline = KaraokeHighlightTimeline.Create(melismaWord, KaraokeHighlightTimelineOptions.Default);
Assert(melismaWord.Syllables.Count == 1 && melismaTimeline is not null,
    "Mehrere Noten in einer Silbe erzeugen keine sprachliche Zusatzsilbe.");

var outlierWord = new StagePresentationWord(20, 20.7, "Hallo",
    [
        new StagePresentationSyllable(20, 20.25, "Hal", .9, false,
            [new StagePresentationNote(20, 20.25, 67, .9)]),
        new StagePresentationSyllable(20.25, 20.7, "lo", .9, false,
            [new StagePresentationNote(20.25, 20.7, 69, .9),
             new StagePresentationNote(20.33, 20.34, 72, .2)])
    ], .9);
Assert(KaraokeHighlightTimeline.Create(outlierWord, KaraokeHighlightTimelineOptions.Default)?.Segments.All(
        segment => segment.Type == KaraokeHighlightSegmentType.Advance) == true,
    "Einzelne niedrigkonfidente Onset-Ausreißer zerstören die Timeline nicht.");
Assert(KaraokeHighlightTimeline.Create(
        new StagePresentationWord(22, 23, "Oh", [], .9), KaraokeHighlightTimelineOptions.Default) is null,
    "Ohne Silben und Pitch-Daten greift der bestehende lineare Fallback.");

var legatoWord = new StagePresentationWord(25, 26.2, "believe",
    [
        new StagePresentationSyllable(25, 25.4, "be", .9, false,
            [new StagePresentationNote(25, 26.2, 67, .9)]),
        new StagePresentationSyllable(25.4, 26.2, "lieve", .9)
    ], .9);
Assert(KaraokeHighlightTimeline.Create(legatoWord, KaraokeHighlightTimelineOptions.Default)?.Segments.All(
        segment => segment.Type == KaraokeHighlightSegmentType.Advance) == true,
    "Eine linguistische Silbengrenze allein erzeugt keinen künstlichen Stopp.");
var sharedDuet = sharedPresentation.Evaluate(1.75);
Assert(sharedDuet.Lines.Count == 2 && sharedDuet.Lines[0].VoiceLane == 0 &&
       sharedDuet.Lines[1].VoiceLane == 1 && sharedDuet.Lines[0].StageEffect == "Pulse",
    "Die gemeinsame Engine erhält parallele Stimmen und Stage-Effekte im selben Frame.");
var sharedFrameBeforeSeek = sharedPresentation.Evaluate(2.35);
_ = sharedPresentation.Evaluate(6.7);
var sharedFrameAfterSeek = sharedPresentation.Evaluate(2.35);
Assert(sharedFrameBeforeSeek.PageIndex == sharedFrameAfterSeek.PageIndex &&
       sharedFrameBeforeSeek.Lines.Count == sharedFrameAfterSeek.Lines.Count &&
       Math.Abs(sharedFrameBeforeSeek.Lines[0].Progress - sharedFrameAfterSeek.Lines[0].Progress) < .000001,
    "Die gemeinsame Stage-Timeline ist zustandslos und liefert nach beliebigem Seek denselben Frame.");

var samples = Enumerable.Range(0, 32768).Select(index => (float)Math.Sin(index / 20d)).ToArray();
var waveform = WaveformPyramid.Create(samples, 8000, 8);
Assert(waveform.Levels.Count > 1, "Waveform wird in mehreren vorberechneten Auflösungen aufgebaut.");
Assert(waveform.SelectLevel(.1).SamplesPerPeak > waveform.Levels[0].SamplesPerPeak,
    "Weite Zoomstufen verwenden eine gröbere gecachte Waveform.");

const string relativeUltraStar = """
#TITLE:Timing Demo
#ARTIST:Example Artist
#MP3:demo.mp3
#YEAR:2024
#LANGUAGE:English
#CREATOR:Test Author
#EDITION:Studio
#RELATIVE:yes
#BPM:120,0
#GAP:1000
: 0 2 60 Hel
: 2 2 60 lo
- 8 8
* 0 2 62 New line
F 2 2 62
E
""";
Assert(UltraStarLyricsImporter.LooksLikeUltraStar(relativeUltraStar),
    "UltraStar Deluxe TXT wird anhand von Header und Notenzeilen erkannt.");
Assert(UltraStarLyricsImporter.LooksLikeUltraStar("#BPM:120\n: invalid"),
    "Auch ein beschädigtes UltraStar-Dokument wird erkannt, damit es nicht als Plaintext importiert wird.");
var ultraStar = UltraStarLyricsImporter.Parse(relativeUltraStar);
Assert(ultraStar.Metadata is { Title: "Timing Demo", Artist: "Example Artist", Relative: true } &&
       ultraStar.Metadata is { Year: "2024", Language: "English", Creator: "Test Author", Edition: "Studio" } &&
       ultraStar.Metadata.Bpm == 120 && ultraStar.Metadata.GapMilliseconds == 1000,
    "UltraStar-Metadaten, Versionseigenschaften, Dezimalkomma, GAP und RELATIVE werden übernommen.");
Assert(ultraStar.Lines.Count == 2 && ultraStar.Lines[0].Start == TimeSpan.FromSeconds(1) &&
       ultraStar.Lines[1].Start == TimeSpan.FromSeconds(2),
    "Relative UltraStar-Beats werden mit der offiziellen Viertelbeat-Zeitbasis umgerechnet.");
var declaredEndImport = UltraStarLyricsImporter.Parse(relativeUltraStar.Replace(
    "#GAP:1000", "#GAP:1000\n#END:123456", StringComparison.Ordinal));
Assert(declaredEndImport.Metadata.DeclaredEndMilliseconds == 123456,
    "Die deklarierte UltraStar-Medienlänge #END bleibt für den Aufnahmevergleich erhalten.");
Assert(ultraStar.Lines[0].Words is [{ Text: "Hello", Syllables.Count: 2 }] &&
       ultraStar.Lines[1].Words is [{ Text: "New" }, { Text: "line" }],
    "UltraStar-Noten werden anhand ihrer Leerzeichen zu Wörtern und Silben zusammengesetzt.");

const string longUltraStarTiming = "#BPM:123.456\n#GAP:321.5\n: 0 1 60 start\n- 1\n: 987654 1 60 end\nE";
var longTimingImport = UltraStarLyricsImporter.Parse(longUltraStarTiming);
var expectedLongTiming = TimeSpan.FromMilliseconds(321.5 + 987654 * 15_000d / 123.456);
Assert(longTimingImport.Lines[1].Start == expectedLongTiming,
    "UltraStar-Beats werden auch weit hinten absolut berechnet; es entsteht keine kumulative Rundungsdrift.");

const string ultraStarMelisma = """
#TITLE:Melisma Test
#ARTIST:Example
#BPM:120
#GAP:0
: 0 2 60 hea
: 2 2 62 ~rts
: 4 2 64 ~
: 6 2 64  beat
: 8 2 65  fi~
: 10 4 67 ~re
- 14
E
""";
var melismaImport = UltraStarLyricsImporter.Parse(ultraStarMelisma);
var melismaWords = melismaImport.Lines.Single().Words!;
Assert(melismaWords.Select(word => word.Text).SequenceEqual(["hearts", "beat", "fire"]) &&
       melismaWords.SelectMany(word => word.Syllables!).All(syllable => !syllable.Text.Contains('~')),
    "UltraStar-Melismazeichen erscheinen weder als Wort noch als sichtbare Silbe.");
Assert(melismaWords[0].Syllables!.Count == 1 && melismaWords[0].Syllables![0].Text == "hearts" &&
       melismaWords[0].Start == TimeSpan.Zero && melismaWords[0].End == TimeSpan.FromMilliseconds(750) &&
       melismaWords[2].Syllables!.Count == 1 && melismaWords[2].Text == "fire",
    "Alleinstehende und texttragende Tilden verlängern dieselbe Silbe über ihre zusätzlichen Noten.");
var ultraStarDocument = ultraStar.ToEditorDocument(Guid.NewGuid());
Assert(ultraStarDocument.Segments.All(segment => segment.Origin == SegmentOrigin.ImportedFromUltraStar) &&
       ultraStarDocument.UsesUltraStarTiming &&
       ReferenceEquals(KaraokeTimingProjection.Create(ultraStarDocument, [1, 1.5, 2]), ultraStarDocument) &&
       TimelineEditing.ValidateLineSequence(ultraStarDocument).Count == 0,
    "Der Editor erhält eine vollständige, kollisionsfreie UltraStar-Hierarchie mit nachvollziehbarer Herkunft.");
var beatProjectionDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var beatProjectionLine = Segment("Beat Test", LyricSegmentType.Line, 1.08, 1.9);
beatProjectionLine.Children.Add(Segment("Beat", LyricSegmentType.Word, 1.08, 1.42, beatProjectionLine.Id));
beatProjectionLine.Children.Add(Segment("Test", LyricSegmentType.Word, 1.53, 1.9, beatProjectionLine.Id));
beatProjectionDocument.Lines.Add(beatProjectionLine);
var karaokeProjection = KaraokeTimingProjection.Create(beatProjectionDocument, [1, 1.5, 2]);
Assert(karaokeProjection.Lines[0].Children[0].Start == TimeSpan.FromSeconds(1.125) &&
       beatProjectionDocument.Lines[0].Children[0].Start == TimeSpan.FromSeconds(1.08),
    "Die Karaoke-Vorschau quantisiert plausible Grenzen auf musikalische Unterteilungen, ohne den Arbeitsstand zu verändern.");
beatProjectionDocument.Lines[0].Children[0].KaraokeTimingLocked = true;
var lockedKaraokeProjection = KaraokeTimingProjection.Create(beatProjectionDocument, [1, 1.5, 2]);
Assert(lockedKaraokeProjection.Lines[0].Children[0].Start == TimeSpan.FromSeconds(1.08) &&
       lockedKaraokeProjection.Lines[0].Children[0].End == TimeSpan.FromSeconds(1.42),
    "Eine im Beat-Modus editierte Wortgrenze bleibt bei späteren Vorschauen exakt erhalten.");
Assert(ultraStar.ToEnhancedLrc().Contains("<00:01.000,00:01.500>Hello", StringComparison.Ordinal),
    "Der Server kann UltraStar-Timing verlustarm als Enhanced LRC an die Pipeline übergeben.");
Assert(ultraStar.ToEnhancedLrc().Contains("[neon-editor-syllables:", StringComparison.Ordinal),
    "UltraStar-Noten bleiben als starke Silbenreferenzen für den nachgelagerten Aligner erhalten.");

var exportedEditorLrc = LyricsDocumentLrcExporter.ToEnhancedLrc(new LyricsEditorDocument
{
    SongId = Guid.Parse("24f786c3-38a7-46bf-ac04-843328aa3124"),
    Lines =
    [
        new LyricSegment
        {
            Id = Guid.NewGuid(), Type = LyricSegmentType.Line,
            Start = TimeSpan.FromSeconds(61.25), End = TimeSpan.FromSeconds(63), Text = "Sing loud",
            Children =
            [
                new LyricSegment { Id = Guid.NewGuid(), Type = LyricSegmentType.Word,
                    Start = TimeSpan.FromSeconds(61.25), End = TimeSpan.FromSeconds(61.8), Text = "Sing" },
                new LyricSegment { Id = Guid.NewGuid(), Type = LyricSegmentType.Word,
                    Start = TimeSpan.FromSeconds(62.1), End = TimeSpan.FromSeconds(63), Text = "loud" }
            ]
        }
    ]
});
Assert(exportedEditorLrc == "[01:01.250]<01:01.250,01:01.800>Sing <01:02.100,01:03.000>loud" + Environment.NewLine,
    "Editor revisions should export their exact word windows as enhanced LRC");
var manualEditorLrc = LyricsDocumentLrcExporter.ToEnhancedLrc(new LyricsEditorDocument
{
    SongId = Guid.NewGuid(),
    Lines =
    [
        new LyricSegment
        {
            Id = Guid.NewGuid(), Type = LyricSegmentType.Line,
            Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(12), Text = "Keep timing",
            IsManuallyAdjusted = true
        }
    ]
});
Assert(manualEditorLrc.StartsWith("[neon-manual:10.0000000,12.0000000]" + Environment.NewLine,
        StringComparison.Ordinal),
    "Manually adjusted editor lines carry an immutable timing range into realignment");
var manualSyllableLrc = LyricsDocumentLrcExporter.ToEnhancedLrc(new LyricsEditorDocument
{
    SongId = Guid.NewGuid(),
    Lines =
    [
        new LyricSegment
        {
            Id = Guid.NewGuid(), Type = LyricSegmentType.Line,
            Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(21), Text = "Träume",
            Children =
            [
                new LyricSegment
                {
                    Id = Guid.NewGuid(), Type = LyricSegmentType.Word,
                    Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(21), Text = "Träume",
                    Children =
                    [
                        new LyricSegment { Id = Guid.NewGuid(), Type = LyricSegmentType.Syllable,
                            Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(20.61),
                            Text = "Träu", IsManuallyAdjusted = true },
                        new LyricSegment { Id = Guid.NewGuid(), Type = LyricSegmentType.Syllable,
                            Start = TimeSpan.FromSeconds(20.61), End = TimeSpan.FromSeconds(21),
                            Text = "me", IsManuallyAdjusted = true }
                    ]
                }
            ]
        }
    ]
});
Assert(manualSyllableLrc.StartsWith("[neon-manual:20.0000000,21.0000000]" + Environment.NewLine,
        StringComparison.Ordinal) &&
       manualSyllableLrc.Contains("[neon-editor-syllables:", StringComparison.Ordinal) &&
       manualSyllableLrc.IndexOf("[neon-editor-syllables:", StringComparison.Ordinal) <
       manualSyllableLrc.IndexOf("[00:20.000]", StringComparison.Ordinal) &&
       manualSyllableLrc.Contains(Environment.NewLine + "[00:20.000]<00:20.000,00:21.000>Träume",
           StringComparison.Ordinal),
    "Manual syllable edits recursively mark their line and are serialized for realignment");

const string absoluteUltraStar = """
#TITLE:Absolute Demo
#ARTIST:Example Artist
#BPM:100.0
#GAP:0
: 10 0 60 two words
- 20
: 20 2 60 done
E
""";
var absoluteImport = UltraStarLyricsImporter.Parse(absoluteUltraStar);
Assert(absoluteImport.Lines[0].Start == TimeSpan.FromSeconds(1.5) &&
       absoluteImport.Lines[0].Words is [{ Text: "two" }, { Text: "words" }] &&
       absoluteImport.Warnings.Count > 0,
    "Absolute Beats, Dezimalpunkt, mehrere Wörter in einer Note und Noten ohne Dauer werden robust normalisiert.");

try
{
    UltraStarLyricsImporter.Parse("#BPM:120\nB 8 140\n: 0 2 60 demo\nE");
    throw new InvalidOperationException("Test fehlgeschlagen: Variable BPM hätte abgelehnt werden müssen.");
}
catch (UltraStarFormatException)
{
    Console.WriteLine("OK: Nicht verlustfrei unterstützte variable BPM werden explizit abgelehnt.");
}

LyricsEditorDocument? replacedDocument = ultraStarDocument;
var replacementDocument = absoluteImport.ToEditorDocument(ultraStarDocument.SongId);
var replaceHistory = new CommandHistory();
replaceHistory.Execute(new ReplaceLyricsDocumentCommand(replacedDocument, replacementDocument,
    value => replacedDocument = value, "Lyrics ersetzen"));
Assert(ReferenceEquals(replacedDocument, replacementDocument),
    "Ein UltraStar-Import kann die Lyrics eines bestehenden Projekts vollständig ersetzen.");
Assert(replaceHistory.Undo() && ReferenceEquals(replacedDocument, ultraStarDocument),
    "Das vollständige Ersetzen der Lyrics ist rückgängig machbar.");
Assert(replaceHistory.Redo() && ReferenceEquals(replacedDocument, replacementDocument),
    "Das vollständige Ersetzen der Lyrics kann wiederholt werden.");

var pitchEvidence = AlignmentPitchEvidence.Parse("""
{
  "basic_pitch_evidence": {
    "pitch_timeline": {
      "events": [
        { "start": 1.25, "end": 1.75, "midi": 64, "amplitude": 0.8, "line": 2 },
        { "start": 2.0, "end": 1.9, "midi": 60, "amplitude": 0.5 }
      ]
    }
  }
}
""");
Assert(pitchEvidence is [{ Midi: 64, Line: 2 }] &&
       pitchEvidence[0].Start == TimeSpan.FromSeconds(1.25) &&
       pitchEvidence[0].End == TimeSpan.FromSeconds(1.75),
    "Die Editor-Pitch-Spur übernimmt nur gültige Basic-Pitch-Noten aus dem technischen Bericht.");
Assert(AlignmentPitchEvidence.Parse("kein JSON").Count == 0,
    "Ein beschädigter älterer Alignment-Bericht deaktiviert die Pitch-Spur sicher.");
var structuredPitch = AlignmentPitchEvidence.Parse("""
{
  "basic_pitch_analysis": {
    "notes": [{
      "start": 1.1, "end": 1.8, "midi": 67, "confidence": 0.91,
      "track_id": "basic-pitch", "singer_id": null,
      "contour": [{ "time": 1.2, "midi": 67.2, "confidence": 0.8 }]
    }]
  }
}
""");
Assert(structuredPitch is [{ Midi: 67, SingerId: null, NoteTrackId: "basic-pitch" }] &&
       structuredPitch[0].Contour is [{ Midi: 67.2 }],
    "Strukturierte Noten behalten Track, leere Singer-ID und Pitch-Contour.");
var pitchDocument = new LyricsEditorDocument { SongId = Guid.CreateVersion7() };
var pitchLine = Segment("line", LyricSegmentType.Line, 1, 2);
var pitchWord = Segment("word", LyricSegmentType.Word, 1, 2, pitchLine.Id);
var pitchSyllable = Segment("syllable", LyricSegmentType.Syllable, 1, 2, pitchWord.Id);
pitchWord.Children.Add(pitchSyllable);
pitchLine.Children.Add(pitchWord);
pitchDocument.Lines.Add(pitchLine);
Assert(AlignmentPitchEvidence.AttachToSyllables(pitchDocument, structuredPitch) == 1 &&
       pitchSyllable.Notes.Count == 1,
    "Noten werden über Zeitüberlappung an vorhandene Silben gebunden, ohne Silben zu erzeugen.");

if (args is ["--usdx-corpus", var corpusPath])
{
    var files = Directory.EnumerateFiles(corpusPath, "*.txt", SearchOption.AllDirectories).ToArray();
    var candidates = 0;
    var accepted = 0;
    var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var file in files)
    {
        var text = File.ReadAllText(file);
        if (!UltraStarLyricsImporter.LooksLikeUltraStar(text)) continue;
        candidates++;
        try
        {
            var parsed = UltraStarLyricsImporter.Parse(text);
            if (!parsed.Lines.Zip(parsed.Lines.Skip(1)).All(pair => pair.First.End <= pair.Second.Start))
                throw new InvalidOperationException("Corpus-Import erzeugt überlappende Zeilen.");
            accepted++;
        }
        catch (UltraStarFormatException exception)
        {
            var reason = exception.LineNumber > 0 && exception.EnglishMessage.Contains(": ", StringComparison.Ordinal)
                ? exception.EnglishMessage[(exception.EnglishMessage.IndexOf(": ", StringComparison.Ordinal) + 2)..]
                : exception.EnglishMessage;
            rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
        }
    }
    Console.WriteLine($"UltraStar-Corpus: {accepted}/{candidates} kompatible Dateien akzeptiert; {rejected.Values.Sum()} kontrolliert abgelehnt.");
    foreach (var reason in rejected.OrderByDescending(item => item.Value))
        Console.WriteLine($"  {reason.Value} × {reason.Key}");
}

Console.WriteLine("Lyrics-Editor-Core-Tests erfolgreich.");

static LyricSegment Segment(string text, LyricSegmentType type, double start, double end, Guid? parent = null) => new()
{
    Id = Guid.NewGuid(), ParentId = parent, Type = type, Text = text,
    Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end),
    OriginalStart = TimeSpan.FromSeconds(start), OriginalEnd = TimeSpan.FromSeconds(end), OriginalText = text,
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Test fehlgeschlagen: " + message);
    Console.WriteLine("OK: " + message);
}
