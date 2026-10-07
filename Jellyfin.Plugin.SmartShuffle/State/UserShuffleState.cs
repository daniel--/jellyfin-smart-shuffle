using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartShuffle.State;

/// <summary>
/// One user's shuffle progress. This, not Jellyfin's played status, is the source of truth.
/// </summary>
public class UserShuffleState
{
    /// <summary>
    /// Gets or sets the id of the user's Smart Shuffle playlist.
    /// </summary>
    public Guid? PlaylistId { get; set; }

    /// <summary>
    /// Gets the upcoming show order. Each slot is a series id; the episode for a slot is
    /// derived from the series' progress, so episode order within a show can never break.
    /// </summary>
    public IList<Guid> Slots { get; init; } = new List<Guid>();

    /// <summary>
    /// Gets the per-series progress.
    /// </summary>
    public Dictionary<Guid, SeriesProgress> Series { get; init; } = [];

    /// <summary>
    /// Gets saved positions of partly watched episodes, by episode id.
    /// </summary>
    public Dictionary<Guid, long> ResumePositions { get; init; } = [];

    /// <summary>
    /// Gets Jellyfin user data captured when playback started, restored when it stops. By episode id.
    /// </summary>
    public Dictionary<Guid, UserDataSnapshot> PendingRestores { get; init; } = [];

    /// <summary>
    /// Gets the episode ids last written to the playlist, to skip no-op updates.
    /// </summary>
    public IList<Guid> PublishedEpisodeIds { get; init; } = new List<Guid>();
}
