using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartShuffle.State;

/// <summary>
/// Everything the plugin persists, keyed by user id.
/// </summary>
public class ShuffleState
{
    /// <summary>
    /// Gets the per-user state.
    /// </summary>
    public Dictionary<Guid, UserShuffleState> Users { get; init; } = [];

    /// <summary>
    /// Gets the state for a user, creating it if needed.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The user's state.</returns>
    public UserShuffleState ForUser(Guid userId)
    {
        if (!Users.TryGetValue(userId, out var state))
        {
            state = new UserShuffleState();
            Users[userId] = state;
        }

        return state;
    }
}
