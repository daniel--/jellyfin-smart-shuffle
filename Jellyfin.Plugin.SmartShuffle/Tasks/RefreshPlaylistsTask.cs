using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SmartShuffle.Tasks;

/// <summary>
/// Rebuilds the Smart Shuffle playlists, keeping the upcoming show order. Picks up new episodes,
/// collection changes, and anything missed while the server was down.
/// </summary>
public class RefreshPlaylistsTask : IScheduledTask
{
    private readonly ShuffleService _shuffleService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshPlaylistsTask"/> class.
    /// </summary>
    /// <param name="shuffleService">The shuffle service.</param>
    public RefreshPlaylistsTask(ShuffleService shuffleService)
    {
        _shuffleService = shuffleService;
    }

    /// <inheritdoc />
    public string Name => "Refresh Smart Shuffle playlists";

    /// <inheritdoc />
    public string Key => "SmartShuffleRefresh";

    /// <inheritdoc />
    public string Description => "Rebuilds each user's Smart Shuffle playlist, keeping the upcoming show order.";

    /// <inheritdoc />
    public string Category => "Smart Shuffle";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _shuffleService.RefreshAllAsync(false, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }
        ];
    }
}
