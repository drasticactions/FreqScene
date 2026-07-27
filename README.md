# FreqScene

FreqScene are a set of [projectM](https://github.com/projectM-visualizer/projectm)-based music visualizer programs, written in .NET.

You can find user-focused docs in the [docs](./docs/) folder.

## CI Builds

CI Builds are unsigned. On macOS, you will need to clear the quarantine on the app bundle. You can do that by opening the terminal and running

```sh
xattr -rd com.apple.quarantine /path/to/bundle.app
```

On Windows, you will need the Microsoft Visual C++ Redistributable installed, you can get it [here](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-supported-redistributable-version). If you don't have it, the app will launch but you won't see visualizations.

Linux builds are bundled as AppImage. These require FUSE to run.

## TL;DR Setup

Once you have FreqScene set up and run for the first time, you should see the default projectM idle animation playing. To install new visualizations, download some [MilkDrop Presets](https://github.com/projectM-visualizer/projectm#presets) and add them to the playlist by right clicking on the tray icon and opening the "Playlist..." option. You can either let them switch on their own, or lock in a selection. You can switch them on the fly by selecting a row.

macOS does not provide a built-in loopback device for audio. You can install [BlackHole](https://github.com/existentialaudio/blackhole) and set up a [Multi Output Device](https://github.com/ExistentialAudio/BlackHole/wiki/Multi-Output-Device) to pipe audio through it.
