## Changes

* Replay buffer works on AMD (AMF) and Intel (Quick Sync) GPUs, detected automatically
* CPU encoding (x264) when no GPU encoder works
* If the GPU capture path fails to start, frames are copied through system memory instead
* Upload setting for clips: same as ShareX, off, or a chosen destination or custom uploader
* Encoder choice under Advanced

Installs over an existing ShareX install. Settings are kept.
