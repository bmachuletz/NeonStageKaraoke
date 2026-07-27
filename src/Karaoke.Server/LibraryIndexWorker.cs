namespace Karaoke.Server;
public sealed class LibraryIndexWorker(LibraryRepository repository, ServerSettingsService settings, ILogger<LibraryIndexWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    { try { await settings.InitializeAsync(stoppingToken); await repository.InitializeAsync(stoppingToken); await repository.TryReindexAsync(stoppingToken); } catch(Exception ex){ log.LogError(ex,"Bibliotheksindex konnte nicht aufgebaut werden"); } }
}
