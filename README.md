# Jellyfin Smart Shuffle

A Jellyfin 12.2 plugin that shuffles a set of TV shows while keeping each show's episodes in order.

- Shows come from a collection (default name **Smart Shuffle**).
- Each enabled user gets a private **Smart Shuffle** playlist: shows are interleaved randomly,
  episodes within a show always play in order.
- Progress is tracked by the plugin, not by Jellyfin's played status. Watched episodes stay
  watched, and the shows stay out of **Next Up** and **Continue Watching**.
- Stopping partway through an episode saves the position. Next time the episode starts, the
  plugin sends the client a seek command to jump back to it.

## How it works

| Piece | File |
|---|---|
| Per-user progress, upcoming show order, saved positions | `State/` (stored as `state.json` in the plugin's data folder) |
| Queue building and playlist publishing | `ShuffleService.cs` |
| Playback, collection and config event hooks; resume seek | `PlaybackListener.cs` |
| Scheduled tasks: refresh (startup + daily 04:00), reshuffle (manual) | `Tasks/` |
| Dashboard settings | `Configuration/` |

The upcoming order is stored as a list of **show slots**, not episodes. Each slot's episode is
derived from that show's progress, so episode order within a show can't break, even if you watch
out of order.

**On playback start:** Jellyfin has already incremented the play count. For a watched episode it
leaves `LastPlayedDate` alone, so the plugin snapshots the original values. If a position is saved,
it seeks there after `ResumeSeekDelaySeconds`.

**On playback stop:**
1. If finished, the show advances and its first slot is consumed. If not, Jellyfin's saved position
   is copied into the plugin's state.
2. The episode's user data is restored: position cleared, play count and last played date put back.
3. The playlist is refreshed.

Jellyfin 12.2 never clears `Played` during playback (see `SessionManager.OnPlaybackStart` and
`UserDataManager.UpdatePlayState`), so watched episodes stay watched without the plugin touching it.

## Build

Needs the .NET 10 SDK, or Docker:

```sh
docker run --rm -v "$PWD":/src -w /src -u $(id -u):$(id -g) -e HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release
```

## Install

### From the plugin repository

1. In Dashboard → Plugins → Repositories, add this repository URL:
   ```
   https://raw.githubusercontent.com/daniel--/jellyfin-smart-shuffle/gh-pages/manifest.json
   ```
2. Install **Smart Shuffle** from the catalog, then restart Jellyfin.

### Manually

Copy `Jellyfin.Plugin.SmartShuffle/bin/Release/net10.0/Jellyfin.Plugin.SmartShuffle.dll` to
`<jellyfin data dir>/plugins/SmartShuffle/`, then restart Jellyfin.

### Setup

1. Create a collection called **Smart Shuffle** and add the shows.
2. Go to Dashboard → Plugins → Smart Shuffle, tick your user, and save. Saving builds the playlists.

## Releasing

Publish a GitHub release with a tag like `v0.2.0` (or `v0.2.0.1`). The `Release` workflow then:

1. Builds the plugin zip with [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager),
   using the release notes as the changelog.
2. Attaches the zip to the release.
3. Adds the new version to `manifest.json` on the `gh-pages` branch.

Pre-releases get the zip attached but are not added to the manifest. When you move to a new
Jellyfin version, bump `targetAbi` in `build.yaml` and the package versions in the `.csproj`.

## Things to verify on a real server

- Play a shuffle episode to the end. Afterwards it should still show as watched, and the show should
  not appear in Next Up. The playlist should advance.
- Stop halfway through. The episode should not appear in Continue Watching. Starting the playlist
  again should jump to where you stopped. If it doesn't, try a longer resume delay; some clients
  don't support remote seek at all.
- While an episode is playing, Jellyfin's progress reports put it in Continue Watching temporarily.
  It disappears when playback stops.

## Known limitations

- Resume relies on remote control. Clients without it start the episode from the beginning.
- For episodes with multiple versions, the play count and date are only restored on the main
  version; resume positions are cleared on all versions.
- Playlist edits made by hand are overwritten on the next refresh.
