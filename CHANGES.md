## Changes

* Fixed watermark missing on region recordings: the video size was misread with current FFmpeg, so the watermark was placed as if the video was 1920x1080
* Fixed the same size misread in ShareX's video tools (thumbnailer, converter)
* Watermark size scales with the recorded area, so wide strips and tall windows stay in proportion
* New Placement setting: inside the recording (always visible) or fixed on screen (only shows when that spot is recorded)
* Replay clips appear in the folder only once they are finished, watermark included

Installs over an existing ShareX install. Settings are kept.
