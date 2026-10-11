# App Volume Mixer – Macro Deck 3 plugin

Adds an **App Volume** widget to Macro Deck 3: a slider tile bound to one application in the
Windows Volume Mixer (Discord, Spotify, your game, a browser, system sounds …).

- Drag the slider to set that app's volume. Every widget bound to the same app moves together.
- Changes made in Windows (or by the app itself) show up on the tile within half a second.
- **Double-tap** the slider to mute / unmute. Moving a muted slider unmutes it.
- An app that isn't running shows *Not running*; the tile picks it up as soon as the app plays audio.
- Works across all playback devices (e.g. Discord on your headset, the game on speakers).
- Options per widget: vertical/horizontal, absolute or relative touch, show name, show %, and the
  standard label / colours / border appearance settings.
- English and German UI.

## Requirements

- Windows 10/11 x64, Macro Deck 3 (beta).
- To build: the .NET 10 SDK.

## Build

```powershell
.\build.ps1
```

This installs the `macrodeck-plugin` CLI if needed, builds, packs and validates
`artifacts\com.yanick.app-volume-mixer-<version>-win-x64.macroDeckPlugin` (file name may vary by CLI version).

Manual equivalent:

```powershell
dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.0-beta.16
cd src\AppVolumeMixer
macrodeck-plugin build --output ..\..\artifacts
```

## Install

**From the Macro Deck Store** (once published): Macro Deck → **Store** → search *App Volume Mixer*.

**From a file:** download the `.macroDeckPlugin` from the [Releases](../../releases) page (or build it
yourself), then Macro Deck → **Integrations** → **Add** → **Install from file…**. A self-built package is
unsigned, so Macro Deck asks you to confirm once. Uninstall a self-built copy before installing the
Store version.

## Releasing (maintainers)

Releases go through the official Macro Deck publishing workflow (`.github/workflows/release.yml`):

1. One-time: in the [Creator Portal](https://docs.macro-deck.app/creator-portal/) create a
   *Plugin / Integration* Project with Package ID `com.yanick.app-volume-mixer`, then **Builds →
   Connect GitHub** and pick this repository.
2. Publish a GitHub release with a SemVer tag, e.g. `v1.0.0`. The tag (not `manifest.json`) sets the
   version; the release notes become the changelog.
3. When the run finishes, the build appears under **Builds** in the Portal: **Create release** →
   fill in the test report → **Add to submission** → **Submit for Review**. The Portal signs the package
   after review.

## Use

1. Edit your deck, add a widget and choose **App Volume**.
2. In the widget settings pick the application (apps currently playing audio are listed; you can also
   type a process name such as `discord` or `valorant` for an app that isn't running yet).
3. Save. Repeat for chat, music and game.

## Development

Run against the live Macro Deck with hot reload (enable *Developer Mode* in Macro Deck first):

```powershell
cd src\AppVolumeMixer
dotnet run --launch-profile "Macro Deck - Real Host"
```

Code map:

| File | What it does |
| --- | --- |
| `Audio/CoreAudioInterop.cs` | Windows Core Audio COM definitions (sessions, volume, mute) |
| `Audio/AudioMixer.cs` | Dedicated audio thread: polls sessions on all playback devices, applies volume/mute, exposes live UI state per app |
| `Widget/AppVolumeWidget.cs` | The tile (slider + name + %), the picker sample and the configuration editor |
| `PluginIntegration.cs` | Registers the widget type and serves its UI sessions |
| `Localization/Strings*.resx` | UI text (English, German) |

The widget type id `com.yanick.app-volume-mixer::app-volume` is stored in every placed widget – don't
change it, or the plugin id in `manifest.json`, after you've placed widgets.
