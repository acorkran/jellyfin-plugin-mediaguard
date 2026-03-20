using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.MediaGuard.Services;

/// <summary>
/// Tracks consecutive playback failure counts per item.
/// Only items that fail repeatedly (exceeding the configured threshold)
/// are considered genuinely corrupt, preventing false positives from user skips.
/// </summary>
public class FailureCounter
{
    private readonly ConcurrentDictionary<Guid, int> _failureCounts = new();

    /// <summary>
    /// Records a failure for the given item and returns the new failure count.
    /// </summary>
    public int RecordFailure(Guid itemId)
    {
        return _failureCounts.AddOrUpdate(itemId, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Resets the failure count for an item (e.g. after a successful re-download).
    /// </summary>
    public void Reset(Guid itemId)
    {
        _failureCounts.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Gets the current failure count for an item.
    /// </summary>
    public int GetCount(Guid itemId)
    {
        return _failureCounts.TryGetValue(itemId, out var count) ? count : 0;
    }
}
