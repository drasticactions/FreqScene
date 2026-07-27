# Remote Viewing

The FreqScene Desktop and Controller apps can send visualization data to other instances.

| Can host
| --- |
| [Desktop app](desktop.md) |
| [Controller](controller.md) |

| Can view |
| --- |
| [Desktop app](desktop.md) |
| [Kiosk](kiosk.md) (via `--connect`) |
| [iOS / tvOS / Android](ios-tvos-android.md) |

# How it works TL;DR

The host sends the PCM byte stream to the remote client. That is then fed through the visualization so it's rendered on device. While this means it's not literally the same visualization on all devices, it's the same process for generating it.

> [!IMPORTANT]
> The audio data is sent over plain HTTP sockets (since setting up self-signed certificates should be either overly complex or not possible on some device platforms). You may not want to run your business meetings through this in a coffee shop.

## Setting up the host

**Desktop app**: Go to the tray menu → **Remote**:

- **Allow Remote Connections**: turns the server on. Off by default.
- **Broadcast on Local Network**: lets viewers find this computer automatically through mDNS. Leaving this off you should still be able connect to the computer, but you'll need the address.

The submenu also shows how many devices are currently connected.

**Kiosk**: the server is on by default. Add `--pair` at startup to print a PIN immediately.

**Controller**: Click **Remote** → **Toggle Remote Connections**.

## Pairing a device

You need to pair devices in order to send and recieve data.

| Host | Where |
| --- | --- |
| Desktop app | Tray → **Remote → Pair a Device…** |
| Controller | **Remote → Pair a Device…** |
| Kiosk | Start with `--pair`, or press `p` |

You'll get a pin on the device, which you then input on the host. The visualizations should then start. 

## Connecting a viewer

**By discovery**: The client apps list the hosts they can find.

- Desktop: **Remote** → **Connect to Host**, then the host's name.
- iOS/tvOS and Android: From the app's opening screen.
- Kiosk: `--connect "<name from --list-servers>"`.

**By address**:

- Desktop: **Remote → Connect to Host → Connect to Address…**
- iOS/tvOS and Android: **Connect by address…**
- Kiosk: `--connect 192.168.x.x`

## Managing paired devices

**Desktop:** tray → **Remote → Paired Devices** lists everything paired. You can then click **Forget This Device** to disconnect it immediately and revokes its access. It'll have to pair again with a fresh PIN.

**Controller:** **Remote → Paired Devices…**, then **Forget Selected**.