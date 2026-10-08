using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartShuffle.Playback;

/// <summary>
/// A player's play queue and where in it the player is.
/// </summary>
/// <param name="ItemIds">The item ids in the queue.</param>
/// <param name="Index">The position of the item now playing.</param>
public sealed record PlayQueuePosition(IReadOnlyList<Guid> ItemIds, int Index)
{
    /// <summary>
    /// Gets the item now playing and the ones after it.
    /// </summary>
    public IReadOnlyList<Guid> Upcoming => ItemIds.Skip(Index).ToList();
}
