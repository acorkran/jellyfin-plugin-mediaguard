using Jellyfin.Plugin.MediaGuard.Services;

namespace Jellyfin.Plugin.MediaGuard.Tests;

public class CooldownTrackerTests
{
    private readonly CooldownTracker _tracker = new();

    [Fact]
    public void TryFlag_FirstTime_ReturnsTrue()
    {
        var itemId = Guid.NewGuid();
        Assert.True(_tracker.TryFlag(itemId, cooldownHours: 24));
    }

    [Fact]
    public void TryFlag_WithinCooldown_ReturnsFalse()
    {
        var itemId = Guid.NewGuid();
        _tracker.TryFlag(itemId, cooldownHours: 24);
        Assert.False(_tracker.TryFlag(itemId, cooldownHours: 24));
    }

    [Fact]
    public void TryFlag_DifferentItems_BothReturnTrue()
    {
        var item1 = Guid.NewGuid();
        var item2 = Guid.NewGuid();

        Assert.True(_tracker.TryFlag(item1, cooldownHours: 24));
        Assert.True(_tracker.TryFlag(item2, cooldownHours: 24));
    }

    [Fact]
    public void TryFlag_ZeroCooldown_AlwaysReturnsTrue()
    {
        // With 0 hours cooldown, everything is immediately expired
        var itemId = Guid.NewGuid();
        Assert.True(_tracker.TryFlag(itemId, cooldownHours: 0));
        // The second call: the entry was just added with timestamp = now,
        // and cooldown is 0 hours. now - flagTime < 0 hours is false since
        // they're essentially the same instant. So this depends on timing.
        // With 0 cooldown the check is: now - lastFlagged < 0 hours = always false
        // So it should always return true.
        Assert.True(_tracker.TryFlag(itemId, cooldownHours: 0));
    }

    [Fact]
    public void TryFlag_RepeatedCooldownCheck_StaysBlocked()
    {
        var itemId = Guid.NewGuid();
        _tracker.TryFlag(itemId, cooldownHours: 24);
        Assert.False(_tracker.TryFlag(itemId, cooldownHours: 24));
        Assert.False(_tracker.TryFlag(itemId, cooldownHours: 24));
        Assert.False(_tracker.TryFlag(itemId, cooldownHours: 24));
    }
}
