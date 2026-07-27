using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class StageTimingDiagnosticsService
{
    private const int Capacity = 3600;
    private readonly object _gate = new();
    private readonly Queue<StageTimingSampleDto> _samples = new();

    public void Add(StageTimingSampleDto sample)
    {
        lock (_gate)
        {
            _samples.Enqueue(sample with { CapturedAt = DateTimeOffset.UtcNow });
            while (_samples.Count > Capacity) _samples.Dequeue();
        }
    }

    public IReadOnlyList<StageTimingSampleDto> Get(Guid? songId, int take)
    {
        lock (_gate)
            return _samples
                .Where(sample => songId is null || sample.SongId == songId)
                .TakeLast(Math.Clamp(take, 1, Capacity))
                .ToArray();
    }
}
