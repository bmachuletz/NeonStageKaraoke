using System.Text.Json;

namespace Karaoke.App.Desktop;

internal sealed class EditorDraftRecovery
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonStage", "editor-drafts-v1");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Task SaveAsync(Guid songId, RecoveryDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, songId.ToString("N") + ".json");
        var temporary = target + ".writing";
        try
        {
            // Absichtlich synchron und atomar: Nach dem Klick auf „Speichern“
            // existiert die Recovery-Datei, bevor die UI wieder Eingaben annimmt
            // oder das Fenster geschlossen werden kann.
            File.WriteAllText(temporary, JsonSerializer.Serialize(draft, Options));
            File.Move(temporary, target, true);
            return Task.CompletedTask;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<RecoveryDraft?> LoadAsync(Guid songId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, songId.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<RecoveryDraft>(stream, Options, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root)) return Task.FromResult(0);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".writing", StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(path);
            deleted++;
        }
        return Task.FromResult(deleted);
    }
}

internal sealed record RecoveryDraft(DateTimeOffset SavedAt, string DocumentJson,
    Guid? ServerVersionId, long ServerRevision, string? SourceFingerprint = null);
