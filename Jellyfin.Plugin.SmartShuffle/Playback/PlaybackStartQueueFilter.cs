using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.SmartShuffle.Playback;

/// <summary>
/// Picks the play queue out of playback start reports (<c>POST /Sessions/Playing</c>) before Jellyfin drops it.
/// </summary>
public sealed class PlaybackStartQueueFilter : IAsyncActionFilter
{
    private readonly ReportedQueues _reportedQueues;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartQueueFilter"/> class.
    /// </summary>
    /// <param name="reportedQueues">Where to record the queues.</param>
    public PlaybackStartQueueFilter(ReportedQueues reportedQueues)
    {
        _reportedQueues = reportedQueues;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var info = context.ActionArguments.Values.OfType<PlaybackStartInfo>().FirstOrDefault();
        var executed = await next().ConfigureAwait(false);

        // The controller fills in the session id while handling the request.
        if (info is not null && executed.Exception is null)
        {
            _reportedQueues.Record(info);
        }
    }
}
