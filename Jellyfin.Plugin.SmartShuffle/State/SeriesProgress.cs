using System;

namespace Jellyfin.Plugin.SmartShuffle.State;

/// <summary>
/// Progress through one series.
/// </summary>
public class SeriesProgress
{
    /// <summary>
    /// Gets or sets the last episode finished through Smart Shuffle.
    /// </summary>
    public Guid? LastFinishedEpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the season number of the last finished episode, used if the episode id disappears.
    /// </summary>
    public int? LastSeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number of the last finished episode, used if the episode id disappears.
    /// </summary>
    public int? LastEpisodeNumber { get; set; }
}
