using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartShuffle.Configuration;

/// <summary>
/// How the next show is picked when the playlist is topped up.
/// </summary>
public enum ShowWeighting
{
    /// <summary>
    /// Every show has the same chance.
    /// </summary>
    Uniform,

    /// <summary>
    /// Shows with more episodes left are picked more often, so shows tend to finish together.
    /// </summary>
    RemainingEpisodes
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the name of the collection holding the shows to shuffle.
    /// </summary>
    public string CollectionName { get; set; } = "Smart Shuffle";

    /// <summary>
    /// Gets or sets the name of the playlist created for each user.
    /// </summary>
    public string PlaylistName { get; set; } = "Smart Shuffle";

    /// <summary>
    /// Gets or sets how many episodes the playlist holds.
    /// </summary>
    public int PlaylistLength { get; set; } = 50;

    /// <summary>
    /// Gets or sets how the next show is picked.
    /// </summary>
    public ShowWeighting Weighting { get; set; } = ShowWeighting.RemainingEpisodes;

    /// <summary>
    /// Gets or sets a value indicating whether the same show may be picked twice in a row.
    /// </summary>
    public bool AvoidBackToBack { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a show starts again from the first episode once finished.
    /// </summary>
    public bool LoopFinishedShows { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether specials (season 0) are included.
    /// </summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>
    /// Gets or sets how long to wait after playback starts before seeking to the saved position.
    /// </summary>
    public int ResumeSeekDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Gets or sets the ids of the users that get a Smart Shuffle playlist.
    /// </summary>
#pragma warning disable CA1819 // Properties should not return arrays - required for XML serialization
    public Guid[] EnabledUserIds { get; set; } = [];
#pragma warning restore CA1819
}
