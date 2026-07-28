using Karaoke.Contracts;
using Karaoke.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

var databasePath = Path.Combine(Path.GetTempPath(), $"neon-stage-playback-{Guid.NewGuid():N}.db");
string? adoptionLibraryPath = null;
string? adoptionDatabasePath = null;
try
{
    var options = Options.Create(new KaraokeOptions { DatabasePath = databasePath });
    var events = new EventRepository(options);
    var queue = new QueueService(options, new TestHubContext(), events);
    await queue.InitializeAsync(default);
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
    Assert(controller.Claim(new(appOne, "App Eins")).OwnsControl, "Die aktive App kann ihre Lease erneuern.");
    controller.Release(appOne);
    Assert(controller.Claim(new(appTwo, "App Zwei")).OwnsControl, "Nach der Freigabe kann eine andere App übernehmen.");

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
                   Currency: "EUR", AudioQuality: "FLAC 24 bit / 96 kHz" } &&
               mappedQobuz.SourceUrl == "https://open.qobuz.com/track/123456",
            "Qobuz-Suchergebnisse behalten Quelle, Preis, Qualität und Katalog-ID.");
    }

    var lyricsVersions = new LyricsVersionRepository(options);
    var versionSongId = Guid.NewGuid();
    var editorJson = JsonSerializer.Serialize(new { schemaVersion = 1, songId = versionSongId, lines = Array.Empty<object>() });
    var version = await lyricsVersions.CreateAsync(versionSongId, new(editorJson), default);
    Assert(version.Revision == 1 && version.Status == LyricsVersionStatus.InReview,
        "Ein Editorentwurf startet versioniert im Prüfstatus.");
    var staleUpdate = await lyricsVersions.UpdateAsync(versionSongId, version.Id,
        new(0, editorJson), default);
    Assert(staleUpdate is null, "Eine veraltete Revision überschreibt keinen Editorentwurf.");
    var changedEditorJson = JsonSerializer.Serialize(new
        { schemaVersion = 1, songId = versionSongId, marker = "zweiter Stand", lines = Array.Empty<object>() });
    var savedVersion = await lyricsVersions.UpdateAsync(versionSongId, version.Id,
        new(version.Revision, changedEditorJson), default);
    Assert(savedVersion is { Revision: 2 } && savedVersion.Id != version.Id,
        "Jedes Speichern legt einen eigenständigen Lyrics-Stand mit neuer Revision an.");
    var archivedVersion = await lyricsVersions.GetAsync(versionSongId, version.Id, default);
    Assert(archivedVersion?.Status == LyricsVersionStatus.Superseded && archivedVersion.DocumentJson == editorJson,
        "Der vorherige Lyrics-Inhalt bleibt unverändert im Versionsarchiv erhalten.");
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
