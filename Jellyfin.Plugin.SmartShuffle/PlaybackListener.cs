using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
    private readonly ISessionManager _sessionManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ShuffleService _shuffleService;
    private readonly ILogger<PlaybackListener> _logger;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackListener"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="shuffleService">The shuffle service.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackListener(
        ISessionManager sessionManager,
        ICollectionManager collectionManager,
        ShuffleService shuffleService,
        ILogger<PlaybackListener> logger)
    {
        _sessionManager = sessionManager;
        _collectionManager = collectionManager;
        _shuffleService = shuffleService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
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

        foreach (var user in e.Users)
        {
            if (!_shuffleService.Handles(user, episode))
            {
                continue;
            }

            Run(async ct =>
            {
                var seekTo = await _shuffleService.OnPlaybackStartAsync(user, episode, e.PlaybackPositionTicks, ct).ConfigureAwait(false);
                if (seekTo is long ticks)
                {
                    await SeekAsync(e.Session, episode, ticks, ct).ConfigureAwait(false);
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

        foreach (var user in e.Users)
        {
            if (_shuffleService.Handles(user, episode))
            {
                Run(ct => _shuffleService.OnPlaybackStoppedAsync(user, episode, e.PlayedToCompletion, ct));
            }
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
    /// Asks the client to jump to the saved position. Clients need a moment to get their player going,
    /// and not every client supports remote seeking.
    /// </summary>
    private async Task SeekAsync(SessionInfo session, Episode episode, long ticks, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0, Plugin.Instance?.Configuration.ResumeSeekDelaySeconds ?? 3));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        if (session.NowPlayingItem is null || !session.NowPlayingItem.Id.Equals(episode.Id))
        {
            return;
        }

        if (!session.SupportsRemoteControl)
        {
            _logger.LogInformation("{Client} doesn't support remote control, can't resume {Episode}", session.Client, episode.Name);
            return;
        }

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
}
