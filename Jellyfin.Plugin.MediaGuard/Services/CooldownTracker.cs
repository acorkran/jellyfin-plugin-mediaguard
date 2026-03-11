using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.MediaGuard.Services;

/// <summary>
/// Tracks recently flagged items to prevent duplicate re-download requests.
/// </summary>
public class CooldownTracker
{
    private readonly ConcurrentDictionary<Guid, DateTime> _flaggedItems = new();

    /// <summary>
    /// Returns true if the item should be processed (not on cooldown).
    /// </summary>
    public bool TryFlag(Guid itemId, int cooldownHours)
    {
        var now = DateTime.UtcNow;

        // Clean up expired entries
        foreach (var kvp in _flaggedItems)
        {
            if (now - kvp.Value > TimeSpan.FromHours(cooldownHours))
            {
                _flaggedItems.TryRemove(kvp.Key, out _);
            }
        }

        if (_flaggedItems.TryGetValue(itemId, out var lastFlagged)
            && now - lastFlagged < TimeSpan.FromHours(cooldownHours))
        {
            return false;
        }

        _flaggedItems[itemId] = now;
        return true;
    }
}
