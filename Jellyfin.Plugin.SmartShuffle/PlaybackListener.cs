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
    // How many times a player is asked to jump to the saved position before giving up.
    private const int MaxSeekAttempts = 3;

    // Players start the next item in their queue within a second or so of stopping the last one.
    private static readonly TimeSpan _continueWindow = TimeSpan.FromSeconds(30);

    // How long to wait for a player to report that it's actually playing before sending commands anyway.
    private static readonly TimeSpan _playingTimeout = TimeSpan.FromSeconds(30);

    // How long after a seek command the player's progress reports are checked against it.
    private static readonly TimeSpan _seekCheckWindow = TimeSpan.FromSeconds(60);

    // Progress reports this soon after a seek command can still carry the position from before it.
    private static readonly TimeSpan _seekSettleTime = TimeSpan.FromSeconds(2);

    // A player this far short of the saved position didn't act on the seek.
    private static readonly long _seekBehindTolerance = TimeSpan.FromSeconds(10).Ticks;

    // A player has to play on this far past the saved position to show that it got there. Some players
    // report the position they were asked for, not where they really are, until playback picks up again.
    private static readonly long _seekConfirmMargin = TimeSpan.FromSeconds(1).Ticks;

    private readonly ISessionManager _sessionManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ShuffleService _shuffleService;
    private readonly ReportedQueues _reportedQueues;
    private readonly ILogger<PlaybackListener> _logger;
    private readonly CancellationTokenSource _stopping = new();

    // The last episode each session stopped, by session id.
    private readonly ConcurrentDictionary<string, LastStop> _lastStops = new();

    // Sessions that have started an episode but haven't reported progress on it yet, by session id.
    private readonly ConcurrentDictionary<string, PendingStart> _pendingStarts = new();

    // Sessions that were asked to jump to a saved position and haven't shown that they did, by session id.
    private readonly ConcurrentDictionary<string, PendingSeek> _pendingSeeks = new();

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
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
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
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
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

        // A seek still being checked belonged to whatever the session was playing before.
        _pendingSeeks.TryRemove(e.Session.Id, out _);

        // Read now: the session's queue is replaced when this episode stops.
        var continued = GetContinuedQueue(e.Session, episode);
        var syncQueue = Plugin.Instance?.Configuration.KeepPlayerQueueInSync != false;

        // Registered now, before any work is queued, so no progress report is missed.
        PendingStart? pending = null;

        foreach (var user in e.Users)
        {
            if (!_shuffleService.Handles(user, episode))
            {
                continue;
            }

            pending ??= AddPendingStart(e.Session, episode, e.PlaybackPositionTicks);
            var playing = pending;

            Run(async ct =>
            {
                var seekTo = await _shuffleService.OnPlaybackStartAsync(user, episode, e.PlaybackPositionTicks, ct).ConfigureAwait(false);
                if (seekTo is null && !syncQueue)
                {
                    return;
                }

                // Clients ignore commands sent while their player is still starting up.
                if (!await WaitUntilPlayingAsync(e.Session, episode, playing, ct).ConfigureAwait(false))
                {
                    return;
                }

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
                    // Checked against the player's progress reports from here on, and sent again if needed.
                    _pendingSeeks[session.Id] = new PendingSeek(episode.Id, ticks, 1, DateTime.UtcNow);
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
            if (_pendingStarts.TryGetValue(e.Session.Id, out var pending) && pending.EpisodeId.Equals(episode.Id))
            {
                RemovePendingStart(e.Session.Id, pending, false);
            }

            if (_pendingSeeks.TryGetValue(e.Session.Id, out var seek) && seek.EpisodeId.Equals(episode.Id))
            {
                _pendingSeeks.TryRemove(new KeyValuePair<string, PendingSeek>(e.Session.Id, seek));
            }
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
        _pendingSeeks.TryRemove(e.SessionInfo.Id, out _);
        if (_pendingStarts.TryGetValue(e.SessionInfo.Id, out var pending))
        {
            RemovePendingStart(e.SessionInfo.Id, pending, false);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        // This fires every few seconds for every playing session, so bail out early.
        if (e.Session is null || e.Item is null)
        {
            return;
        }

        // Players can report progress while they're still buffering; the position moving on from where it
        // started is what shows the player is really going.
        if (_pendingStarts.TryGetValue(e.Session.Id, out var pending)
            && pending.EpisodeId.Equals(e.Item.Id)
            && !e.IsPaused
            && (e.PlaybackPositionTicks is not long position || position > pending.StartTicks))
        {
            RemovePendingStart(e.Session.Id, pending, true);
        }

        if (_pendingSeeks.TryGetValue(e.Session.Id, out var seek))
        {
            CheckSeek(e, seek);
        }
    }

    /// <summary>
    /// Checks a progress report against the position the player was asked to jump to, and asks again if
    /// the player is playing somewhere well short of it. Jellyfin for Android TV, for one, restarts the
    /// episode from the beginning when a seek reaches it before its player can seek.
    /// </summary>
    private void CheckSeek(PlaybackProgressEventArgs e, PendingSeek seek)
    {
        var session = e.Session;
        var pendingSeek = new KeyValuePair<string, PendingSeek>(session.Id, seek);
        var now = DateTime.UtcNow;
        if (e.Item is not Episode episode
            || !seek.EpisodeId.Equals(episode.Id)
            || now - seek.SentAt > _seekCheckWindow)
        {
            _pendingSeeks.TryRemove(pendingSeek);
            return;
        }

        if (e.IsPaused
            || e.PlaybackPositionTicks is not long position
            || now - seek.SentAt < _seekSettleTime)
        {
            return;
        }

        if (position >= seek.TargetTicks + _seekConfirmMargin)
        {
            _pendingSeeks.TryRemove(pendingSeek);
            return;
        }

        // Only a player that's moving can be told apart from one that's still starting up again.
        if (position <= 0 || position >= seek.TargetTicks - _seekBehindTolerance)
        {
            return;
        }

        if (seek.Attempts >= MaxSeekAttempts)
        {
            if (_pendingSeeks.TryRemove(pendingSeek))
            {
                _logger.LogWarning(
                    "{Client} is at {Position} in {Episode} after being asked {Attempts} times to resume at {Target}, giving up",
                    session.Client,
                    TimeSpan.FromTicks(position),
                    episode.Name,
                    seek.Attempts,
                    TimeSpan.FromTicks(seek.TargetTicks));
            }

            return;
        }

        // Whichever report gets its update in sends the command, so reports arriving together send one.
        var retry = seek with { Attempts = seek.Attempts + 1, SentAt = now };
        if (!_pendingSeeks.TryUpdate(session.Id, retry, seek))
        {
            return;
        }

        _logger.LogInformation(
            "{Client} is at {Position} in {Episode} after being asked to resume at {Target}, asking again (attempt {Attempt} of {MaxAttempts})",
            session.Client,
            TimeSpan.FromTicks(position),
            episode.Name,
            TimeSpan.FromTicks(seek.TargetTicks),
            retry.Attempts,
            MaxSeekAttempts);
        Run(ct => SeekAsync(session, episode, seek.TargetTicks, ct));
    }

    private PendingStart AddPendingStart(SessionInfo session, Episode episode, long? startTicks)
    {
        var pending = new PendingStart(
            episode.Id,
            startTicks ?? 0,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pendingStarts.AddOrUpdate(
            session.Id,
            pending,
            (_, superseded) =>
            {
                superseded.Playing.TrySetResult(false);
                return pending;
            });
        return pending;
    }

    private void RemovePendingStart(string sessionId, PendingStart pending, bool playing)
    {
        _pendingStarts.TryRemove(new KeyValuePair<string, PendingStart>(sessionId, pending));
        pending.Playing.TrySetResult(playing);
    }

    /// <summary>
    /// Waits for the player to report that the episode is actually playing. Returns false if it stopped or
    /// moved on to something else first.
    /// </summary>
    private async Task<bool> WaitUntilPlayingAsync(SessionInfo session, Episode episode, PendingStart pending, CancellationToken cancellationToken)
    {
        try
        {
            return await pending.Playing.Task.WaitAsync(_playingTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Not every player reports progress; the caller still checks that it's on this episode.
            _logger.LogDebug("{Client} hasn't reported progress on {Episode}, sending commands anyway", session.Client, episode.Name);
            RemovePendingStart(session.Id, pending, true);
            return true;
        }
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

        // The replacement queue is the whole playlist started at this episode, so it still holds more than
        // one episode when this is the last one. Jellyfin turns a one-episode play command into "the rest of
        // that show", so never send one.
        var replacement = index >= 0 ? playlist : upcoming;
        var startIndex = index >= 0 ? index : 0;
        if (replacement.Count < 2)
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
                ItemIds = replacement.ToArray(),
                PlayCommand = PlayCommand.PlayNow,
                StartIndex = startIndex,
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

    private sealed record PendingSeek(Guid EpisodeId, long TargetTicks, int Attempts, DateTime SentAt);

    private sealed record PendingStart(Guid EpisodeId, long StartTicks, TaskCompletionSource<bool> Playing);
}
