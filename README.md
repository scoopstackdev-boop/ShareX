<h3 align="center">ShareX with a replay buffer</h3>
<p align="center">Fork of <a href="https://github.com/ShareX/ShareX">ShareX</a> 20.2.0. Not affiliated with the ShareX team.</p>
<br>

## Download
* Latest release: [Releases](../../releases/latest)
* Installs over an existing ShareX install. Settings are kept.

## Changes
* Replay buffer
  * Saves the last 30 seconds to 1 hour of your screen on a hotkey
  * Capture a monitor, an application, the active window or a region
  * Quality presets, or custom codec (H.264, HEVC, AV1), bitrate and preset
  * Desktop audio and microphone, mixed or on separate tracks
  * Saved clips go through after capture tasks, so uploading works as normal
  * Encodes on the GPU (NVIDIA, AMD or Intel), frames stay on the GPU. Falls back to CPU encoding
  * Clips can upload to any ShareX destination or custom uploader, separate from screenshots
* Screen recording
  * Restart button and hotkey
  * Mute button and hotkey
* Update check points to this repository instead of the official one

## Hotkeys
| Action | Default |
| --- | --- |
| Save replay | Ctrl + Alt + F10 |
| Start/Stop replay buffer | Ctrl + Alt + Shift + F10 |
| Replay buffer settings | None |
| Restart screen recording | None |
| Mute/Unmute screen recording audio | None |

Hotkeys can be changed in Hotkey settings. Replay buffer settings are also in the tray menu.

## Requirements
* Windows 10 or 11, x64
* NVIDIA, AMD or Intel GPU recommended for the replay buffer, CPU encoding works without one
* Desktop audio needs a loopback device (virtual-audio-capturer or Stereo Mix)

## Known issues
* Exclusive fullscreen games can record black with application capture
* Clips are cut on 2 second boundaries

## Building
```
dotnet restore --runtime win-x64 ShareX.sln
dotnet build --no-restore --configuration Release -p:Platform=x64 --self-contained true ShareX.sln
```
Requires the .NET 9 SDK.

## License
GPL v3, same as [ShareX](https://github.com/ShareX/ShareX). See [LICENSE.txt](LICENSE.txt).
