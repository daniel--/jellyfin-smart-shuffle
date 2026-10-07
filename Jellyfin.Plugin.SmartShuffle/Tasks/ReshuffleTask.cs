using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SmartShuffle.Tasks;

/// <summary>
/// Throws away the upcoming show order and picks a new one. Progress within each show is kept.
/// Has no default triggers; run it from the dashboard.
/// </summary>
public class ReshuffleTask : IScheduledTask
{
    private readonly ShuffleService _shuffleService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReshuffleTask"/> class.
    /// </summary>
    /// <param name="shuffleService">The shuffle service.</param>
    public ReshuffleTask(ShuffleService shuffleService)
    {
        _shuffleService = shuffleService;
    }

    /// <inheritdoc />
    public string Name => "Reshuffle Smart Shuffle playlists";

    /// <inheritdoc />
    public string Key => "SmartShuffleReshuffle";

    /// <inheritdoc />
    public string Description => "Picks a new random show order. Each show's progress is kept.";

    /// <inheritdoc />
    public string Category => "Smart Shuffle";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _shuffleService.RefreshAllAsync(true, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
