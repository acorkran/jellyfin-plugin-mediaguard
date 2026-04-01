using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.MediaGuard.Services;

/// <summary>
/// Tracks consecutive playback failure counts per item with time-based expiry.
/// Only items that fail repeatedly within a configured time window are considered
/// genuinely corrupt, preventing false positives from occasional user skips
/// that accumulate over days or weeks.
/// </summary>
public class FailureCounter
{
    private readonly ConcurrentDictionary<Guid, FailureRecord> _failures = new();

    /// <summary>
    /// Records a failure for the given item and returns the new failure count.
    /// If the previous failure is older than <paramref name="windowHours"/>,
    /// the count resets to 1 (stale failures don't accumulate).
    /// </summary>
    public int RecordFailure(Guid itemId, int windowHours)
    {
        var now = DateTime.UtcNow;
        return _failures.AddOrUpdate(
            itemId,
            _ => new FailureRecord(1, now),
            (_, existing) =>
            {
                if ((now - existing.LastFailure).TotalHours > windowHours)
                {
                    return new FailureRecord(1, now);
                }

                return new FailureRecord(existing.Count + 1, now);
            }).Count;
    }

    /// <summary>
    /// Resets the failure count for an item (e.g. after a successful re-download
    /// or after a successful playback).
    /// </summary>
    public void Reset(Guid itemId)
    {
        _failures.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Gets the current failure count for an item.
    /// </summary>
    public int GetCount(Guid itemId)
    {
        return _failures.TryGetValue(itemId, out var record) ? record.Count : 0;
    }

    private sealed record FailureRecord(int Count, DateTime LastFailure);
}
