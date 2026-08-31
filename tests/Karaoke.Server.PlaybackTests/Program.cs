using Karaoke.Contracts;
using Karaoke.Editor.Core;
using Karaoke.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

var databasePath = Path.Combine(Path.GetTempPath(), $"neon-stage-playback-{Guid.NewGuid():N}.db");
string? adoptionLibraryPath = null;
string? adoptionDatabasePath = null;
try
{
    await VerifyUsdbHttpAndMatchingAsync();
    await VerifyLegacyUsdbChartSkipsAiAlignmentAsync();
    await VerifyAnimuxUsdbPreferenceAndFallbackAsync();
    await VerifyUsdbFailureFallsBackCleanlyAsync();
    await VerifyUsdbEditorPickerCreatesIsolatedVersionAsync();
    VerifyUsdbRecordingTimingDiagnostics();
    await VerifyAlignmentSyllablesSkipDisplayBoundaryLinesAsync();
    await VerifyOverlappingAlignmentSnapshotsLandInReviewCategoryAsync();
    await VerifyFirstSongStartsActiveStageAsync();
    var options = Options.Create(new KaraokeOptions { DatabasePath = databasePath });
    var events = new EventRepository(options);
    var queue = new QueueService(options, new TestHubContext(), events);
    await queue.InitializeAsync(default);
    var standardEvent = await events.CreateAsync(new CreateKaraokeEventRequest(
        "Standard Stage Test", DateTimeOffset.UtcNow.AddHours(2)), default);
    Assert(standardEvent.StageThemeId == EventRepository.DefaultStageThemeId,
        "Events ohne Auswahl verwenden weiterhin die bisherige Standardbühne.");
    var first = await SeedSongAsync(databasePath, "Erster Song");
    var second = await SeedSongAsync(databasePath, "Zweiter Song");
    await queue.AddAsync(EventRepository.DefaultEventId, first, "Anna", default);
    await queue.AddAsync(EventRepository.DefaultEventId, second, "Ben", default);

    var started = await queue.StartAsync(default);
    Assert(started.IsRunning && !started.IsPaused && started.Current?.Song.Id == first.Id, "Start wählt den ersten Titel.");

    var paused = await queue.PauseAsync(default);
    Assert(paused.IsPaused, "Pause setzt den Pausenzustand.");
    var resumed = await queue.ResumeAsync(default);
    Assert(resumed.IsRunning && !resumed.IsPaused, "Resume setzt die Wiedergabe fort.");

    var position = TimeSpan.FromSeconds(17.5);
    var accepted = await queue.UpdatePositionAsync(new(started.Current!.Id, position), default);
    Assert(accepted?.Position == position, "Position des aktuellen Eintrags wird angenommen.");
    var rejected = await queue.UpdatePositionAsync(new(Guid.NewGuid(), TimeSpan.FromSeconds(3)), default);
    Assert(rejected is null, "Position eines fremden Eintrags wird abgewiesen.");

    var next = await queue.NextAsync(default);
    Assert(next.Current?.Song.Id == second.Id && next.Position == TimeSpan.Zero, "Next startet den zweiten Titel bei Position null.");
    var previous = await queue.PreviousAsync(default);
    Assert(previous.Current?.Song.Id == first.Id && previous.Position == TimeSpan.Zero, "Previous kehrt zum ersten Titel zurück.");
    var completed = await queue.CompleteAsync(previous.Current!.Id, default);
    var duplicateCompletion = await queue.CompleteAsync(previous.Current.Id, default);
    Assert(completed.Current?.Song.Id == second.Id && duplicateCompletion.Current?.Song.Id == second.Id,
        "Ein doppeltes Titelende überspringt keinen weiteren Song.");

    var controller = new PlaybackControllerService();
    var appOne = Guid.NewGuid();
    var appTwo = Guid.NewGuid();
    Assert(controller.Claim(new(appOne, "App Eins")).OwnsControl, "Die erste App übernimmt die Bühnensteuerung.");
    Assert(!controller.Claim(new(appTwo, "App Zwei")).OwnsControl, "Eine zweite App wird während der aktiven Lease abgewiesen.");
    Assert(controller.Claim(new(appTwo, "Unity Stage", Force: true)).OwnsControl,
        "Eine bewusste Bedienung direkt auf der Stage übernimmt die Steuerung sofort.");
    Assert(!controller.Claim(new(appOne, "App Eins")).OwnsControl,
        "Normale Clients können die von der Stage übernommene Lease nicht verdrängen.");
    Assert(controller.Claim(new(appTwo, "Unity Stage")).OwnsControl, "Die aktive App kann ihre Lease erneuern.");
    controller.Release(appTwo);
    Assert(controller.Claim(new(appOne, "App Eins")).OwnsControl, "Nach der Freigabe kann eine andere App übernehmen.");

    var libraryPath = Path.Combine(Path.GetTempPath(), $"neon-stage-library-{Guid.NewGuid():N}");
    var settings = new ServerSettingsService(options);
    await settings.UpdateAsync(libraryPath, default);
    var restartedOptions = Options.Create(new KaraokeOptions { DatabasePath = databasePath, LibraryPath = "./music" });
    var restartedSettings = new ServerSettingsService(restartedOptions);
    await restartedSettings.InitializeAsync(default);
    Assert(restartedSettings.Get().LibraryPath == Path.GetFullPath(libraryPath), "Der Bibliothekspfad bleibt nach einem Serverneustart erhalten.");
    Directory.Delete(libraryPath);

    var qobuzSettings = new QobuzPluginSettingsService(options, Options.Create(new QobuzOptions()));
    var savedQobuz = await qobuzSettings.UpdateAsync(new(false, "partner-app", "secret-value", "user-token",
        QobuzDownloadQuality.FlacCd), default);
    Assert(savedQobuz.Configured && savedQobuz.HasAppSecret && savedQobuz.HasUserAuthToken &&
           !savedQobuz.Enabled,
        "Qobuz-Credentials werden nur als Vorhanden-Flags an den Editor zurückgegeben.");
    var restartedQobuz = new QobuzPluginSettingsService(restartedOptions, Options.Create(new QobuzOptions()));
    var workerQobuz = await restartedQobuz.GetWorkerSettingsAsync(default);
    Assert(workerQobuz.AppId == "partner-app" && workerQobuz.AppSecret == "secret-value" &&
           workerQobuz.UserAuthToken == "user-token" && workerQobuz.FormatId == 6,
        "Die serverseitige Qobuz-Konfiguration bleibt nach einem Neustart erhalten.");
    using (var qobuzTrackJson = JsonDocument.Parse("""
    {
      "id": 123456,
      "title": "Example Song",
      "duration": 187,
      "preview_url": "https://stream.qobuz.example/preview.mp3",
      "performer": { "name": "Example Artist" },
      "album": {
        "title": "Example Album",
        "image": { "large": "https://static.qobuz.example/cover.jpg" },
        "maximum_bit_depth": 24,
        "maximum_sampling_rate": 96
      },
      "price": 1.49,
      "currency": "EUR"
    }
    """))
    {
        var mappedQobuz = QobuzCatalogService.Map(qobuzTrackJson.RootElement);
        Assert(mappedQobuz is { Source: AudioCatalogSource.Qobuz, QobuzId: "123456", Price: 1.49m,
                   Currency: "EUR", AudioQuality: "FLAC 24 bit / 96 kHz",
                   DownloadSource: AudioDownloadSource.Qobuz, HasPreview: true } &&
               mappedQobuz.SourceUrl == "https://open.qobuz.com/track/123456" &&
               mappedQobuz.DurationLabel == "3:07",
            "Qobuz-Suchergebnisse behalten Katalog, Downloadquelle, Dauer, Vorschau, Preis, Qualität und ID.");
    }

    Assert(FolderImportService.IsSupportedAudioFile("Demo.MP3") &&
           FolderImportService.IsSupportedAudioFile("Demo.FlAc") &&
           !FolderImportService.IsSupportedAudioFile("Demo.wav"),
        "Der Audio-Ordnerimport akzeptiert MP3 und FLAC unabhängig von der Schreibweise.");
    var synchronizedLyricsPath = Path.Combine(Path.GetTempPath(), $"neon-stage-synced-{Guid.NewGuid():N}.lrc");
    var plainLyricsPath = Path.Combine(Path.GetTempPath(), $"neon-stage-plain-{Guid.NewGuid():N}.lrc");
    var markerLyricsPath = Path.Combine(Path.GetTempPath(), $"neon-stage-marker-{Guid.NewGuid():N}.lrc");
    try
    {
        await File.WriteAllTextAsync(synchronizedLyricsPath,
            "[ar:Example]\n[00:12.340]A synchronized lyric line\n");
        await File.WriteAllTextAsync(plainLyricsPath,
            "A plain lyric line\nAnother line without timestamps\n");
        await File.WriteAllTextAsync(markerLyricsPath,
            "[00:12.340]<00:12.340,00:13.000>[Chorus]\n");
        Assert(LibraryRepository.HasSynchronizedLyrics(synchronizedLyricsPath),
            "Eine echte LRC-Zeitmarke wird als synchronisierte Lyrics erkannt.");
        Assert(!LibraryRepository.HasSynchronizedLyrics(plainLyricsPath),
            "Reiner Lyrics-Text wird nicht fälschlich als synchronisiert markiert.");
        Assert(!LibraryRepository.HasSynchronizedLyrics(markerLyricsPath),
            "Ein zeitgestempelter Strukturmarker gilt nicht als synchronisierter Liedtext.");
    }
    finally
    {
        File.Delete(synchronizedLyricsPath);
        File.Delete(plainLyricsPath);
        File.Delete(markerLyricsPath);
    }
    var uniqueAudioFolder = Path.Combine(Path.GetTempPath(), $"neon-stage-audio-name-{Guid.NewGuid():N}");
    Directory.CreateDirectory(uniqueAudioFolder);
    try
    {
        await File.WriteAllBytesAsync(Path.Combine(uniqueAudioFolder, "Demo.flac"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(uniqueAudioFolder, "Track.MP3"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(uniqueAudioFolder, "Ignored.wav"), [0]);
        var nestedAudioFolder = Path.Combine(uniqueAudioFolder, "nested");
        Directory.CreateDirectory(nestedAudioFolder);
        await File.WriteAllBytesAsync(Path.Combine(nestedAudioFolder, "Nested.FLAC"), [0]);
        Assert(Path.GetFileName(FolderImportService.UniquePath(uniqueAudioFolder, "Demo.flac")) == "Demo (2).flac" &&
               FolderImportService.NormalizeAudioExtension("Demo.FLAC") == ".flac",
            "Kollisionsnamen und normalisierte Dateiendungen erhalten das FLAC-Format.");
        Assert(FolderImportService.DiscoverAudioFiles(uniqueAudioFolder, recursive: false).Length == 2 &&
               FolderImportService.DiscoverAudioFiles(uniqueAudioFolder, recursive: true).Length == 3,
            "Die Ordnersuche findet MP3 und FLAC und respektiert die rekursive Einstellung.");
        var lyricsPath = Path.Combine(uniqueAudioFolder, "Demo.lrc");
        Assert(FolderImportService.SelectPipelineRoute(lyricsPath) == FolderImportPipelineRoute.FullTranscript,
            "Ohne lokale oder LRCLIB-Lyrics wählt der Ordnerimport die Volltranskript-Pipeline.");
        await File.WriteAllTextAsync(lyricsPath, "   \n");
        Assert(FolderImportService.SelectPipelineRoute(lyricsPath) == FolderImportPipelineRoute.FullTranscript,
            "Eine leere LRC-Datei verhindert den Volltranskript-Fallback nicht.");
        await File.WriteAllTextAsync(lyricsPath, "[00:01.00]Example lyrics");
        Assert(FolderImportService.SelectPipelineRoute(lyricsPath) == FolderImportPipelineRoute.Variant12,
            "Mit vorhandenen Lyrics wählt der Ordnerimport Variante 1.2.");
    }
    finally { Directory.Delete(uniqueAudioFolder, recursive: true); }

    var lyricsVersions = new LyricsVersionRepository(options);
    var versionSongId = Guid.NewGuid();
    var editorJson = JsonSerializer.Serialize(new { schemaVersion = 1, songId = versionSongId, lines = Array.Empty<object>() });
    var alignmentReportJson = """{"quality":{"score":91,"publishable":true}}""";
    var version = await lyricsVersions.CreateAsync(versionSongId,
        new(editorJson, AlignmentReportJson: alignmentReportJson), default);
    Assert(version.Revision == 1 && version.Status == LyricsVersionStatus.InReview &&
           version.AlignmentReportJson == alignmentReportJson,
        "Ein Editorentwurf startet versioniert im Prüfstatus und bindet seinen Alignment-Bericht.");
    var staleUpdate = await lyricsVersions.UpdateAsync(versionSongId, version.Id,
        new(0, editorJson), default);
    Assert(staleUpdate is null, "Eine veraltete Revision überschreibt keinen Editorentwurf.");
    var changedEditorJson = JsonSerializer.Serialize(new
        { schemaVersion = 1, songId = versionSongId, marker = "zweiter Stand", lines = Array.Empty<object>() });
    var savedVersion = await lyricsVersions.UpdateAsync(versionSongId, version.Id,
        new(version.Revision, changedEditorJson), default);
    Assert(savedVersion is { Revision: 2 } && savedVersion.Id != version.Id,
        "Jedes Speichern legt einen eigenständigen Lyrics-Stand mit neuer Revision an.");
    Assert(savedVersion!.AlignmentReportJson == alignmentReportJson &&
           (await lyricsVersions.GetAllAsync(versionSongId, default)).Single(item => item.Id == savedVersion.Id).HasAlignmentReport,
        "Ein abgeleiteter Editor-Stand behält die Provenienz seines technischen Alignment-Berichts.");
    var archivedVersion = await lyricsVersions.GetAsync(versionSongId, version.Id, default);
    Assert(archivedVersion?.Status == LyricsVersionStatus.Superseded && archivedVersion.DocumentJson == editorJson,
        "Der vorherige Lyrics-Inhalt bleibt unverändert im Versionsarchiv erhalten.");
    var comparisonJson = JsonSerializer.Serialize(new
        { schemaVersion = 1, songId = versionSongId, marker = "Alignment-Vergleich", lines = Array.Empty<object>() });
    var comparisonVersion = await lyricsVersions.CreateAsync(versionSongId,
        new(comparisonJson, Status: LyricsVersionStatus.Generated, PreserveExistingDrafts: true), default);
    var stillCurrentDraft = await lyricsVersions.GetLatestDraftAsync(versionSongId, default);
    Assert(comparisonVersion.Status == LyricsVersionStatus.Generated &&
           stillCurrentDraft?.Id == savedVersion!.Id && stillCurrentDraft.DocumentJson == changedEditorJson,
        "Eine erzeugte Alignment-Vergleichsversion ersetzt den aktuellen Editor-Stand nicht.");
    var reviewed = await lyricsVersions.ChangeStatusAsync(versionSongId, savedVersion!.Id, savedVersion.Revision,
        LyricsVersionStatus.Reviewed, default);
    var approved = await lyricsVersions.ChangeStatusAsync(versionSongId, savedVersion.Id, reviewed!.Revision,
        LyricsVersionStatus.Approved, default);
    var published = await lyricsVersions.ChangeStatusAsync(versionSongId, savedVersion.Id, approved!.Revision,
        LyricsVersionStatus.Published, default);
    Assert(published?.Status == LyricsVersionStatus.Published,
        "Lyrics durchlaufen Review, Freigabe und Veröffentlichung mit Revisionen.");
    Assert(await lyricsVersions.UpdateAsync(versionSongId, savedVersion.Id,
        new(published!.Revision, changedEditorJson), default) is null,
        "Eine veröffentlichte Lyrics-Version ist unveränderlich.");
    Assert(await lyricsVersions.DeleteAsync(versionSongId, savedVersion.Id, default) == LyricsVersionDeleteResult.Published,
        "Die veröffentlichte Stage-Version ist vor dem Löschen geschützt.");
    Assert(await lyricsVersions.DeleteAsync(versionSongId, version.Id, default) == LyricsVersionDeleteResult.Deleted,
        "Ein archivierter Lyrics-Stand kann gezielt gelöscht werden.");
    var overlappingJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        songId = versionSongId,
        lines = new[]
        {
            new { start = "00:00:01", end = "00:00:03" },
            new { start = "00:00:02.900", end = "00:00:04" }
        }
    });
    try
    {
        await lyricsVersions.CreateAsync(versionSongId, new(overlappingJson), default);
        throw new InvalidOperationException("Test fehlgeschlagen: Überlappende Zeilen wurden gespeichert.");
    }
    catch (ArgumentException exception)
    {
        Assert(exception.Message.Contains("nicht überschneiden", StringComparison.Ordinal),
            "Auch der Server lehnt überlappende Editor-Zeilen verbindlich ab.");
    }
    var duetJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        songId = versionSongId,
        lines = new[]
        {
            new { start = "00:00:01", end = "00:00:03", voiceLane = 0 },
            new { start = "00:00:02.900", end = "00:00:04", voiceLane = 1 }
        }
    });
    var duetVersion = await lyricsVersions.CreateAsync(versionSongId, new(duetJson), default);
    Assert(duetVersion.Revision > 0,
        "Der Server bewahrt zeitgleichen Gesang in zwei unabhängigen Gesangsspuren.");
    var explicitlyAccepted = await lyricsVersions.CreateAsync(versionSongId,
        new(overlappingJson, AllowTimingConflicts: true), default);
    var explicitlyPublished = await lyricsVersions.ChangeStatusAsync(versionSongId, explicitlyAccepted.Id,
        explicitlyAccepted.Revision, LyricsVersionStatus.Published, default, allowTimingConflicts: true);
    Assert(explicitlyPublished?.Status == LyricsVersionStatus.Published,
        "Eine ausdrückliche manuelle Freigabe darf bekannte Timing-Warnungen bewusst übernehmen.");

    var nestedOverlapJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        songId = versionSongId,
        lines = new object[]
        {
            new { start = "00:00:01", end = "00:00:03", children = new[] {
                new { start = "00:00:02", end = "00:00:03.500", children = Array.Empty<object>() } } },
            new { start = "00:00:03", end = "00:00:04", children = Array.Empty<object>() }
        }
    });
    try
    {
        await lyricsVersions.CreateAsync(versionSongId, new(nestedOverlapJson), default);
        throw new InvalidOperationException("Test fehlgeschlagen: Überlappender Wortinhalt wurde gespeichert.");
    }
    catch (ArgumentException exception)
    {
        Assert(exception.Message.Contains("Wörter und Silben", StringComparison.Ordinal),
            "Der Server prüft die effektiven Grenzen einschließlich verschachtelter Wörter.");
    }

    var wishlist = new WishlistRepository(options, events, new ChangeFeedService());
    var wishedTrack = new SpotifyTrackDto("spotify-test", "spotify:track:spotify-test", "Wunschtitel", "Testband", "Testalbum", null, 180000, true, "https://open.spotify.com/track/spotify-test");
    await wishlist.AddAsync(EventRepository.DefaultEventId, new(wishedTrack, "Carla"), default);
    await wishlist.AddAsync(EventRepository.DefaultEventId, new(wishedTrack, "David"), default);
    var savedWishes = await wishlist.GetAsync(EventRepository.DefaultEventId, default);
    Assert(savedWishes.Count == 1 && savedWishes[0].RequestedBy == "Carla", "Doppelte Spotify-Wünsche werden nur einmal gespeichert.");
    Assert(savedWishes[0].Track.SpotifyUrl == wishedTrack.SpotifyUrl, "Der öffentliche Spotify-Link wird mit dem Wunsch gespeichert.");

    adoptionLibraryPath = Path.Combine(Path.GetTempPath(), $"neon-stage-adoption-{Guid.NewGuid():N}");
    Directory.CreateDirectory(adoptionLibraryPath);
    var candidateAudio = Path.Combine(adoptionLibraryPath, "Audio Candidate.wav");
    WriteTestWave(candidateAudio);
    Assert(await wishlist.SetAudioCandidateAsync(EventRepository.DefaultEventId, savedWishes[0].Id,
            candidateAudio, "Audio gefunden · Lyrics-Alignment fehlgeschlagen", default),
        "Ein fehlgeschlagenes Alignment merkt sich den vorhandenen Audiofund beim Wunsch.");
    var candidateWish = (await wishlist.GetAsync(EventRepository.DefaultEventId, default)).Single();
    Assert(candidateWish.HasAudioCandidate && candidateWish.Status.Contains("fehlgeschlagen", StringComparison.Ordinal),
        "Der Client erhält Übernehmbarkeit und Fehlerstatus, aber keinen lokalen Dateipfad.");
    var adoptionOptions = Options.Create(new KaraokeOptions
        { DatabasePath = adoptionDatabasePath = Path.Combine(Path.GetTempPath(), $"neon-stage-adoption-{Guid.NewGuid():N}.db"), LibraryPath = adoptionLibraryPath });
    var adoptionLibrary = new LibraryRepository(adoptionOptions, NullLogger<LibraryRepository>.Instance,
        new TestHubContext(), new ChangeFeedService());
    var adopted = await adoptionLibrary.AdoptWithoutLyricsAsync(candidateAudio, wishedTrack,
        savedWishes[0].Id, default);
    Assert(adopted.LibraryCategory == SongLibraryCategory.WithoutLyrics && !adopted.HasLyrics &&
           adopted.ReviewStatus == SongReviewStatus.InReview,
        "Ein bestätigter Audiofund wird ausschließlich als unveröffentlichtes Projekt ‚Ohne Lyrics‘ indexiert.");
    Assert(await adoptionLibrary.ContainsSongAsync("WUNSCHTITEL!", "Testband", default) &&
           !await adoptionLibrary.ContainsSongAsync("Anderer Titel", "Testband", default),
        "Die gemeinsame Importprüfung erkennt normalisierte Titel/Interpreten auch im Review und verhindert Dubletten.");
    Assert(await adoptionLibrary.SetReviewStatusAsync(adopted.Id, SongReviewStatus.Approved, default) is null,
        "Ein Song ohne Lyrics und Stems kann nicht versehentlich für die Stage freigegeben werden.");
    Assert(await adoptionLibrary.WriteImportedLyricsSourceAsync(adopted.Id,
            "[00:01.000]<00:01.000,00:02.000>Demo", default),
        "Nachträglich importierte Lyrics werden als Quelle für das nächste Alignment gespeichert.");
    await adoptionLibrary.TryReindexAsync(default);
    var stillIncomplete = await adoptionLibrary.GetAsync(adopted.Id, default);
    Assert(stillIncomplete?.LibraryCategory == SongLibraryCategory.WithoutLyrics && !stillIncomplete.HasLyrics,
        "Lyrics allein machen den Song ohne erzeugte Instrumental- und Vocalspuren noch nicht stagefähig.");
    var candidateBasePath = Path.Combine(Path.GetDirectoryName(candidateAudio)!,
        Path.GetFileNameWithoutExtension(candidateAudio));
    await File.WriteAllBytesAsync(candidateBasePath + ".instrumental.ogg", [1]);
    await File.WriteAllBytesAsync(candidateBasePath + ".vocals.ogg", [1]);
    await adoptionLibrary.TryReindexAsync(default);
    var completedAdoption = await adoptionLibrary.GetAsync(adopted.Id, default);
    Assert(completedAdoption?.LibraryCategory == SongLibraryCategory.KaraokeReady &&
           completedAdoption.HasLyrics && completedAdoption.HasInstrumental && completedAdoption.HasVocals,
        "Nach Lyrics-Import und erfolgreicher Stem-Erzeugung wechselt das Projekt automatisch in die reguläre Review-Kategorie.");
    var fallbackBaseLyrics = await adoptionLibrary.ReadBaseLyricsSourceAsync(adopted.Id, default);
    Assert(fallbackBaseLyrics is { IsPreAlignmentSource: false } &&
           fallbackBaseLyrics.Lyrics.Contains("Demo", StringComparison.Ordinal),
        "Ohne Pre-Align-Datei zeigt der Base-Lyrics-Viewer die tatsächlich verwendbare aktuelle LRC.");
    Assert(await adoptionLibrary.WriteBaseLyricsSourceAsync(adopted.Id,
            "[00:01.000]Verse 1\n[00:02.000]Bearbeitete Base-Lyrics", default),
        "Editierte Base-Lyrics werden getrennt vom aktuellen Timeline-Alignment gespeichert.");
    var editedBaseLyrics = await adoptionLibrary.ReadBaseLyricsSourceAsync(adopted.Id, default);
    Assert(editedBaseLyrics is { IsPreAlignmentSource: true } &&
           editedBaseLyrics.Lyrics.Contains("Bearbeitete Base-Lyrics", StringComparison.Ordinal),
        "Der Base-Lyrics-Viewer liest nach dem Speichern exakt die neue Pre-Align-Quelle.");
    Assert((await adoptionLibrary.SetReviewStatusAsync(adopted.Id, SongReviewStatus.Approved, default))?.ReviewStatus ==
           SongReviewStatus.Approved &&
           (await adoptionLibrary.SetReviewStatusAsync(adopted.Id, SongReviewStatus.InReview, default))?.ReviewStatus ==
           SongReviewStatus.InReview,
        "Ein freigegebener Song kann wieder aus der Stage entfernt und in Review gesetzt werden.");
    Assert(await wishlist.RemoveAsync(EventRepository.DefaultEventId, savedWishes[0].Id, default) &&
           (await wishlist.GetAsync(EventRepository.DefaultEventId, default)).Count == 0,
        "Ein nicht verarbeiteter oder fehlgeschlagener Wunsch kann endgültig entfernt werden.");
    await adoptionLibrary.DeleteSongAsync(adopted.Id, default);

    var party = await events.CreateAsync(new("Geburtstag", DateTimeOffset.UtcNow.AddDays(7)), default);
    await wishlist.AddAsync(party.Id, new(wishedTrack, "Eva"), default);
    var partyWishes = await wishlist.GetAsync(party.Id, default);
    Assert(partyWishes.Count == 1 && partyWishes[0].RequestedBy == "Eva",
        "Derselbe Spotify-Titel kann unabhängig in verschiedenen Event-Wunschlisten stehen.");
    await queue.AddAsync(party.Id, first, "Eva", default);
    var partyQueue = await queue.GetStateAsync(party.Id, default);
    Assert(partyQueue.Queue.Count == 1 && partyQueue.Queue[0].RequestedBy == "Eva",
        "Vorab befüllte Event-Wartelisten bleiben voneinander getrennt.");
    await events.ActivateAsync(party.Id, default);
    var activeParty = await events.GetActiveAsync(default);
    Assert(activeParty?.Id == party.Id && (await queue.GetActiveStateAsync(default)).Queue.Count == 1,
        "Beim Aktivieren wechselt die Bühne auf die vorbereitete Event-Warteliste.");

    var enhancedLyrics = LrcParser.Parse(Guid.NewGuid(),
        ["[00:12.57]<00:12.12>Das <00:12.36>Leben <00:12.52>bockt <00:12.76>nicht"], TimeSpan.FromSeconds(20));
    Assert(enhancedLyrics.Lines[0].Start == TimeSpan.FromSeconds(12.12), "Enhanced LRC verwendet den frühesten Wortzeitpunkt als Zeilenstart.");
    Assert(enhancedLyrics.Lines[0].Words is { Count: 4 } words && words[1].Text == "Leben" && words[1].Start == TimeSpan.FromSeconds(12.36),
        "Wortgenaue LRC-Zeitpunkte bleiben vollständig erhalten.");
    var lyricsWithMarker = LrcParser.Parse(Guid.NewGuid(),
        ["[00:10.00]Echter Text", "[00:14.00]<00:14.00,00:15.00>Chorus", "[00:16.00]Nächster Text"],
        TimeSpan.FromSeconds(20));
    Assert(lyricsWithMarker.Lines.All(line => line.Text != "Chorus") &&
           lyricsWithMarker.Lines.All(line => line.Words?.All(word => word.Text != "Chorus") != false),
        "Strukturmarker werden nicht als singbare LRC-Zeilen, Wörter oder Silben übernommen.");
    var multiVoiceLyrics = LrcParser.Parse(Guid.NewGuid(),
        ["[00:10.000]<00:10.000,00:13.000>Lead",
         "[neon-voice:1:WndlaXRlIFN0aW1tZQ]",
         "[00:11.000]<00:11.000,00:14.000>Woho"], TimeSpan.FromSeconds(20));
    Assert(multiVoiceLyrics.Lines.Count == 2 &&
           multiVoiceLyrics.Lines[0].VoiceLane == 0 &&
           multiVoiceLyrics.Lines[1].VoiceLane == 1 &&
           multiVoiceLyrics.Lines[1].VoiceLabel == "Zweite Stimme" &&
           multiVoiceLyrics.Lines[0].End == TimeSpan.FromSeconds(13) &&
           multiVoiceLyrics.Lines[1].End == TimeSpan.FromSeconds(14),
        "Enhanced LRC bewahrt parallele Stimmen als getrennte, überlappende Spuren.");

    // The Unity Stage uses UnityEngine.JsonUtility and therefore depends on these
    // exact camelCase field names and nested arrays. Editor/versioning endpoints
    // must remain additive and may not alter the live lyrics contract.
    var stageJson = JsonSerializer.Serialize(enhancedLyrics, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var stageDocument = JsonDocument.Parse(stageJson);
    var stageLine = stageDocument.RootElement.GetProperty("lines")[0];
    var stageWord = stageLine.GetProperty("words")[0];
    Assert(stageLine.TryGetProperty("start", out _) && stageLine.TryGetProperty("end", out _) &&
           stageLine.TryGetProperty("text", out _) && stageLine.TryGetProperty("index", out _),
        "Der Live-Lyrics-Vertrag der Unity-Stage behält alle Zeilenfelder.");
    Assert(stageWord.TryGetProperty("start", out _) && stageWord.TryGetProperty("end", out _) &&
           stageWord.TryGetProperty("text", out _) && stageWord.TryGetProperty("index", out _) &&
           stageWord.TryGetProperty("syllables", out _) && stageWord.TryGetProperty("syllableConfidence", out _),
        "Der Live-Lyrics-Vertrag der Unity-Stage behält Wort- und Silbenfelder.");

    var editedRuntimeJson = JsonSerializer.Serialize(new
    {
        lines = new[] { new
        {
            start = "00:00:01", end = "00:00:03", text = "Veralteter Zeilentext",
            children = new object[]
            {
                new { type = "Word", start = "00:00:01", end = "00:00:02", text = "Hallo",
                    confidence = .9, children = Array.Empty<object>() },
                new { type = "Word", start = "00:00:02", end = "00:00:03", text = "toys",
                    confidence = .9, children = Array.Empty<object>() }
            }
        } }
    });
    var mappedRuntime = EditorLyricsRuntimeMapper.Map(enhancedLyrics, editedRuntimeJson);
    Assert(mappedRuntime.Lines[0].Text == "Hallo toys" && mappedRuntime.Lines[0].Words?.Count == 2,
        "Die Stage bildet den sichtbaren Zeilentext aus eingefügten Editor-Wörtern.");

    var sourceMetadata = new AudioMetadata("Mein Song", "Meine Band", "Studioalbum", 200);
    var exactDuration = new LrclibTrack(1, "Mein Song", "Meine Band", "Anderes Album", 200.2, false, null, "[00:01]Text");
    var betterAlbumWrongDuration = new LrclibTrack(2, "Mein Song", "Meine Band", "Studioalbum", 204, false, null, "[00:01]Text");
    Assert(MatchScorer.SelectBest(sourceMetadata, [betterAlbumWrongDuration, exactDuration], 5)?.Item.Id == exactDuration.Id,
        "Bei kompatiblen Metadaten gewinnt zuerst die gleiche Songlänge.");
    var shorter = new LrclibTrack(3, "Mein Song", "Meine Band", "Studioalbum", 198, false, null, "[00:01]Text");
    var longer = new LrclibTrack(4, "Mein Song", "Meine Band", "Studioalbum", 203, false, null, "[00:01]Text");
    Assert(MatchScorer.SelectBest(sourceMetadata, [longer, shorter], 5)?.Item.Id == shorter.Id,
        "Die Dauersuche nähert sich symmetrisch über die kleinste absolute Abweichung an.");
    var earlyLyrics = exactDuration with { Id = 5, SyncedLyrics = "[00:04.51]Erste Zeile" };
    var lateLyrics = exactDuration with { Id = 6, SyncedLyrics = "[00:07.92]Erste Zeile" };
    var timingConflict = MatchScorer.SelectBest(sourceMetadata, [earlyLyrics, lateLyrics], 5);
    Assert(timingConflict is { Confident: false } && timingConflict.Reason.Contains("zeitlich verschiedene", StringComparison.Ordinal),
        "Gleich lange Songs mit verschiedenen Gesangseinsätzen werden nicht automatisch zugeordnet.");

    Console.WriteLine("Playback-Integrationstests erfolgreich.");
}
finally
{
    if (File.Exists(databasePath)) File.Delete(databasePath);
    var settingsPath = Path.Combine(Path.GetDirectoryName(databasePath)!, Path.GetFileNameWithoutExtension(databasePath) + ".server-settings.json");
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
    var qobuzPath = Path.Combine(Path.GetDirectoryName(databasePath)!,
        Path.GetFileNameWithoutExtension(databasePath) + ".qobuz-plugin.json");
    if (File.Exists(qobuzPath)) File.Delete(qobuzPath);
    if (adoptionLibraryPath is not null && Directory.Exists(adoptionLibraryPath))
        Directory.Delete(adoptionLibraryPath, recursive: true);
    if (adoptionDatabasePath is not null && File.Exists(adoptionDatabasePath)) File.Delete(adoptionDatabasePath);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Test fehlgeschlagen: " + message);
    Console.WriteLine("OK: " + message);
}

static async Task VerifyFirstSongStartsActiveStageAsync()
{
    var database = Path.Combine(Path.GetTempPath(), $"neon-stage-autostart-{Guid.NewGuid():N}.db");
    try
    {
        var options = Options.Create(new KaraokeOptions { DatabasePath = database });
        var events = new EventRepository(options);
        var queue = new QueueService(options, new TestHubContext(), events);
        await queue.InitializeAsync(default);
        var activeEvent = await events.CreateAsync(new("Autostart Party", DateTimeOffset.UtcNow), default);
        await events.ActivateAsync(activeEvent.Id, default);
        var first = await SeedSongAsync(database, "Autostart Song");
        var added = await queue.AddAsync(activeEvent.Id, first, "Gast", default);
        var state = await queue.GetActiveStateAsync(default);
        Assert(added.Status == QueueEntryStatus.Playing && state.IsRunning && !state.IsPaused &&
               state.Current?.Id == added.Id && state.Queue.Count == 0,
            "Der erste Titel einer leeren aktiven Warteliste startet automatisch auf der Bühne.");

        var second = await SeedSongAsync(database, "Wartender Song");
        await queue.AddAsync(activeEvent.Id, second, "Gast", default);
        state = await queue.GetActiveStateAsync(default);
        Assert(state.Current?.Id == added.Id && state.Queue.Count == 1 && state.Queue[0].Song.Id == second.Id,
            "Weitere Titel warten normal, während der automatisch gestartete Titel läuft.");
    }
    finally
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);
    }
}

static async Task VerifyUsdbHttpAndMatchingAsync()
{
    var searchHandler = new QueueHttpHandler(
        JsonResponse("""
        {"secties":{"1":{"label":"Songs","content":[
          {"id":"423","label":"The Nights - Avicii","note":"2014","href":"//usdb.eu/Avicii/TheNights"}
        ]}}}
        """),
        HtmlResponse("<a href=\"//usdb.eu/Avicii/TheNights/473\">The Nights</a>"),
        TextResponse("""
        ({"title":{"label":"The Nights"},"artist":{"label":"Avicii"},"gap":1000,
          "lyrics":{"0":{"txt":{"1":[
            {"start":0,"stop":500,"text":"The","golden":false,"freestyle":false},
            {"start":500,"stop":1500,"text":" Nights","golden":false,"freestyle":false}
          ]},"start":0,"stop":1500}}})
        """));
    var downloadHandler = new QueueHttpHandler(
        HtmlResponse("<a href=\"/download/473\">Download</a>"),
        HtmlResponse("<div id=\"countdown\">Please wait</div>"),
        JsonResponse("[]"),
        new HttpResponseMessage(HttpStatusCode.InternalServerError),
        HtmlResponse("<div id=\"countdown\">Still waiting</div>"),
        new HttpResponseMessage(HttpStatusCode.InternalServerError),
        HtmlResponse("<div id=\"countdown\">Still waiting</div>"));
    using var http = new HttpClient(searchHandler);
    var options = new UsdbOptions
    {
        BaseUrl = "https://usdb.eu",
        DownloadWaitSeconds = 0,
        DownloadRetryDelaySeconds = 0,
        MaximumDurationDifferenceSeconds = 10,
        CachePath = Path.Combine(Path.GetTempPath(), $"neon-usdb-{Guid.NewGuid():N}")
    };
    var client = new UsdbClient(http, options, options.CachePath, TimeProvider.System,
        NullLogger<UsdbClient>.Instance, _ => downloadHandler);
    using (var emptySearch = JsonDocument.Parse("[]"))
        Assert(client.ParseSearch(emptySearch.RootElement).Count == 0,
            "Eine leere USDB-Suche im realen Array-Format wird als null Treffer statt als Fehler behandelt.");
    var search = await client.SearchAsync("The Nights", "Avicii", default);
    var versions = await client.ResolveVersionsAsync(search.Single(), default);
    var downloaded = await client.DownloadAsync(versions.Single(), default);
    Assert(downloaded.Parsed.SyllableCount == 2 && versions[0].VersionId == 473,
        "USDB-JSON, Versionslink, Session-Countdown und UltraStar-TXT werden ohne Browser verarbeitet.");
    Assert(downloadHandler.Requests.Count == 7 &&
           downloadHandler.Requests[0].Method == HttpMethod.Get &&
           downloadHandler.Requests[0].Uri.AbsolutePath == "/Avicii/TheNights/473" &&
           downloadHandler.Requests[1].Method == HttpMethod.Get &&
           downloadHandler.Requests[1].Uri.AbsolutePath == "/download/473" &&
           downloadHandler.Requests[1].Referrer?.AbsolutePath == "/Avicii/TheNights/473" &&
           downloadHandler.Requests[2].Method == HttpMethod.Post &&
           downloadHandler.Requests[2].Uri.AbsolutePath == "/download" &&
           downloadHandler.Requests[2].Referrer?.AbsolutePath == "/download/473" &&
           downloadHandler.Requests[2].IsAjax &&
           downloadHandler.Requests[3].Method == HttpMethod.Get &&
           downloadHandler.Requests[3].Uri.AbsolutePath == "/download/473" &&
           downloadHandler.Requests[3].Referrer?.AbsolutePath == "/download/473" &&
           downloadHandler.Requests[4].Method == HttpMethod.Get &&
           downloadHandler.Requests[4].Uri.AbsolutePath == "/download/473" &&
           downloadHandler.Requests[6].Method == HttpMethod.Get &&
           downloadHandler.Requests[6].Uri.AbsolutePath == "/download/473",
        "Der USDB-Download bildet die Browsersequenz ab und fällt nach defekten TXT-Reloads auf offizielle Player-Daten zurück.");

    var matcher = new UsdbSongMatcher(Options.Create(options));
    var compatible = matcher.Assess(new("song.mp3", "The Nights", "Avicii", null,
        TimeSpan.FromSeconds(10), 2014), versions[0], downloaded.Parsed);
    var wrongDuration = matcher.Assess(new("song.mp3", "The Nights", "Avicii", null,
        TimeSpan.FromSeconds(40), 2014), versions[0], downloaded.Parsed);
    Assert(compatible.Accepted && !wrongDuration.Accepted,
        "USDB-Matching akzeptiert die passende Aufnahme und verwirft dieselben Metadaten mit falscher Dauer.");
}

static async Task VerifyUsdbFailureFallsBackCleanlyAsync()
{
    var target = Path.Combine(Path.GetTempPath(), $"neon-usdb-fallback-{Guid.NewGuid():N}.lrc");
    var options = Options.Create(new UsdbOptions());
    var source = new UsdbLyricsSourceService(new ThrowingUsdbClient(),
        new UsdbSongMatcher(options), options, NullLogger<UsdbLyricsSourceService>.Instance);
    var fallbackCalled = false;
    var result = await source.ResolveWithFallbackAsync(new("song.mp3", "Missing", "Artist", null,
        TimeSpan.FromMinutes(3)), target, "LRCLIB", async cancellationToken =>
        {
            fallbackCalled = true;
            await File.WriteAllTextAsync(target, "[00:01.000]Fallback lyrics\n", cancellationToken);
            return true;
        }, default);
    Assert(result is { Success: true, Source: "LRCLIB" } && fallbackCalled && File.Exists(target),
        "Ein USDB-HTTP-Fehler hinterlässt keine Teil-LRC und der LRCLIB-Fallback kann übernehmen.");
    File.Delete(target);
}

static async Task VerifyLegacyUsdbChartSkipsAiAlignmentAsync()
{
    var target = Path.Combine(Path.GetTempPath(), $"neon-usdb-direct-{Guid.NewGuid():N}.lrc");
    try
    {
        var options = Options.Create(new UsdbOptions { MaximumDurationDifferenceSeconds = 10 });
        var source = new UsdbLyricsSourceService(new PickerUsdbClient(),
            new UsdbSongMatcher(options), options, NullLogger<UsdbLyricsSourceService>.Instance);
        var result = await source.TryResolveAsync(new("missing-test-audio.mp3", "The Nights", "Avicii",
            null, TimeSpan.FromSeconds(10), 2014), target, default);
        Assert(result.Success && result.TrustedDirectCandidate && File.Exists(target),
            "Ein sicher gematchtes älteres UltraStar-Chart ohne #END überspringt ebenfalls das AI-Lyrics-Alignment.");
    }
    finally
    {
        if (File.Exists(target)) File.Delete(target);
    }
}

static async Task VerifyAnimuxUsdbPreferenceAndFallbackAsync()
{
    var cache = Path.Combine(Path.GetTempPath(), $"neon-usdb-animux-{Guid.NewGuid():N}");
    try
    {
        const string lyrics = """
            #TITLE:The Nights
            #ARTIST:Avicii
            #BPM:120
            #GAP:1000
            : 0 16 60 The
            : 16 56 62  Nights
            - 72
            E
            """;
        var animuxHandler = new QueueHttpHandler(
            HtmlResponse("<html>landing</html>"),
            // The post-login intermediate page can still contain this generic
            // prompt even though the session cookie is already authenticated.
            HtmlResponse("<span class='gen'>Welcome, Please login</span>"),
            HtmlResponse("<span class='gen'>Welcome <b>tester</b></span>"),
            HtmlResponse("""
                <tr class="list_tr1" data-songid="26152" data-lastchange="1">
                <td></td><td></td><td>Avicii</td><td><a href="?link=detail&amp;id=26152">The Nights</a></td>
                <td>Pop</td><td>2014</td><td></td><td>Yes</td><td>English</td></tr>
                """),
            HtmlResponse($"<textarea>{WebUtility.HtmlEncode(lyrics)}</textarea>"));
        var options = new UsdbOptions
        {
            MaximumCandidates = 5,
            CachePath = cache,
            Animux = new()
            {
                Enabled = true,
                BaseUrl = "https://usdb.animux.de",
                Username = "tester",
                Password = "not-a-real-secret"
            }
        };
        var animux = new AnimuxUsdbClient(new HttpClient(animuxHandler), options, cache,
            NullLogger<AnimuxUsdbClient>.Instance);
        var found = await animux.SearchAsync("The Nights", "Avicii", default);
        var resolved = await animux.ResolveVersionsAsync(found.Single(), default);
        var downloaded = await animux.DownloadAsync(resolved.Single(), default);
        Assert(found.Single().Provider == UsdbProviders.Animux && resolved.Single().VersionId == 26152 &&
               downloaded.Parsed.WordCount == 2 && animuxHandler.Requests.Count == 5,
            "Animux-Login, Listensuche und authentifizierter TXT-Abruf verwenden denselben USDB-Vertrag.");

        var failingAnimuxHandler = new QueueHttpHandler(
            HtmlResponse("<html>landing</html>"),
            HtmlResponse("<span class='gen'>Welcome <b>tester</b></span>"),
            HtmlResponse("<span class='gen'>Welcome <b>tester</b></span>"),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var failingAnimux = new AnimuxUsdbClient(new HttpClient(failingAnimuxHandler), options, cache,
            NullLogger<AnimuxUsdbClient>.Instance);
        var euHandler = new QueueHttpHandler(JsonResponse("""
            {"secties":{"1":{"label":"Songs","content":[
              {"label":"The Nights - Avicii","note":"2014","href":"//usdb.eu/Avicii/TheNights"}
            ]}}}
            """));
        var eu = new UsdbClient(new HttpClient(euHandler), options, cache, TimeProvider.System,
            NullLogger<UsdbClient>.Instance, _ => new QueueHttpHandler());
        var preferred = new PreferredUsdbClient(failingAnimux, eu,
            NullLogger<PreferredUsdbClient>.Instance);
        var fallback = await preferred.SearchAsync("The Nights", "Avicii", default);
        Assert(fallback.Single().Provider == UsdbProviders.Eu && euHandler.Requests.Count == 1,
            "Ein Animux-Ausfall fällt automatisch und ohne Nutzerinteraktion auf usdb.eu zurück.");
    }
    finally
    {
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
    }
}

static async Task VerifyUsdbEditorPickerCreatesIsolatedVersionAsync()
{
    var database = Path.Combine(Path.GetTempPath(), $"neon-usdb-editor-{Guid.NewGuid():N}.db");
    try
    {
        var songId = Guid.NewGuid();
        var song = new SongDto(songId, "The Nights", "Avicii", "Stories", 10, true);
        var repository = new LyricsVersionRepository(Options.Create(new KaraokeOptions { DatabasePath = database }));
        var oldJson = JsonSerializer.Serialize(new
            { schemaVersion = 1, songId, lines = Array.Empty<object>() });
        var oldVersion = await repository.CreateAsync(songId, new(oldJson), default);
        var options = Options.Create(new UsdbOptions { MaximumDurationDifferenceSeconds = 10 });
        var client = new PickerUsdbClient();
        var service = new UsdbEditorLyricsService(client, new UsdbSongMatcher(options), repository,
            TimeProvider.System, NullLogger<UsdbEditorLyricsService>.Instance);

        var search = await service.SearchAsync(song, song.Title, default);
        Assert(search.Items.Count == 2 && search.Items.Count(item => item.IsRecommended) == 1 &&
               (await repository.GetAllAsync(songId, default)).Count == 1,
            "Die Editor-USDB-Suche markiert genau den besten Treffer, speichert vor der Auswahl aber nichts.");
        var selected = search.Items.Single(item => item.IsRecommended);
        var imported = await service.ImportAsync(song, selected.SelectionToken, default);
        var versions = await repository.GetAllAsync(songId, default);
        var document = JsonSerializer.Deserialize<LyricsEditorDocument>(imported!.Version.DocumentJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(imported.Version.Revision == 2 && versions.Count == 2 &&
               versions.Single(item => item.Id == oldVersion.Id).Status == LyricsVersionStatus.InReview &&
               document?.Segments.Any(item => item.Type == LyricSegmentType.Syllable) == true,
            "Auswählen speichert UltraStar-Wörter und -Silben als neue Revision, ohne den alten Arbeitsstand zu archivieren.");
        Assert(await service.ImportAsync(song, selected.SelectionToken, default) is null,
            "Ein serverseitiges USDB-Auswahltoken kann nur einmal und nur für seinen Song verwendet werden.");
    }
    finally
    {
        if (File.Exists(database)) File.Delete(database);
    }
}

static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json")
};

static HttpResponseMessage HtmlResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "text/html")
};

static HttpResponseMessage TextResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "text/plain")
};

static void VerifyUsdbRecordingTimingDiagnostics()
{
    const string ultraStar = """
        #TITLE:Timing Test
        #ARTIST:Test Artist
        #BPM:120
        #GAP:4000
        #END:100000
        : 0 4 60 Sing
        E
        """;
    var parsed = UltraStarLyricsImporter.Parse(ultraStar);
    var detected = UsdbRecordingTimingAnalyzer.ParseSilenceDetect("""
        [silencedetect] silence_start: 0
        [silencedetect] silence_end: 2.000 | silence_duration: 2.000
        [silencedetect] silence_start: 102.000
        """, TimeSpan.FromSeconds(103));
    var padded = UsdbRecordingTimingAnalyzer.Compare(detected, parsed);
    Assert(detected.LeadingSilence == TimeSpan.FromSeconds(2) &&
           detected.TrailingSilence == TimeSpan.FromSeconds(1),
        "Randstille wird unabhängig vom musikalischen UltraStar-GAP gemessen.");
    Assert(padded.Hypothesis == UsdbTimelineHypothesis.ImportedAudioHasExtraEdgeSilence &&
           padded.SuggestedGlobalOffset == TimeSpan.FromSeconds(2) && padded.DurationCompatible,
        "Wenn #END erst nach virtuellem Trimmen passt, wird nur die gemessene Startstille als Offset vorgeschlagen.");

    var matcher = new UsdbSongMatcher(Options.Create(new UsdbOptions
        { MaximumDurationDifferenceSeconds = 3 }));
    var version = new UsdbVersionCandidate(1, "Timing Test", "Test Artist", null,
        null, null, new Uri("https://usdb.example/song/1"));
    var paddedAssessment = matcher.Assess(new("song.flac", "Timing Test", "Test Artist", null,
        TimeSpan.FromSeconds(103)), version, parsed, detected);
    Assert(paddedAssessment.Accepted && paddedAssessment.DurationDifferenceSeconds == 0,
        "USDB-Matching vergleicht bei gemessener Randstille auch die virtuelle reine Aufnahmezeit.");

    var sameTimeline = UsdbRecordingTimingAnalyzer.Compare(
        new(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), true), parsed);
    Assert(sameTimeline.Hypothesis == UsdbTimelineHypothesis.OriginalTimeline &&
           sameTimeline.SuggestedGlobalOffset == TimeSpan.Zero,
        "Eine bereits passende Gesamtlänge erhält keinen künstlichen USDB-Offset.");
}

static async Task VerifyAlignmentSyllablesSkipDisplayBoundaryLinesAsync()
{
    var songId = Guid.NewGuid();
    var lyrics = new LyricsDto(songId,
    [
        new LyricsLineDto(TimeSpan.FromSeconds(1), "First", TimeSpan.FromSeconds(2), 0,
        [
            new LyricsWordDto(TimeSpan.FromSeconds(1), "First", TimeSpan.FromSeconds(1.5), 0)
        ]),
        // Timestamp-only Enhanced-LRC rows delimit display holds but have no
        // matching row in the aligner's singing-line report.
        new LyricsLineDto(TimeSpan.FromSeconds(2), string.Empty, TimeSpan.FromSeconds(3), 1, []),
        new LyricsLineDto(TimeSpan.FromSeconds(3), "Second word", TimeSpan.FromSeconds(4), 2,
        [
            new LyricsWordDto(TimeSpan.FromSeconds(3), "Second", TimeSpan.FromSeconds(3.4), 0),
            new LyricsWordDto(TimeSpan.FromSeconds(3.4), "word", TimeSpan.FromSeconds(3.8), 1)
        ])
    ]);
    var reportPath = Path.Combine(Path.GetTempPath(), $"neon-syllables-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(reportPath, """
        {
          "details": [
            { "text": "First", "words": [
              { "word": "First", "syllable_confidence": 0.9,
                "syllables": [{ "text": "First", "start": 1.0, "end": 1.5, "confidence": 0.9 }] }
            ]},
            { "text": "Second word", "words": [
              { "word": "Second", "syllable_confidence": 0.8,
                "syllables": [{ "text": "Se", "start": 3.0, "end": 3.2, "confidence": 0.8 },
                              { "text": "cond", "start": 3.2, "end": 3.4, "confidence": 0.8 }] },
              { "word": "word", "syllable_confidence": 0.8,
                "syllables": [{ "text": "word", "start": 3.4, "end": 3.8, "confidence": 0.8 }] }
            ]}
          ]
        }
        """);

        var result = await LibraryRepository.AddSyllableAlignmentAsync(
            lyrics, reportPath, CancellationToken.None);
        var syllables = result.Lines[2].Words![0].Syllables!;
        Assert(syllables.Count == 2 && syllables.All(item => item.End > item.Start),
            "Alignment-Silben bleiben nach einer leeren LRC-Anzeigegrenze der richtigen Textzeile zugeordnet.");
    }
    finally
    {
        if (File.Exists(reportPath)) File.Delete(reportPath);
    }
}

static async Task VerifyOverlappingAlignmentSnapshotsLandInReviewCategoryAsync()
{
    var database = Path.Combine(Path.GetTempPath(), $"neon-stage-overlap-review-{Guid.NewGuid():N}.db");
    var libraryRoot = Path.Combine(Path.GetTempPath(), $"neon-stage-overlap-library-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(libraryRoot);
        var options = Options.Create(new KaraokeOptions
        {
            DatabasePath = database,
            LibraryPath = libraryRoot
        });
        var library = new LibraryRepository(options, NullLogger<LibraryRepository>.Instance,
            new TestHubContext(), new ChangeFeedService());
        await library.InitializeAsync(default);
        var audioPath = Path.Combine(libraryRoot, "song.wav");
        WriteTestWave(audioPath);
        await File.WriteAllTextAsync(Path.ChangeExtension(audioPath, ".lrc"), "[00:01.00]First");
        await File.WriteAllTextAsync(Path.ChangeExtension(audioPath, ".vocals.flac"), "stub");
        await File.WriteAllTextAsync(Path.ChangeExtension(audioPath, ".instrumental.flac"), "stub");
        await library.TryReindexAsync(default);
        var song = (await library.SearchAsync(null, 0, 10, default, includeUnreleased: true))
            .SingleOrDefault();
        var lrcPath = Path.Combine(libraryRoot, "overlapping.lrc");
        await File.WriteAllTextAsync(lrcPath,
            "[00:01.000]<00:01.000,00:01.500>First\n[00:01.400]<00:01.400,00:01.900>Second\n");
        var reportPath = Path.Combine(libraryRoot, "overlapping.alignment.json");
        await File.WriteAllTextAsync(reportPath, """{"details": []}""");
        var service = new LyricsAlignmentVersionService(library,
            new LyricsVersionRepository(options), new ChangeFeedService());
        var version = await service.SnapshotFileAsync(song!.Id, lrcPath, reportPath,
            "overlap-test", "test", CancellationToken.None, LyricsVersionStatus.Generated);

        Assert(version.Status == LyricsVersionStatus.ReviewOverlaps,
            "Alignment-Ergebnisse mit Zeilenüberlappungen landen in der Review-Kategorie.");
    }
    finally
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);
        if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
    }
}

static async Task<SongDto> SeedSongAsync(string databasePath, string title)
{
    var song = new SongDto(Guid.NewGuid(), title, "Testband", "Testalbum", 180, true, false, false);
    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync();
    var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS songs(
            id TEXT PRIMARY KEY,title TEXT NOT NULL,artist TEXT NOT NULL,album TEXT NOT NULL,duration REAL NOT NULL,
            hasLyrics INTEGER NOT NULL,hasCover INTEGER NOT NULL
        );
        INSERT INTO songs(id,title,artist,album,duration,hasLyrics,hasCover)
        VALUES($id,$title,$artist,$album,$duration,1,0);
        """;
    command.Parameters.AddWithValue("$id", song.Id.ToString());
    command.Parameters.AddWithValue("$title", song.Title);
    command.Parameters.AddWithValue("$artist", song.Artist);
    command.Parameters.AddWithValue("$album", song.Album);
    command.Parameters.AddWithValue("$duration", song.DurationSeconds);
    await command.ExecuteNonQueryAsync();
    return song;
}

static void WriteTestWave(string path)
{
    const int sampleRate = 8000;
    const short channels = 1;
    const short bits = 16;
    var dataSize = sampleRate * channels * (bits / 8);
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8.ToArray()); writer.Write(36 + dataSize); writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write(channels);
    writer.Write(sampleRate); writer.Write(sampleRate * channels * (bits / 8));
    writer.Write((short)(channels * (bits / 8))); writer.Write(bits);
    writer.Write("data"u8.ToArray()); writer.Write(dataSize); writer.Write(new byte[dataSize]);
}

sealed class TestHubContext : IHubContext<KaraokeHub>
{
    public IHubClients Clients { get; } = new TestHubClients();
    public IGroupManager Groups { get; } = new TestGroupManager();
}

sealed class TestHubClients : IHubClients
{
    private static readonly IClientProxy Proxy = new TestClientProxy();
    public IClientProxy All => Proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Client(string connectionId) => Proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
    public IClientProxy Group(string groupName) => Proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
    public IClientProxy User(string userId) => Proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

sealed class TestClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class TestGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class QueueHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);
    public List<ObservedHttpRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_responses.Count == 0) throw new InvalidOperationException("No mocked HTTP response remains.");
        Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Referrer,
            request.Headers.TryGetValues("X-Requested-With", out var values) &&
            values.Contains("XMLHttpRequest", StringComparer.OrdinalIgnoreCase)));
        var response = _responses.Dequeue();
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

sealed record ObservedHttpRequest(HttpMethod Method, Uri Uri, Uri? Referrer, bool IsAjax);

sealed class ThrowingUsdbClient : IUsdbClient
{
    public Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(string title, string artist,
        CancellationToken cancellationToken) => throw new HttpRequestException("simulated USDB outage");

    public Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(UsdbSearchCandidate candidate,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UsdbDownloadedLyrics> DownloadAsync(UsdbVersionCandidate version,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

sealed class PickerUsdbClient : IUsdbClient
{
    private const string Lyrics = """
        #TITLE:The Nights
        #ARTIST:Avicii
        #BPM:120
        #GAP:1000
        : 0 16 60 The
        : 16 56 62  Nights
        - 72
        E
        """;

    public Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(string title, string artist,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UsdbSearchCandidate>>
    ([
        new("The Nights - Avicii", "The Nights", "Avicii", 2014,
            new Uri("https://usdb.eu/Avicii/TheNights")),
        new("Night Changes - One Direction", "Night Changes", "One Direction", 2014,
            new Uri("https://usdb.eu/OneDirection/NightChanges"))
    ]);

    public Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(UsdbSearchCandidate candidate,
        CancellationToken cancellationToken)
    {
        var exact = candidate.Artist == "Avicii";
        return Task.FromResult<IReadOnlyList<UsdbVersionCandidate>>
        ([new(exact ? 473 : 999, candidate.Title, candidate.Artist, candidate.Year, "English", null,
            new Uri(exact ? "https://usdb.eu/Avicii/TheNights/473" :
                "https://usdb.eu/OneDirection/NightChanges/999"))]);
    }

    public Task<UsdbDownloadedLyrics> DownloadAsync(UsdbVersionCandidate version,
        CancellationToken cancellationToken)
    {
        var parsed = UltraStarLyricsImporter.Parse(Lyrics);
        return Task.FromResult(new UsdbDownloadedLyrics(version, Lyrics, parsed, false));
    }
}
