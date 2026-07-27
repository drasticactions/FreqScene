# Kiosk

FreqScene.Kiosk is a CLI app for Linux for running or broadcasting visualizations directly to a given host system, without an existing Wayland or X11 session needed.

## Install

```sh
tar xzf freqscene-kiosk-x.x.x-x64.tar.gz
cd freqscene-kiosk
./freqscene-kiosk
```

Your machine needs OpenGL/EGL drivers, and access to DRM and a seat. By default, it uses `seatd` to handle that. You'll also need your user to be apart of the `video` and `render` groups. If you want it render from audio on the system, you'll also need an audio source that supports OpenAL, like PulseAudio.

## Quick start

You can run it directly on the system using its audio, or by pairing it with a remote device.

```sh
# check what it can see
./freqscene-kiosk --list-outputs
./freqscene-kiosk --list-audio

# run it fullscreen on the default display with the built-in test tone,
# and print a pairing PIN so a phone can connect
./freqscene-kiosk --pair ~/presets

# run it on a specific screen at a specific mode, listening to a capture device
./freqscene-kiosk --output HDMI-A-1 --mode 1920x1080@60 --audio "Monitor of Built-in Audio" ~/presets
```

## Options

**Display**

| Option | Meaning |
| --- | --- |
| `--backend auto\|drm\|wayland` | How to draw. `auto` uses Wayland if a session is running, otherwise takes the display directly via DRM/KMS. |
| `--output <name>` | Which screen. The names come from `--list-outputs`. |
| `--mode <WxH[@Hz]>` | Video mode when driving the display directly, e.g. `1920x1080` or `1920x1080@60`. |
| `--list-outputs` | Show available screens and their supported modes (`*` marks the preferred one). |

**Audio**

| Option | Meaning |
| --- | --- |
| `--audio <name>` | `synthetic` for the built-in test tone, or a capture device name. |
| `--list-audio` | Show available audio sources. |

**Remote**

| Option | Meaning |
| --- | --- |
| `--pair` | Print a pairing PIN at startup so a device can pair right away. |
| `--port <number>` | Listen on a different port (default 39501). |
| `--no-remote` | Don't accept remote connections at all. |
| `--connect <target>` | Mirror another FreqScene machine instead of playing its own presets. Accepts `host`, `host:port`, or a name from `--list-servers`. |
| `--list-servers` | Look for FreqScene hosts on the network for a few seconds and list them. |

**Other**

| Option | Meaning |
| --- | --- |
| `--config-dir <dir>` | Keep this instance's playlist, settings, and logs in their own folder. Use it to run more than one kiosk on a machine. |
| `--verbose` / `--log-level <level>` | More diagnostic output. `--verbose` is shorthand for `--log-level debug`. |

`--connect` can't be combined with `--port` or `--pair` — a kiosk either hosts or watches, never both. While it's watching, the presets and audio come from the host, so `--audio` and any preset arguments are ignored.

## Keys

If you do have a keyboard attached:

| Key | Action |
| --- | --- |
| `n` | Next preset |
| `b` | Previous preset |
| `p` | Print a fresh pairing PIN |
| `q` | Quit |

`Ctrl+C` also exits cleanly.

## Remote control

To pair, you need the PIN. Start it with `--pair`, or pressing `p`. Then enter that PIN on the device.

You can also point a kiosk at *another* FreqScene machine with `--connect`, turning it into a second screen showing the same thing. If that host requires pairing, the kiosk asks for the PIN.

## Running it automatically at boot

You can also use the included `freqscene-kiosk.service` template for systemd if you want to run this on startup:

```sh
sudo cp -r . /opt/freqscene-kiosk
sudo cp freqscene-kiosk.service /etc/systemd/system/
sudo systemctl enable --now freqscene-kiosk
```

As shipped it runs as a dedicated `freqscene` user with the `video` and `render` groups, uses the DRM backend, and restarts if it crashes. Edit `ExecStart` in the unit file to add your own options.

```sh
systemctl status freqscene-kiosk
journalctl -u freqscene-kiosk -f
```
