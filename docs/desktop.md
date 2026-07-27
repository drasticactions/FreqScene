# Desktop

## First Run

Running the desktop app for the first time, you should see the default projectM idle animation. This plays when no visualization is currently running, and can be used to verify your setup is working correctly.

To get set up, we first need to add presets.

## Playlist

The playlist is for adding `.milk` and texture files. FreqScene runs on projectM, and can support whatever MilkDrop visualizations it supports. You can find examples on its [repo page](https://github.com/projectM-visualizer/projectm#presets) or elsewhere on the web.

### Playing and organizing

| Action | How |
| --- | --- |
| Play a preset now | Double-click it, press **Space**, or right-click → Play |
| Jump to what's playing | **Now Playing** button, or **Ctrl+L** |
| Remove | Select and press **Delete**, or use the Remove button |
| Reorder | Drag rows, use the **▲ ▼** buttons, or **Alt+Up / Alt+Down** |
| Move to the ends | Right-click → Move to Top / Move to Bottom |
| Sort | **Sort by Name** or **Sort by Path** |
| Find something | Type in the **Search** box |

You can also multi-select to delete presets.

### Playback controls

- **◀ Prev / Next ▶** step through the playlist.
- **Shuffle** picks the next preset at random instead of in order.
- **Lock preset** locks the currently playing preset.
- **Duration** are the seconds before advancing to the next preset. The default is 30.
- **Gain** is how hard the audio drives the visuals, from 0 to 4.

By default, the playlist will advance automatically. If you want to stick to one preset, turn on **Lock preset**.

### Textures

Some presets reference external image files. If a preset pack came with a `textures` folder, add it
here and those presets will render as intended instead of falling back to flat colors.

## Audio

This lists the available audio sources that can be used to drive the visualization. Depending on your platform, you may need to install additional software to be able to listen to and play back your system audio. View [Troubleshooting](troubleshooting.md) for more information.

## Display modes

**Window**: an ordinary resizable window.

**Wallpaper**: the visualization sits behind your icons and windows, replacing your wallpaper. Turning on **Wallpaper Transparency** will blend the visualization in with the wallpaper underneath.

**Overlay**: similar to **Wallpaper**, but the visualization floats above your other windows. Mouse clicks are passed through to whatever is underneath.

Window mode is available on all three platforms. Wallpaper and Overlay are available on macOS and Linux.

> [!IMPORTANT]
> On Linux, the Wallpaper and Overlay modes depend on your Wayland Compositor supporting `layer-shell`. If it doesn't support it, only Window mode will be available. X11 only supports Window mode.

