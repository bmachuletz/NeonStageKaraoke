using Karaoke.Contracts;
using Karaoke.Server;
using QRCoder;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.Services.Configure<KaraokeOptions>(builder.Configuration.GetSection("Karaoke"));
builder.Services.PostConfigure<KaraokeOptions>(options =>
{
    if (!Path.IsPathFullyQualified(options.DatabasePath))
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
        options.DatabasePath = Path.GetFullPath(Path.Combine(repositoryRoot, options.DatabasePath));
    }
});
builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<ChangeFeedService>();
builder.Services.AddSingleton<StageReactionService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<PlaybackControllerService>();
builder.Services.AddSingleton<ServerSettingsService>();
builder.Services.AddSingleton<SpotifyService>();
builder.Services.AddSingleton<WishlistRepository>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton<WishlistProcessingService>();
builder.Services.AddSingleton<SongImportService>();
builder.Services.AddSingleton<FolderImportService>();
builder.Services.AddSingleton<SongRealignmentService>();
builder.Services.AddSingleton<SongPackageService>();
builder.Services.AddSingleton<StageTimingDiagnosticsService>();
builder.Services.AddSingleton<LyricsVersionRepository>();
builder.Services.AddSingleton<LyricsAlignmentVersionService>();
builder.Services.AddHostedService<LibraryIndexWorker>();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/admin", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "admin.html"), "text/html"));
app.MapGet("/e/{token}", (string token, IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "index.html"), "text/html"));
app.MapHub<KaraokeHub>("/hubs/karaoke");
app.MapGet("/api/changes/stream", async (HttpContext context, ChangeFeedService changes, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";
    var subscription = changes.Subscribe();
    try
    {
        await context.Response.WriteAsync("event: ready\ndata: connected\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
        await foreach (var change in subscription.Reader.ReadAllAsync(ct))
        {
            await context.Response.WriteAsync($"event: {change}\ndata: {DateTimeOffset.UtcNow:O}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    finally { changes.Unsubscribe(subscription.Id); }
});
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "karaoke-server" }));
app.MapPost("/api/diagnostics/stage-timing", (StageTimingSampleDto sample, StageTimingDiagnosticsService diagnostics) =>
{
    diagnostics.Add(sample);
    return Results.NoContent();
});
app.MapGet("/api/diagnostics/stage-timing", (Guid? songId, int? take, StageTimingDiagnosticsService diagnostics) =>
    Results.Ok(diagnostics.Get(songId, Math.Clamp(take ?? 300, 1, 3600))));
app.MapGet("/api/events", async (EventRepository events, CancellationToken ct) => Results.Ok(await events.GetAllAsync(ct)));
app.MapGet("/api/events/active", async (EventRepository events, CancellationToken ct) =>
    await events.GetActiveAsync(ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapGet("/api/events/public/{token}", async (string token, EventRepository events, CancellationToken ct) =>
    await events.GetByTokenAsync(token, ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapPost("/api/events", async (CreateKaraokeEventRequest create, EventRepository events, CancellationToken ct) =>
{
    try { return Results.Ok(await events.CreateAsync(create, ct)); }
    catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
});
app.MapPost("/api/events/{id:guid}/activate", async (Guid id, EventRepository events, CancellationToken ct) =>
    await events.ActivateAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapPost("/api/events/{id:guid}/deactivate", async (Guid id, EventRepository events, CancellationToken ct) =>
    await events.DeactivateAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapDelete("/api/events/{id:guid}", async (Guid id, bool? includeOpenWishes, EventRepository events, CancellationToken ct) =>
    await events.DeleteAsync(id, includeOpenWishes == true, ct) switch
    {
        EventDeleteResult.Deleted => Results.NoContent(),
        EventDeleteResult.NotFound => Results.NotFound(),
        EventDeleteResult.Active => Results.Conflict("Eine aktive Session muss vor dem Löschen beendet werden."),
        EventDeleteResult.HasWishes => Results.Conflict("Die Session enthält noch Wünsche."),
        EventDeleteResult.Protected => Results.Conflict("Die interne Basissession kann nicht gelöscht werden."),
        _ => Results.BadRequest()
    });
app.MapGet("/api/admin/wishlist-processing", (WishlistProcessingService processing) => Results.Ok(processing.GetStatus()));
app.MapGet("/api/admin/song-import", (SongImportService imports) => Results.Ok(imports.GetStatus()));
app.MapGet("/api/admin/folder-import", (FolderImportService imports) => Results.Ok(imports.GetStatus()));
app.MapGet("/api/admin/song-realignment", (SongRealignmentService realignment) => Results.Ok(realignment.GetStatus()));
app.MapPost("/api/admin/songs/{id:guid}/realign", async (Guid id, SongRealignmentService realignment, CancellationToken ct) =>
    await realignment.TryStartAsync(id, ct) switch
    {
        null => Results.NotFound(),
        false => Results.Conflict("Es läuft bereits eine GPU-Neuausrichtung."),
        true => Results.Accepted(value: realignment.GetStatus())
    });
app.MapPost("/api/admin/songs/realign-all", (SongRealignmentService realignment) =>
    realignment.TryStartAll()
        ? Results.Accepted(value: realignment.GetStatus())
        : Results.Conflict("Es läuft bereits eine GPU-Neuausrichtung."));
app.MapPost("/api/admin/song-packages/export", async (SongPackageExportRequest request,
    HttpContext context, SongPackageService packages, CancellationToken ct) =>
{
    try
    {
        var export = await packages.ExportAsync(request.SongIds, ct);
        context.Response.OnCompleted(() =>
        {
            try { File.Delete(export.Path); }
            catch (IOException) { }
            return Task.CompletedTask;
        });
        return Results.File(export.Path, "application/vnd.neonstage.song-package+zip",
            export.FileName, enableRangeProcessing: false);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FileNotFoundException)
    {
        return Results.BadRequest(exception.Message);
    }
});
app.MapPost("/api/admin/song-packages/import", async (HttpRequest request,
    SongPackageService packages, CancellationToken ct) =>
{
    if (request.ContentLength == 0) return Results.BadRequest("Das Songpaket ist leer.");
    try { return Results.Ok(await packages.ImportAsync(request.Body, ct)); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or System.Text.Json.JsonException)
    {
        return Results.BadRequest(exception.Message);
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
app.MapPost("/api/admin/songs/{id:guid}/lyrics/snapshot-alignment", async (
    Guid id, LibraryRepository library, SongRealignmentService realignment, CancellationToken ct) =>
{
    if (await library.GetAsync(id, ct) is null) return Results.NotFound();
    try
    {
        var version = await realignment.SnapshotCurrentAsync(id, ct);
        return Results.Ok(new LyricsVersionSummaryDto(version.Id, version.SongId, version.Revision,
            version.Status, version.AnalysisRunId, version.CreatedAt, version.UpdatedAt));
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
    {
        return Results.BadRequest(exception.Message);
    }
});
app.MapPost("/api/admin/song-import", async (HttpRequest request, SongImportService imports, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Multipart-Formulardaten erwartet.");
    var form = await request.ReadFormAsync(ct);
    var audio = form.Files.GetFile("audio");
    if (audio is null || audio.Length == 0) return Results.BadRequest("MP3-Datei fehlt.");
    try
    {
        var status = await imports.TryQueueAsync(audio, form["title"].ToString(), form["artist"].ToString(),
            form["lyrics"].ToString(), !string.Equals(form["useLrclib"], "false", StringComparison.OrdinalIgnoreCase), ct);
        return status is null ? Results.Conflict(imports.GetStatus()) : Results.Accepted(value: status);
    }
    catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
}).DisableAntiforgery();
app.MapPost("/api/admin/folder-import", (FolderImportRequest request, FolderImportService imports) =>
{
    try
    {
        var status = imports.TryStart(request);
        return status is null ? Results.Conflict(imports.GetStatus()) : Results.Accepted(value: status);
    }
    catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or IOException)
    {
        return Results.BadRequest(exception.Message);
    }
});
app.MapPost("/api/events/{id:guid}/wishlist/process", async (Guid id, int? max, EventRepository events, WishlistRepository wishes, WishlistProcessingService processing, CancellationToken ct) =>
{
    var karaokeEvent = await events.GetByIdAsync(id, ct);
    if (karaokeEvent is null) return Results.NotFound();
    var available = (await wishes.GetAsync(id, ct)).Count;
    return processing.TryStart(karaokeEvent, Math.Clamp(max ?? 0, 0, 100), available)
        ? Results.Accepted(value: processing.GetStatus())
        : Results.Conflict(processing.GetStatus());
});
app.MapPost("/api/admin/wishlist/process", async (EventRepository events, WishlistRepository wishes, WishlistProcessingService processing, CancellationToken ct) =>
{
    var allEvents = await events.GetAllAsync(ct);
    var available = 0;
    foreach (var item in allEvents) available += (await wishes.GetAsync(item.Id, ct)).Count;
    return processing.TryStart(null, 0, available, allEvents: true)
        ? Results.Accepted(value: processing.GetStatus()) : Results.Conflict(processing.GetStatus());
});
app.MapPost("/api/admin/wishlist/import", async (AddWishRequest request, EventRepository events,
    WishlistRepository wishes, WishlistProcessingService processing, CancellationToken ct) =>
{
    var adminEvent = await events.GetByIdAsync(EventRepository.DefaultEventId, ct);
    if (adminEvent is null) return Results.Problem("Die interne Admin-Session fehlt.");
    var wish = await wishes.AddAsync(adminEvent.Id, request with { RequestedBy = "Admin" }, ct);
    if (!processing.TryStart(adminEvent, 1, 1, wish.Id))
        return Results.Conflict(new { message = "Ein Worker läuft bereits. Der Titel wurde zur Wunschliste hinzugefügt.", wish });
    return Results.Accepted(value: new { wish, processing = processing.GetStatus() });
});
app.MapPost("/api/events/{eventId:guid}/wishlist/{wishId:guid}/process", async (Guid eventId, Guid wishId,
    EventRepository events, WishlistRepository wishes, WishlistProcessingService processing, CancellationToken ct) =>
{
    var karaokeEvent = await events.GetByIdAsync(eventId, ct);
    if (karaokeEvent is null) return Results.NotFound();
    if (!(await wishes.GetAsync(eventId, ct)).Any(wish => wish.Id == wishId)) return Results.NotFound();
    return processing.TryStart(karaokeEvent, 1, 1, wishId)
        ? Results.Accepted(value: processing.GetStatus()) : Results.Conflict(processing.GetStatus());
});
app.MapGet("/api/events/{token}/qr", async (string token, HttpRequest request, EventRepository events, CancellationToken ct) =>
{
    if (await events.GetByTokenAsync(token, ct) is null) return Results.NotFound();
    var url = $"{request.Scheme}://{request.Host}/e/{Uri.EscapeDataString(token)}";
    return Results.Bytes(PngByteQRCodeHelper.GetQRCode(url, QRCodeGenerator.ECCLevel.Q, 12, false), "image/png");
});
app.MapGet("/api/stage/guest-qr", async (HttpRequest request, EventRepository events, CancellationToken ct) =>
{
    var active = await events.GetActiveAsync(ct);
    var guestUrl = active is null ? $"{request.Scheme}://{request.Host}/" :
        $"{request.Scheme}://{request.Host}/e/{Uri.EscapeDataString(active.InviteToken)}";
    var png = PngByteQRCodeHelper.GetQRCode(guestUrl, QRCodeGenerator.ECCLevel.Q, 12, false);
    return Results.Bytes(png, "image/png");
});
app.MapPost("/api/reactions", async (string? eventToken, StageReactionRequest reaction,
    EventRepository events, StageReactionService reactions, CancellationToken ct) =>
{
    var eventId = await events.ResolveIdAsync(eventToken, ct);
    if (eventId is null) return Results.NotFound();
    return reactions.Add(eventId.Value, reaction) is { } added ? Results.Ok(added) : Results.StatusCode(429);
});
app.MapGet("/api/events/{eventId:guid}/reactions", (Guid eventId, long? after, StageReactionService reactions) =>
    Results.Ok(new { items = reactions.Get(eventId, Math.Max(0, after ?? 0)) }));
app.MapGet("/api/songs", async (string? q, int? skip, int? take, bool? includeUnreleased, LibraryRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.SearchAsync(q, skip ?? 0, Math.Clamp(take ?? 50, 1, 500), ct, includeUnreleased == true)));
app.MapGet("/api/songs/new", async (int? take, LibraryRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetNewestAsync(take ?? 8, ct)));
app.MapGet("/api/library/search", async (string? q, int? page, int? pageSize, string? eventToken, LibraryRepository repo, QueueService queue, EventRepository events, CancellationToken ct) =>
{
    var safePage = Math.Max(page ?? 1, 1);
    var safePageSize = Math.Clamp(pageSize ?? 30, 1, 100);
    var result = await repo.SearchPageAsync(q, safePage, safePageSize, ct);
    var eventId = await events.ResolveIdAsync(eventToken, ct);
    if (eventId is null) return Results.NotFound();
    var queuedIds = await queue.GetQueuedSongIdsAsync(eventId.Value, ct);
    return Results.Ok(result with
    {
        Items = result.Items.Select(song => song with { IsQueued = queuedIds.Contains(song.Id) }).ToArray()
    });
});
app.MapGet("/api/songs/{id:guid}", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.GetAsync(id, ct) is { } song ? Results.Ok(song) : Results.NotFound());
app.MapPut("/api/admin/songs/{id:guid}/review-status", async (Guid id, ChangeSongReviewStatusRequest request,
    LibraryRepository repo, CancellationToken ct) =>
    await repo.SetReviewStatusAsync(id, request.Status, ct) is { } song ? Results.Ok(song) : Results.BadRequest("Lyrics sowie Instrumental- und Vocalspur sind erforderlich."));
app.MapDelete("/api/admin/songs/{id:guid}", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.DeleteSongAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
app.MapGet("/api/songs/{id:guid}/lyrics", async (Guid id, LibraryRepository repo, LyricsVersionRepository versions, CancellationToken ct) =>
{
    var lyrics = await repo.ReadLyricsAsync(id, ct);
    if (lyrics is null) return Results.NotFound();
    var version = await versions.GetRuntimeAsync(id, ct);
    if (version is null) return Results.Ok(lyrics);
    try
    {
        return Results.Ok(EditorLyricsRuntimeMapper.Map(lyrics, version.DocumentJson));
    }
    catch (System.Text.Json.JsonException) { return Results.Ok(lyrics); }
});
app.MapGet("/api/admin/songs/{id:guid}/lyrics/source", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.ReadLyricsAsync(id, ct) is { } lyrics ? Results.Ok(lyrics) : Results.NotFound());
app.MapGet("/api/songs/{id:guid}/lyrics/editor", async (Guid id, LyricsVersionRepository versions, CancellationToken ct) =>
    await versions.GetLatestDraftAsync(id, ct) is { } version ? Results.Ok(version) : Results.NotFound());
app.MapGet("/api/songs/{id:guid}/lyrics/versions", async (Guid id, LyricsVersionRepository versions, CancellationToken ct) =>
    Results.Ok(await versions.GetAllAsync(id, ct)));
app.MapGet("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}", async (Guid id, Guid versionId, LyricsVersionRepository versions, CancellationToken ct) =>
    await versions.GetAsync(id, versionId, ct) is { } version ? Results.Ok(version) : Results.NotFound());
app.MapDelete("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}", async (Guid id, Guid versionId, LyricsVersionRepository versions, CancellationToken ct) =>
    await versions.DeleteAsync(id, versionId, ct) switch
    {
        LyricsVersionDeleteResult.Deleted => Results.NoContent(),
        LyricsVersionDeleteResult.Published => Results.Conflict("Die aktuell veröffentlichte Lyrics-Version ist geschützt. Veröffentliche zuerst einen anderen Stand."),
        _ => Results.NotFound()
    });
app.MapPost("/api/songs/{id:guid}/lyrics/versions", async (Guid id, CreateLyricsVersionRequest request, LyricsVersionRepository versions, LibraryRepository library, CancellationToken ct) =>
{
    if (await library.GetAsync(id, ct) is null) return Results.NotFound();
    try { return Results.Ok(await versions.CreateAsync(id, request, ct)); }
    catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException) { return Results.BadRequest(exception.Message); }
});
app.MapPut("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}", async (Guid id, Guid versionId, UpdateLyricsVersionRequest request, LyricsVersionRepository versions, CancellationToken ct) =>
{
    try { return await versions.UpdateAsync(id, versionId, request, ct) is { } version ? Results.Ok(version) : Results.Conflict("Die Lyrics-Version wurde zwischenzeitlich verändert oder bereits veröffentlicht."); }
    catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException) { return Results.BadRequest(exception.Message); }
});
app.MapPost("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}/review", (Guid id, Guid versionId, ChangeLyricsVersionStatusRequest request, LyricsVersionRepository versions, CancellationToken ct) =>
    ChangeLyricsStatus(id, versionId, request.ExpectedRevision, LyricsVersionStatus.Reviewed, request.AllowTimingConflicts, versions, ct));
app.MapPost("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}/approve", (Guid id, Guid versionId, ChangeLyricsVersionStatusRequest request, LyricsVersionRepository versions, CancellationToken ct) =>
    ChangeLyricsStatus(id, versionId, request.ExpectedRevision, LyricsVersionStatus.Approved, request.AllowTimingConflicts, versions, ct));
app.MapPost("/api/songs/{id:guid}/lyrics/versions/{versionId:guid}/publish", (Guid id, Guid versionId, ChangeLyricsVersionStatusRequest request, LyricsVersionRepository versions, CancellationToken ct) =>
    ChangeLyricsStatus(id, versionId, request.ExpectedRevision, LyricsVersionStatus.Published, request.AllowTimingConflicts, versions, ct));
app.MapGet("/api/songs/{id:guid}/visualization", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.ReadVisualizationAsync(id, ct) is { } visualization ? Results.Ok(visualization) : Results.NotFound());
app.MapGet("/api/songs/{id:guid}/audio", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
{
    var file = await repo.GetAudioFileAsync(id, ct);
    return file is { } audioFile
        ? Results.File(audioFile.Path, audioFile.ContentType, enableRangeProcessing: true)
        : Results.NotFound();
});
app.MapGet("/api/songs/{id:guid}/stems", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.GetStemAvailabilityAsync(id, ct) is { } stems ? Results.Ok(stems) : Results.NotFound());
app.MapGet("/api/songs/{id:guid}/stems/{kind}", async (Guid id, string kind, string? format, LibraryRepository repo, CancellationToken ct) =>
{
    var file = await repo.GetStemFileAsync(id, kind.ToLowerInvariant(), ct, format);
    return file is { } stem
        ? Results.File(stem.Path, stem.ContentType, enableRangeProcessing: true)
        : Results.NotFound();
});
app.MapGet("/api/songs/{id:guid}/cover", async (Guid id, LibraryRepository repo, CancellationToken ct) =>
    await repo.GetCoverAsync(id, ct) is { } cover
        ? Results.Bytes(cover.Data, cover.ContentType)
        : Results.NotFound());
app.MapPost("/api/admin/songs/{id:guid}/cover", async (Guid id, IFormFile file, LibraryRepository repo, CancellationToken ct) =>
{
    await using var stream = file.OpenReadStream();
    return await repo.SetCoverAsync(id, stream, file.Length, ct)
        ? Results.NoContent()
        : Results.BadRequest("Das Bild konnte nicht als Cover verarbeitet werden.");
}).DisableAntiforgery();
app.MapGet("/api/queue", async (string? eventToken, QueueService queue, EventRepository events, CancellationToken ct) =>
    await events.ResolveIdAsync(eventToken, ct) is { } eventId ? Results.Ok(await queue.GetStateAsync(eventId, ct)) : Results.NotFound());
app.MapPost("/api/queue", async (string? eventToken, AddQueueRequest request, QueueService queue, LibraryRepository repo, EventRepository events, CancellationToken ct) =>
{
    var eventId = await events.ResolveIdAsync(eventToken, ct);
    if (eventId is null) return Results.NotFound();
    var song = await repo.GetAsync(request.SongId, ct);
    return song is not { ReviewStatus: SongReviewStatus.Approved }
        ? Results.BadRequest("Dieser Song ist noch nicht freigegeben.")
        : Results.Ok(await queue.AddAsync(eventId.Value, song, request.RequestedBy, ct));
});
app.MapDelete("/api/queue/{id:guid}", async (Guid id, string? eventToken, QueueService queue, EventRepository events, CancellationToken ct) =>
    await events.ResolveIdAsync(eventToken, ct) is { } eventId && await queue.RemoveAsync(eventId, id, ct) ? Results.NoContent() : Results.NotFound());
app.MapDelete("/api/queue", async (string? eventToken, QueueService queue, EventRepository events, CancellationToken ct) =>
{ var eventId = await events.ResolveIdAsync(eventToken, ct); if (eventId is null) return Results.NotFound(); await queue.ClearAsync(eventId.Value, ct); return Results.NoContent(); });
app.MapPut("/api/queue/reorder", async (string? eventToken, ReorderQueueRequest request, QueueService queue, EventRepository events, CancellationToken ct) =>
{ var eventId = await events.ResolveIdAsync(eventToken, ct); return eventId is not null && await queue.ReorderAsync(eventId.Value, request.EntryId, request.Position, ct) ? Results.Ok(await queue.GetStateAsync(eventId.Value, ct)) : Results.NotFound(); });
app.MapPost("/api/playback/controller/claim", (PlaybackControllerRequest request, PlaybackControllerService controller) =>
    Results.Ok(controller.Claim(request)));
app.MapDelete("/api/playback/controller", (HttpRequest request, PlaybackControllerService controller) =>
{
    if (ReadControllerId(request) is not { } id) return Results.BadRequest();
    controller.Release(id);
    return Results.NoContent();
});
app.MapPost("/api/playback/start", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.StartAsync(ct)) : Results.Conflict("Eine andere App steuert bereits die Bühne."));
app.MapPost("/api/playback/start-fresh", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.StartFreshAsync(ct)) : Results.Conflict("Eine andere App steuert bereits die Bühne."));
app.MapPost("/api/playback/pause", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.PauseAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/resume", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.ResumeAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/next", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.NextAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/complete", async (HttpRequest request, CompletePlaybackRequest completion, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.CompleteAsync(completion.QueueEntryId, ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/previous", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.PreviousAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/skip", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.SkipAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/restart", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.RestartAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/stop", async (HttpRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    HasControl(request, controller) ? Results.Ok(await queue.StopAsync(ct)) : Results.Conflict("Keine Steuerberechtigung."));
app.MapPost("/api/playback/position", async (HttpRequest httpRequest, PlaybackPositionUpdateRequest request, QueueService queue, PlaybackControllerService controller, CancellationToken ct) =>
    !HasControl(httpRequest, controller) ? Results.Conflict("Keine Steuerberechtigung.") :
    await queue.UpdatePositionAsync(request, ct) is { } update ? Results.Ok(update) : Results.Conflict());
app.MapGet("/api/library/scan-status", (LibraryRepository repo) => Results.Ok(repo.GetScanStatus()));
app.MapPost("/api/library/reindex", async (LibraryRepository repo, CancellationToken ct) =>
    await repo.TryReindexAsync(ct)
        ? Results.Ok(repo.GetScanStatus())
        : Results.Conflict(repo.GetScanStatus()));
app.MapGet("/api/settings/library", (ServerSettingsService settings) => Results.Ok(settings.Get()));
app.MapPut("/api/settings/library", async (LibrarySettingsDto request, ServerSettingsService settings, LibraryRepository repo, CancellationToken ct) =>
{
    try
    {
        var saved = await settings.UpdateAsync(request.LibraryPath, ct);
        _ = repo.TryReindexAsync(CancellationToken.None);
        return Results.Ok(saved);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(exception.Message);
    }
});
app.MapGet("/api/spotify/status", async (SpotifyService spotify, CancellationToken ct) => Results.Ok(await spotify.GetStatusAsync(ct)));
app.MapGet("/api/spotify/connect", (SpotifyService spotify) =>
{
    try { return Results.Redirect(spotify.CreateAuthorizationUrl()); }
    catch (InvalidOperationException exception) { return Results.BadRequest(exception.Message); }
});
app.MapGet("/api/spotify/callback", async (string? code, string? state, string? error, SpotifyService spotify, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error)) return Results.Content($"Spotify-Verbindung abgelehnt: {error}", "text/plain");
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state)) return Results.BadRequest("Spotify-Antwort unvollständig.");
    try
    {
        await spotify.CompleteAuthorizationAsync(code, state, ct);
        return Results.Content("<html><body style='background:#090713;color:white;font-family:sans-serif;text-align:center;padding:15vh'><h1 style='color:#e9ff39'>Spotify verbunden</h1><p>Die Neon-Stage-Wunschplaylist ist bereit. Dieses Fenster kann geschlossen werden.</p></body></html>", "text/html");
    }
    catch (Exception exception) { return Results.BadRequest(exception.Message); }
});
app.MapGet("/api/wishlist/search", async (string? q, SpotifyService spotify, CancellationToken ct) =>
{
    try { return Results.Ok(await spotify.SearchAsync(q ?? string.Empty, ct)); }
    catch (Exception exception) { return Results.Problem("Spotify-Suche fehlgeschlagen: " + exception.Message); }
});
app.MapGet("/api/wishlist", async (string? eventToken, WishlistRepository wishes, EventRepository events, CancellationToken ct) =>
    await events.ResolveIdAsync(eventToken, ct) is { } eventId ? Results.Ok(await wishes.GetAsync(eventId, ct)) : Results.NotFound());
app.MapPost("/api/wishlist", async (string? eventToken, AddWishRequest request, WishlistRepository wishes, SpotifyService spotify, EventRepository events, CancellationToken ct) =>
{
    var eventId = await events.ResolveIdAsync(eventToken, ct);
    if (eventId is null) return Results.NotFound();
    var existed = (await wishes.GetAsync(eventId.Value, ct)).Any(wish => wish.Track.Id == request.Track.Id);
    var wish = await wishes.AddAsync(eventId.Value, request, ct);
    if (!existed)
    {
        try { await spotify.AddToPlaylistAsync(request.Track.Uri, ct); }
        catch (Exception) { /* Der lokale Wunsch bleibt auch bei einer Spotify-Störung erhalten. */ }
    }
    return Results.Ok(wish);
});
app.MapDelete("/api/wishlist/{id:guid}", async (Guid id, string? eventToken, WishlistRepository wishes, EventRepository events, CancellationToken ct) =>
    await events.ResolveIdAsync(eventToken, ct) is { } eventId && await wishes.RemoveAsync(eventId, id, ct) ? Results.NoContent() : Results.NotFound());

// Event activation resets the playback state. Initialize the queue schema before
// accepting requests so a freshly created database can start a quick session
// before the queue endpoint has ever been called.
await app.Services.GetRequiredService<QueueService>().InitializeAsync(CancellationToken.None);

app.Run();

static Guid? ReadControllerId(HttpRequest request) =>
    Guid.TryParse(request.Headers[PlaybackControllerService.HeaderName].FirstOrDefault(), out var id) ? id : null;

static bool HasControl(HttpRequest request, PlaybackControllerService controller) =>
    ReadControllerId(request) is { } id && controller.Owns(id);

static async Task<IResult> ChangeLyricsStatus(Guid songId, Guid versionId, long expectedRevision,
    LyricsVersionStatus target, bool allowTimingConflicts, LyricsVersionRepository versions, CancellationToken ct)
{
    try
    {
        return await versions.ChangeStatusAsync(songId, versionId, expectedRevision, target, ct,
            allowTimingConflicts) is { } changed
            ? Results.Ok(changed)
            : Results.Conflict("Ungültiger Statuswechsel oder zwischenzeitlich geänderte Lyrics-Version.");
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
}
