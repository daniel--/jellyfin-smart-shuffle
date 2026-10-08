using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartShuffle.Playback;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartShuffle;

/// <summary>
/// Hooks Jellyfin's playback, collection and configuration events up to <see cref="ShuffleService"/>.
/// </summary>
public sealed class PlaybackListener : IHostedService, IDisposable
{
    // Players start the next item in their queue within a second or so of stopping the last one.
    private static readonly TimeSpan _continueWindow = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessionManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ShuffleService _shuffleService;
    private readonly ReportedQueues _reportedQueues;
    private readonly ILogger<PlaybackListener> _logger;
    private readonly CancellationTokenSource _stopping = new();

    // The last episode each session stopped, by session id.
    private readonly ConcurrentDictionary<string, LastStop> _lastStops = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackListener"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="syncPlayManager">The SyncPlay manager.</param>
    /// <param name="shuffleService">The shuffle service.</param>
    /// <param name="reportedQueues">The queues players sent with their playback start reports.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackListener(
        ISessionManager sessionManager,
        ICollectionManager collectionManager,
        ISyncPlayManager syncPlayManager,
        ShuffleService shuffleService,
        ReportedQueues reportedQueues,
        ILogger<PlaybackListener> logger)
    {
        _sessionManager = sessionManager;
        _collectionManager = collectionManager;
        _syncPlayManager = syncPlayManager;
        _shuffleService = shuffleService;
        _reportedQueues = reportedQueues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded += OnSessionEnded;
        _collectionManager.ItemsAddedToCollection += OnCollectionChanged;
        _collectionManager.ItemsRemovedFromCollection += OnCollectionChanged;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
        _collectionManager.ItemsAddedToCollection -= OnCollectionChanged;
        _collectionManager.ItemsRemovedFromCollection -= OnCollectionChanged;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Episode episode)
        {
            return;
        }

        // Read now: the session's queue is replaced when this episode stops.
        var continued = GetContinuedQueue(e.Session, episode);
        var syncQueue = Plugin.Instance?.Configuration.KeepPlayerQueueInSync != false;

        foreach (var user in e.Users)
        {
            if (!_shuffleService.Handles(user, episode))
            {
                continue;
            }

            Run(async ct =>
            {
                var seekTo = await _shuffleService.OnPlaybackStartAsync(user, episode, e.PlaybackPositionTicks, ct).ConfigureAwait(false);
                if (seekTo is null && !syncQueue)
                {
                    return;
                }

                // Clients need a moment to get their player going before they act on commands.
                var delay = TimeSpan.FromSeconds(Math.Max(0, Plugin.Instance?.Configuration.ResumeSeekDelaySeconds ?? 3));
                await Task.Delay(delay, ct).ConfigureAwait(false);

                var session = e.Session;
                if (session.NowPlayingItem is null || !session.NowPlayingItem.Id.Equals(episode.Id))
                {
                    return;
                }

                if (!session.SupportsRemoteControl)
                {
                    _logger.LogInformation("{Client} doesn't support remote control, can't resume or sync {Episode}", session.Client, episode.Name);
                    return;
                }

                // By now the filter has recorded the queue from the start report, if the player sent one.
                var playerQueue = _reportedQueues.Get(session.Id, episode.Id) ?? continued;
                if (syncQueue
                    && playerQueue is not null
                    && await SyncQueueAsync(session, user, episode, playerQueue, seekTo, ct).ConfigureAwait(false))
                {
                    return;
                }

                if (seekTo is long ticks)
                {
                    await SeekAsync(session, episode, ticks, ct).ConfigureAwait(false);
                }
            });
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not Episode episode)
        {
            return;
        }

        if (e.Session is not null)
        {
            _lastStops[e.Session.Id] = new LastStop(episode.Id, DateTime.UtcNow);
            _reportedQueues.Remove(e.Session.Id);
        }

        foreach (var user in e.Users)
        {
            if (_shuffleService.Handles(user, episode))
            {
                Run(ct => _shuffleService.OnPlaybackStoppedAsync(user, episode, e.PlayedToCompletion, ct));
            }
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        _lastStops.TryRemove(e.SessionInfo.Id, out _);
        _reportedQueues.Remove(e.SessionInfo.Id);
    }

    private void OnCollectionChanged(object? sender, CollectionModifiedEventArgs e)
    {
        Run(ct => _shuffleService.RefreshAllAsync(false, ct));
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        Run(ct => _shuffleService.RefreshAllAsync(false, ct));
    }

    /// <summary>
    /// If the session just moved on to <paramref name="episode"/> by itself (the episode before it in the
    /// player's queue ended or was skipped), returns the player's queue. This is the fallback for players
    /// that don't send their queue when playback starts: Jellyfin keeps the queue sent when playback
    /// stops, and a player that moved on is still playing that queue.
    /// </summary>
    private PlayQueuePosition? GetContinuedQueue(SessionInfo? session, Episode episode)
    {
        if (session is null
            || !_lastStops.TryGetValue(session.Id, out var lastStop)
            || DateTime.UtcNow - lastStop.At > _continueWindow)
        {
            return null;
        }

        var queue = session.NowPlayingQueue?.Select(q => q.Id).ToList() ?? [];
        for (var i = 0; i < queue.Count - 1; i++)
        {
            if (queue[i].Equals(lastStop.EpisodeId) && queue[i + 1].Equals(episode.Id))
            {
                return new PlayQueuePosition(queue, i + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Makes the player's queue match the current playlist. Returns whether playback was restarted, in
    /// which case the restart already went to <paramref name="seekTo"/>.
    /// </summary>
    private async Task<bool> SyncQueueAsync(
        SessionInfo session,
        User user,
        Episode episode,
        PlayQueuePosition playerQueue,
        long? seekTo,
        CancellationToken cancellationToken)
    {
        if (!user.Id.Equals(session.UserId)
            || _syncPlayManager.IsUserActive(user.Id)
            || !_shuffleService.IsShuffleQueue(playerQueue.ItemIds))
        {
            return false;
        }

        var playlist = await _shuffleService.GetPlaylistAsync(user, cancellationToken).ConfigureAwait(false);

        // An episode that's no longer in the playlist still gets to finish. The one the player moved on
        // from is left out: it was skipped.
        var previous = _lastStops.TryGetValue(session.Id, out var lastStop) && DateTime.UtcNow - lastStop.At <= _continueWindow
            ? lastStop.EpisodeId
            : Guid.Empty;
        var index = playlist.IndexOf(episode.Id);
        var upcoming = index >= 0
            ? playlist.Skip(index).ToList()
            : playlist.Where(id => !id.Equals(previous)).Prepend(episode.Id).ToList();

        // Jellyfin turns a one-episode play command into "the rest of that show", so never send one.
        if (upcoming.Count < 2)
        {
            return false;
        }

        var playerUpcoming = playerQueue.Upcoming;
        if (playerUpcoming.Count <= upcoming.Count && upcoming.Take(playerUpcoming.Count).SequenceEqual(playerUpcoming))
        {
            // Up to date, apart from episodes added to the end of the playlist since.
            var missing = upcoming.Skip(playerUpcoming.Count).ToArray();
            if (missing.Length >= 2)
            {
                await _sessionManager.SendPlayCommand(
                    null!,
                    session.Id,
                    new PlayRequest { ItemIds = missing, PlayCommand = PlayCommand.PlayLast },
                    cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        // Starting at the top of a copy of the playlist means Play was pressed on it, so start where the
        // playlist starts. Picking an episode further down keeps that episode.
        if (playerQueue.Index == 0
            && playlist.Count >= 2
            && !playlist[0].Equals(episode.Id)
            && IsCopyOf(playerQueue.ItemIds, playlist))
        {
            _logger.LogInformation(
                "{Client} started {Episode} from an out-of-date or re-sorted copy of the Smart Shuffle playlist; starting the current playlist instead",
                session.Client,
                episode.Name);
            await _sessionManager.SendPlayCommand(
                null!,
                session.Id,
                new PlayRequest { ItemIds = playlist.ToArray(), PlayCommand = PlayCommand.PlayNow, StartIndex = 0 },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        _logger.LogInformation(
            "Replacing out-of-date Smart Shuffle queue on {Client} ({PlayerCount} items) with the current playlist ({Count} items)",
            session.Client,
            playerUpcoming.Count,
            upcoming.Count);
        await _sessionManager.SendPlayCommand(
            null!,
            session.Id,
            new PlayRequest
            {
                ItemIds = upcoming.ToArray(),
                PlayCommand = PlayCommand.PlayNow,
                StartIndex = 0,
                StartPositionTicks = seekTo ?? session.PlayState?.PositionTicks
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether a queue is mostly the same episodes as the playlist, however it's
    /// ordered. A queue from some other list of the same shows won't be.
    /// </summary>
    private static bool IsCopyOf(IReadOnlyList<Guid> queue, List<Guid> playlist)
    {
        var inPlaylist = playlist.ToHashSet();
        return queue.Count(inPlaylist.Contains) * 2 >= Math.Max(queue.Count, playlist.Count);
    }

    /// <summary>
    /// Asks the client to jump to the saved position.
    /// </summary>
    private async Task SeekAsync(SessionInfo session, Episode episode, long ticks, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resuming {Episode} at {Position} on {Client}", episode.Name, TimeSpan.FromTicks(ticks), session.Client);
        await _sessionManager.SendPlaystateCommand(
            null!,
            session.Id,
            new PlaystateRequest { Command = PlaystateCommand.Seek, SeekPositionTicks = ticks },
            cancellationToken).ConfigureAwait(false);
    }

    private void Run(Func<CancellationToken, Task> work)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await work(_stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Server shutting down.
                }
#pragma warning disable CA1031 // Event handlers must not throw
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "Smart Shuffle failed handling an event");
                }
            },
            _stopping.Token);
    }

    private sealed record LastStop(Guid EpisodeId, DateTime At);
}
