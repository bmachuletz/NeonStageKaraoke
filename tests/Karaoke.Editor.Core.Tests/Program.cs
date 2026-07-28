using Karaoke.Contracts;
using Karaoke.Editor.Core;

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
laterLine.Start = earlierLine.End;
var overflowingWord = Segment("Ausklang", LyricSegmentType.Word, 2.8, 4.2, earlierLine.Id);
earlierLine.Children.Add(overflowingWord);
Assert(TimelineEditing.ValidateLineSequence(lineSequence).Count == 1,
    "Auch ein Wort außerhalb des Zeilenrahmens darf nicht in die nächste Zeile ragen.");
earlierLine.Children.Clear();

var songId = Guid.NewGuid();
var dto = new LyricsDto(songId, [new LyricsLineDto(TimeSpan.FromSeconds(1), "Hallo", TimeSpan.FromSeconds(2), 0,
    [new LyricsWordDto(TimeSpan.FromSeconds(1), "Hallo", TimeSpan.FromSeconds(2), 0,
        [new LyricsSyllableDto(TimeSpan.FromSeconds(1), "Hal", TimeSpan.FromSeconds(1.5), 0, .8),
         new LyricsSyllableDto(TimeSpan.FromSeconds(1.5), "lo", TimeSpan.FromSeconds(2), 1, .9)], .85)])]);
var imported = LyricsDocumentImporter.Import(dto, "run-1", "model-1");
Assert(imported.Lines[0].Children[0].Children.Count == 2, "KI-Hierarchie wird vollständig importiert.");
Assert(imported.Segments.All(segment => segment.OriginalStart == segment.Start), "KI-Originalzeiten bleiben erhalten.");
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

var holdDocument = new LyricsEditorDocument { SongId = Guid.NewGuid() };
var heldLine = Segment("Erste Zeile", LyricSegmentType.Line, 1, 2);
heldLine.HoldAfterMilliseconds = 200;
holdDocument.Lines.Add(heldLine);
holdDocument.Lines.Add(Segment("Zweite Zeile", LyricSegmentType.Line, 2.3, 3.2));
var heldFrame = new StageLyricsPreview(holdDocument).Evaluate(TimeSpan.FromSeconds(2.25));
Assert(heldFrame.Lines.Count == 1 && heldFrame.Lines[0].Text == "Zweite Zeile",
    "Eine manuelle Haltezeit beendet den Absatz an der konfigurierten Zeile.");

var samples = Enumerable.Range(0, 32768).Select(index => (float)Math.Sin(index / 20d)).ToArray();
var waveform = WaveformPyramid.Create(samples, 8000, 8);
Assert(waveform.Levels.Count > 1, "Waveform wird in mehreren vorberechneten Auflösungen aufgebaut.");
Assert(waveform.SelectLevel(.1).SamplesPerPeak > waveform.Levels[0].SamplesPerPeak,
    "Weite Zoomstufen verwenden eine gröbere gecachte Waveform.");

const string relativeUltraStar = """
#TITLE:Timing Demo
#ARTIST:Example Artist
#MP3:demo.mp3
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
       ultraStar.Metadata.Bpm == 120 && ultraStar.Metadata.GapMilliseconds == 1000,
    "UltraStar-Metadaten, Dezimalkomma, GAP und RELATIVE werden übernommen.");
Assert(ultraStar.Lines.Count == 2 && ultraStar.Lines[0].Start == TimeSpan.FromSeconds(1) &&
       ultraStar.Lines[1].Start == TimeSpan.FromSeconds(2),
    "Relative UltraStar-Beats werden mit der offiziellen Viertelbeat-Zeitbasis umgerechnet.");
Assert(ultraStar.Lines[0].Words is [{ Text: "Hello", Syllables.Count: 2 }] &&
       ultraStar.Lines[1].Words is [{ Text: "New" }, { Text: "line" }],
    "UltraStar-Noten werden anhand ihrer Leerzeichen zu Wörtern und Silben zusammengesetzt.");
var ultraStarDocument = ultraStar.ToEditorDocument(Guid.NewGuid());
Assert(ultraStarDocument.Segments.All(segment => segment.Origin == SegmentOrigin.ImportedFromUltraStar) &&
       TimelineEditing.ValidateLineSequence(ultraStarDocument).Count == 0,
    "Der Editor erhält eine vollständige, kollisionsfreie UltraStar-Hierarchie mit nachvollziehbarer Herkunft.");
Assert(ultraStar.ToEnhancedLrc().Contains("<00:01.000,00:01.500>Hello", StringComparison.Ordinal),
    "Der Server kann UltraStar-Timing verlustarm als Enhanced LRC an die Pipeline übergeben.");

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
