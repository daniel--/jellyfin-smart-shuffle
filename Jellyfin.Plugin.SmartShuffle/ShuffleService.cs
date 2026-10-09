using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartShuffle.Configuration;
using Jellyfin.Plugin.SmartShuffle.State;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartShuffle;

/// <summary>
/// Core Smart Shuffle logic: the per-user show order, progress tracking, and the playlist.
/// </summary>
public class ShuffleService
{
    // Only skip the resume seek if the client already started near the saved position.
    private static readonly long _alreadyResumedTolerance = TimeSpan.FromSeconds(30).Ticks;

    // Jellyfin 12.1 stamps LastPlayedDate when playback starts; a date this recent means it was overwritten.
    private static readonly TimeSpan _justStampedWindow = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ISessionManager _sessionManager;
    private readonly ShuffleStateStore _store;
    private readonly ILogger<ShuffleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShuffleService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="playlistManager">The playlist manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="store">The state store.</param>
    /// <param name="logger">The logger.</param>
    public ShuffleService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPlaylistManager playlistManager,
        ISessionManager sessionManager,
        ShuffleStateStore store,
        ILogger<ShuffleService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _sessionManager = sessionManager;
        _store = store;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether Smart Shuffle handles playback of this item for this user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item being played.</param>
    /// <returns>Whether the item is an episode of a shuffle show and the user is enabled.</returns>
    public bool Handles(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        return item is Episode episode
            && Config.EnabledUserIds.Contains(user.Id)
            && GetShuffleSeries().Any(s => s.Id.Equals(episode.SeriesId));
    }

    /// <summary>
    /// Gets a value indicating whether a player's queue looks like a copy of a Smart Shuffle playlist:
    /// only episodes of shuffle shows, from more than one show. A queue from a show's own page has one show.
    /// </summary>
    /// <param name="itemIds">The ids in the player's queue.</param>
    /// <returns>Whether the queue is a shuffle queue.</returns>
    public bool IsShuffleQueue(IEnumerable<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var shuffleSeriesIds = GetShuffleSeries().Select(s => s.Id).ToHashSet();
        var seriesInQueue = new HashSet<Guid>();
        foreach (var id in itemIds)
        {
            if (_libraryManager.GetItemById(id) is not Episode episode || !shuffleSeriesIds.Contains(episode.SeriesId))
            {
                return false;
            }

            seriesInQueue.Add(episode.SeriesId);
        }

        return seriesInQueue.Count > 1;
    }

    /// <summary>
    /// Gets the episodes currently in the user's playlist, in order.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode ids.</returns>
    public Task<List<Guid>> GetPlaylistAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _store.UseAsync(
            state => Task.FromResult(state.ForUser(user.Id).PublishedEpisodeIds.ToList()),
            cancellationToken);
    }

    /// <summary>
    /// Rebuilds the playlist of every enabled user.
    /// </summary>
    /// <param name="reshuffle">Whether to throw away the upcoming show order and pick a new one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshAllAsync(bool reshuffle, CancellationToken cancellationToken)
    {
        foreach (var userId in Config.EnabledUserIds)
        {
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                _logger.LogWarning("Smart Shuffle is enabled for unknown user {UserId}", userId);
                continue;
            }

            await _store.UseAsync(
                async state =>
                {
                    var userState = state.ForUser(user.Id);
                    if (reshuffle)
                    {
                        userState.Slots.Clear();
                    }

                    await RefreshUserAsync(user, userState).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called when a shuffle episode starts playing. Snapshots the user data Jellyfin is about to change
    /// and returns the saved position to seek to, if any. Episodes that aren't in the user's playlist were
    /// picked outside the shuffle and are left to Jellyfin.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="startPositionTicks">Where the client started playback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position to seek to, or null.</returns>
    public Task<long?> OnPlaybackStartAsync(User user, Episode episode, long? startPositionTicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(episode);

        return _store.UseAsync(
            state =>
            {
                var userState = state.ForUser(user.Id);
                if (!IsShufflePlayback(userState, episode))
                {
                    return Task.FromResult<long?>(null);
                }

                // Keep an existing snapshot: it holds the true original if an earlier stop was missed.
                if (!userState.PendingRestores.ContainsKey(episode.Id))
                {
                    var snapshot = userState.Baselines.Remove(episode.Id, out var baseline)
                        ? baseline
                        : SnapshotAfterStart(user, episode);
                    if (snapshot is not null)
                    {
                        userState.PendingRestores[episode.Id] = snapshot;
                    }
                }

                long? seekTo = null;
                if (userState.ResumePositions.TryGetValue(episode.Id, out var saved)
                    && (startPositionTicks ?? 0) < saved - _alreadyResumedTolerance)
                {
                    seekTo = saved;
                }

                return Task.FromResult(seekTo);
            },
            cancellationToken);
    }

    /// <summary>
    /// Called when a shuffle episode stops. Records progress, puts Jellyfin's user data back the way it
    /// was, and refreshes the playlist. Episodes that aren't in the user's playlist are left to Jellyfin.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="playedToCompletion">Whether Jellyfin considered the episode finished.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task OnPlaybackStoppedAsync(User user, Episode episode, bool playedToCompletion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(episode);

        return _store.UseAsync(
            async state =>
            {
                var userState = state.ForUser(user.Id);
                if (!IsShufflePlayback(userState, episode))
                {
                    return false;
                }

                if (playedToCompletion)
                {
                    userState.ResumePositions.Remove(episode.Id);
                    MarkFinished(user, userState, episode);
                }
                else
                {
                    // Jellyfin has already applied its min/max resume percentages to the saved position.
                    var position = episode.GetAllVersions()
                        .Select(v => _userDataManager.GetUserData(user, v)?.PlaybackPositionTicks ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    if (position > 0)
                    {
                        userState.ResumePositions[episode.Id] = position;
                    }
                }

                userState.PendingRestores.Remove(episode.Id, out var snapshot);
                RestoreUserData(user, episode, snapshot);

                await RefreshUserAsync(user, userState).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Whether playing <paramref name="episode"/> counts as shuffle playback: it's in the playlist, or it
    /// was when it started. Other episodes of shuffle shows were picked outside the shuffle, for example
    /// from the show's page, and don't move the show's progress.
    /// </summary>
    private static bool IsShufflePlayback(UserShuffleState userState, Episode episode)
    {
        return userState.PublishedEpisodeIds.Contains(episode.Id)
            || userState.PendingRestores.ContainsKey(episode.Id);
    }

    /// <summary>
    /// Fallback for playlist episodes that have no baseline, such as one added since the last refresh. Jellyfin has already
    /// done PlayCount++ by the time the start event fires, so that can be undone. 12.2 leaves LastPlayedDate
    /// alone for watched episodes, but 12.1 overwrites it, in which case the original is lost.
    /// </summary>
    private UserDataSnapshot? SnapshotAfterStart(User user, Episode episode)
    {
        var data = _userDataManager.GetUserData(user, episode);
        if (data is not { Played: true })
        {
            return null;
        }

        var dateWasStamped = data.LastPlayedDate > DateTime.UtcNow - _justStampedWindow;
        return new UserDataSnapshot
        {
            PlayCount = Math.Max(0, data.PlayCount - 1),
            LastPlayedDate = data.LastPlayedDate,
            KeepCurrentLastPlayedDate = dateWasStamped
        };
    }

    /// <summary>
    /// Records the untouched user data of every queued episode, so it can be put back after playback.
    /// Must run before playback starts: Jellyfin changes the data before plugins hear about it.
    /// </summary>
    private void CaptureBaselines(User user, UserShuffleState userState, List<Episode> queue)
    {
        var queued = queue.Select(e => e.Id).ToHashSet();
        foreach (var id in userState.Baselines.Keys.Where(id => !queued.Contains(id)).ToList())
        {
            userState.Baselines.Remove(id);
        }

        var playing = _sessionManager.Sessions
            .Select(s => s.NowPlayingItem?.Id)
            .OfType<Guid>()
            .ToHashSet();

        foreach (var episode in queue)
        {
            if (userState.Baselines.ContainsKey(episode.Id)
                || userState.PendingRestores.ContainsKey(episode.Id)
                || playing.Contains(episode.Id))
            {
                continue;
            }

            var data = _userDataManager.GetUserData(user, episode);
            if (data is { Played: true, PlaybackPositionTicks: 0 })
            {
                userState.Baselines[episode.Id] = new UserDataSnapshot
                {
                    PlayCount = data.PlayCount,
                    LastPlayedDate = data.LastPlayedDate
                };
            }
        }
    }

    private void MarkFinished(User user, UserShuffleState userState, Episode episode)
    {
        if (episode.Series is not Series series)
        {
            return;
        }

        var episodes = GetEpisodes(series, user);
        var index = episodes.FindIndex(e => e.Id.Equals(episode.Id));
        if (index < 0)
        {
            return;
        }

        userState.Series.TryGetValue(series.Id, out var progress);
        var next = NextIndex(progress, episodes);
        if (next >= episodes.Count && Config.LoopFinishedShows)
        {
            next = 0;
        }

        // Rewatching an episode before the current one doesn't move the show backwards.
        if (index < next)
        {
            return;
        }

        userState.Series[series.Id] = new SeriesProgress
        {
            LastFinishedEpisodeId = episode.Id,
            LastSeasonNumber = episode.ParentIndexNumber,
            LastEpisodeNumber = episode.IndexNumber
        };

        // The show's first slot has now been watched. Later slots shift to the following episodes.
        userState.Slots.Remove(series.Id);
    }

    private void RestoreUserData(User user, Episode episode, UserDataSnapshot? snapshot)
    {
        foreach (var version in episode.GetAllVersions())
        {
            var data = _userDataManager.GetUserData(user, version);
            if (data is null)
            {
                continue;
            }

            // Clearing the position keeps the episode out of Continue Watching; the plugin seeks instead.
            data.PlaybackPositionTicks = 0;

            // Restoring LastPlayedDate keeps the show out of Next Up for users with rewatching enabled.
            if (snapshot is not null && version.Id.Equals(episode.Id))
            {
                data.PlayCount = snapshot.PlayCount;
                if (!snapshot.KeepCurrentLastPlayedDate)
                {
                    data.LastPlayedDate = snapshot.LastPlayedDate;
                }
            }

            _userDataManager.SaveUserData(user, version, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
    }

    private async Task RefreshUserAsync(User user, UserShuffleState userState)
    {
        var episodesBySeries = GetShuffleSeries()
            .Where(s => s.IsVisible(user))
            .ToDictionary(s => s.Id, s => GetEpisodes(s, user));

        var queue = BuildQueue(userState, episodesBySeries);
        CaptureBaselines(user, userState, queue);
        await PublishAsync(user, userState, queue).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the slot list into episodes, drops slots that no longer resolve, and tops it up with
    /// randomly picked shows.
    /// </summary>
    private static List<Episode> BuildQueue(UserShuffleState userState, Dictionary<Guid, List<Episode>> episodesBySeries)
    {
        var config = Config;
        var queue = new List<Episode>();
        var slots = new List<Guid>();
        var cursors = new Dictionary<Guid, int>();
        var queuedPerSeries = new Dictionary<Guid, int>();
        var used = new HashSet<Guid>();

        bool TryAppend(Guid seriesId)
        {
            if (!episodesBySeries.TryGetValue(seriesId, out var episodes) || episodes.Count == 0)
            {
                return false;
            }

            if (!cursors.TryGetValue(seriesId, out var cursor))
            {
                userState.Series.TryGetValue(seriesId, out var progress);
                cursor = NextIndex(progress, episodes);
            }

            if (cursor >= episodes.Count)
            {
                if (!config.LoopFinishedShows)
                {
                    return false;
                }

                cursor %= episodes.Count;
            }

            var episode = episodes[cursor];

            // A short show looping round onto an episode that's already queued.
            if (!used.Add(episode.Id))
            {
                return false;
            }

            cursors[seriesId] = cursor + 1;
            queuedPerSeries[seriesId] = queuedPerSeries.GetValueOrDefault(seriesId) + 1;
            queue.Add(episode);
            slots.Add(seriesId);
            return true;
        }

        int Available(Guid seriesId)
        {
            var episodes = episodesBySeries[seriesId];
            var queued = queuedPerSeries.GetValueOrDefault(seriesId);
            if (config.LoopFinishedShows)
            {
                return episodes.Count - queued;
            }

            userState.Series.TryGetValue(seriesId, out var progress);
            return episodes.Count - NextIndex(progress, episodes) - queued;
        }

        foreach (var seriesId in userState.Slots.Take(config.PlaylistLength))
        {
            TryAppend(seriesId);
        }

        while (queue.Count < config.PlaylistLength)
        {
            var candidates = episodesBySeries.Keys.Where(id => Available(id) > 0).ToList();
            if (config.AvoidBackToBack && candidates.Count > 1 && slots.Count > 0)
            {
                candidates.Remove(slots[^1]);
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var picked = config.Weighting == ShowWeighting.RemainingEpisodes
                ? PickWeighted(candidates, Available)
                : candidates[NextRandom(candidates.Count)];

            if (!TryAppend(picked))
            {
                break;
            }
        }

        ReplaceAll(userState.Slots, slots);
        return queue;
    }

    private static void ReplaceAll(IList<Guid> target, IEnumerable<Guid> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static Guid PickWeighted(List<Guid> candidates, Func<Guid, int> weight)
    {
        var weights = candidates.Select(weight).ToList();
        var roll = NextRandom(weights.Sum());
        for (var i = 0; i < candidates.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0)
            {
                return candidates[i];
            }
        }

        return candidates[^1];
    }

#pragma warning disable CA5394 // Random is fine for picking shows
    private static int NextRandom(int maxExclusive) => Random.Shared.Next(maxExclusive);
#pragma warning restore CA5394

    /// <summary>
    /// The index of the episode after the last finished one. May equal the episode count when a show is done.
    /// </summary>
    private static int NextIndex(SeriesProgress? progress, List<Episode> episodes)
    {
        if (progress?.LastFinishedEpisodeId is not Guid lastId)
        {
            return 0;
        }

        var index = episodes.FindIndex(e => e.Id.Equals(lastId));
        if (index >= 0)
        {
            return index + 1;
        }

        // The episode was removed or re-added with a new id; fall back to season/episode numbers.
        var last = (progress.LastSeasonNumber ?? 0, progress.LastEpisodeNumber ?? 0);
        index = episodes.FindIndex(e => (e.ParentIndexNumber ?? 0, e.IndexNumber ?? 0).CompareTo(last) > 0);
        return index >= 0 ? index : episodes.Count;
    }

    private List<Series> GetShuffleSeries()
    {
        var collectionName = Config.CollectionName;
        var collection = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            Name = collectionName
        }).OfType<BoxSet>().FirstOrDefault();

        if (collection is null)
        {
            _logger.LogWarning("Smart Shuffle collection {CollectionName} not found", collectionName);
            return [];
        }

        return collection.GetLinkedChildren().OfType<Series>().ToList();
    }

    private static List<Episode> GetEpisodes(Series series, User user)
    {
        var includeSpecials = Config.IncludeSpecials;
        return series.GetEpisodes(user, new DtoOptions(false), false)
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem && (includeSpecials || e.ParentIndexNumber != 0))
            .ToList();
    }

    private async Task PublishAsync(User user, UserShuffleState userState, List<Episode> queue)
    {
        var config = Config;
        var ids = queue.Select(e => e.Id).ToList();

        var playlist = userState.PlaylistId is Guid playlistId
            ? _libraryManager.GetItemById(playlistId) as Playlist
            : null;

        if (playlist is null)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = config.PlaylistName,
                ItemIdList = ids,
                MediaType = MediaType.Video,
                UserId = user.Id,
                Public = false
            }).ConfigureAwait(false);

            userState.PlaylistId = Guid.Parse(result.Id);
            ReplaceAll(userState.PublishedEpisodeIds, ids);
            _logger.LogInformation("Created Smart Shuffle playlist for {UserName} with {Count} episodes", user.Username, ids.Count);
            return;
        }

        if (ids.SequenceEqual(userState.PublishedEpisodeIds)
            && playlist.LinkedChildren.Length == ids.Count
            && string.Equals(playlist.Name, config.PlaylistName, StringComparison.Ordinal))
        {
            return;
        }

        await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
        {
            Id = playlist.Id,
            UserId = user.Id,
            Name = config.PlaylistName,
            Ids = ids
        }).ConfigureAwait(false);

        ReplaceAll(userState.PublishedEpisodeIds, ids);
    }
}
