using System;
using System.Collections.Concurrent;
using System.Linq;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.SmartShuffle.Playback;

/// <summary>
/// The play queue each player sent with its last playback start report. Jellyfin itself drops the
/// queue from start reports and only keeps the one sent when playback stops.
/// </summary>
public sealed class ReportedQueues
{
    private readonly ConcurrentDictionary<string, Reported> _bySession = new();

    /// <summary>
    /// Records the queue from a playback start report.
    /// </summary>
    /// <param name="info">The report, after Jellyfin has filled in its session id.</param>
    public void Record(PlaybackStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrEmpty(info.SessionId) || info.NowPlayingQueue is not { Length: > 0 } queue)
        {
            return;
        }

        var ids = queue.Select(q => q.Id).ToList();
        var index = string.IsNullOrEmpty(info.PlaylistItemId)
            ? -1
            : Array.FindIndex(queue, q => string.Equals(q.PlaylistItemId, info.PlaylistItemId, StringComparison.Ordinal));
        if (index < 0)
        {
            index = ids.IndexOf(info.ItemId);
        }

        if (index >= 0)
        {
            _bySession[info.SessionId] = new Reported(info.ItemId, new PlayQueuePosition(ids, index));
        }
    }

    /// <summary>
    /// Gets the queue the session reported when it started playing <paramref name="itemId"/>.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="itemId">The item now playing.</param>
    /// <returns>The queue, or null if the session didn't report one for this item.</returns>
    public PlayQueuePosition? Get(string sessionId, Guid itemId)
    {
        return _bySession.TryGetValue(sessionId, out var reported) && reported.ItemId.Equals(itemId)
            ? reported.Queue
            : null;
    }

    /// <summary>
    /// Forgets a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Remove(string sessionId)
    {
        _bySession.TryRemove(sessionId, out _);
    }

    private sealed record Reported(Guid ItemId, PlayQueuePosition Queue);
}
