using System;

namespace Jellyfin.Plugin.SmartShuffle.State;

/// <summary>
/// The parts of Jellyfin's user data that playback changes on an already watched episode.
/// </summary>
public class UserDataSnapshot
{
    /// <summary>
    /// Gets or sets the play count before playback started.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// Gets or sets the last played date before playback started.
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the original last played date is unknown and should be left as is.
    /// </summary>
    public bool KeepCurrentLastPlayedDate { get; set; }
}
