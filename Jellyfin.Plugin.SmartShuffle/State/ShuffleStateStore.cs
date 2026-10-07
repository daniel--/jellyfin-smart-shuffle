using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartShuffle.State;

/// <summary>
/// Loads and saves <see cref="ShuffleState"/> as JSON in the plugin's data folder.
/// All access goes through <see cref="UseAsync{T}"/>, which serializes readers and writers.
/// </summary>
public sealed class ShuffleStateStore : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ShuffleStateStore> _logger;
    private ShuffleState? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShuffleStateStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ShuffleStateStore(ILogger<ShuffleStateStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(
        Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin not initialized"),
        "state.json");

    /// <summary>
    /// Runs <paramref name="action"/> with exclusive access to the state, then saves it.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The work to do.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    public async Task<T> UseAsync<T>(Func<ShuffleState, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            var result = await action(_state).ConfigureAwait(false);
            await SaveAsync(_state, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    private async Task<ShuffleState> LoadAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return new ShuffleState();
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<ShuffleState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? new ShuffleState();
            }
        }
        catch (JsonException ex)
        {
            var backup = path + ".corrupt";
            _logger.LogError(ex, "Smart Shuffle state file is corrupt, moving it to {Backup} and starting fresh", backup);
            File.Move(path, backup, true);
            return new ShuffleState();
        }
    }

    private static async Task SaveAsync(ShuffleState state, CancellationToken cancellationToken)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file first so a crash mid-write can't corrupt the state.
        var tempPath = path + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, true);
    }
}
