# Troubleshooting

## Nothing appears, or the window is black

**On Windows**, the usual cause is a missing
[Microsoft Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist), causing projectM to fail to load. Install it and restart.

## macOS won't open the app

Release builds aren't notarized. Clear the quarantine flag:

```sh
xattr -rd com.apple.quarantine /path/to/FreqScene.app
```

## The visuals don't react to my music

1. Switch to the **Synthetic** source to debug. If the visualization pulses, FreqScene should be fine and the problem is which device it's listening to.
2. You may need to change the output device you're monitoring, or use third-party software to mix sources together.
   - **macOS**: Install
     [BlackHole](https://github.com/existentialaudio/blackhole) and set up a
     [Multi-Output Device](https://github.com/ExistentialAudio/BlackHole/wiki/Multi-Output-Device).
   - **Windows**: This depends on your chipset. "Stereo Mix" if your driver has it, or a virtual audio cable.
   - **Linux**: the `.monitor` device matching your output.
3. If the visuals move but barely, raise **Gain**. Also check what the visualization is tuned to check for.

## My presets don't look right

Some presets need texture images that aren't part of the preset file. If a pack came with a `textures` folder, add it in the playlist window's **Textures** tab.

## Performance, Battery Issues

This can depend on many factors.

- Some MilkDrop visualizations are **intense**. Depending on what they're doing, you may hit a bottleneck of the CPU, GPU, or both.
- Depending on your setup, you may be running the visualization through a software renderer.

If you want improve performance or help lower the total load on a given system, you can:

- Lower **Resolution** to 75% or 50%.
- Cap **Frame Rate** at 30 FPS.

## Log files

If you need to report a problem, the logs are in a `logs` folder inside the data folder above. Each program writes its own file, and a few runs are kept.

For more detail, start the program with `--log-level debug`:

```sh
FreqScene --log-level debug
freqscene-kiosk --verbose
```
