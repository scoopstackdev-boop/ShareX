#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.UploadersLib;
using System.Drawing;

namespace ShareX
{
    public enum ReplayBufferCodec
    {
        H264,
        HEVC,
        AV1
    }

    public enum ReplayBufferQuality
    {
        SmallFiles,
        Balanced,
        BestQuality,
        Custom
    }

    public enum ReplayBufferEncoder
    {
        Auto,
        NVIDIA,
        AMD,
        Intel,
        CPU
    }

    public enum ReplayBufferUpload
    {
        ShareXDefault,
        Off,
        Destination
    }

    public enum ReplayBufferCaptureMode
    {
        Monitor,
        App,
        ActiveWindow,
        Area
    }

    public class ReplayBufferSettings
    {
        // How much history is kept, in seconds
        public int BufferSeconds { get; set; } = 300;

        // Length of the clip written by the save hotkey, in seconds
        public int ClipSeconds { get; set; } = 300;

        public ReplayBufferCaptureMode CaptureMode { get; set; } = ReplayBufferCaptureMode.Monitor;

        // -1 = monitor under the cursor when the buffer starts
        public int MonitorIndex { get; set; } = -1;

        // Executable name for app capture, for example RobloxPlayerBeta.exe
        public string AppExe { get; set; } = "";

        // Screen coordinates for area capture
        public Rectangle Area { get; set; } = Rectangle.Empty;

        public ReplayBufferQuality Quality { get; set; } = ReplayBufferQuality.Balanced;
        public int FPS { get; set; } = 60;

        // Used when Quality is Custom
        public ReplayBufferCodec Codec { get; set; } = ReplayBufferCodec.H264;
        public int BitrateKbps { get; set; } = 8000;

        // NVENC preset p1 (fastest) to p7 (best quality), used when Quality is Custom
        public int Preset { get; set; } = 4;

        public ReplayBufferEncoder Encoder { get; set; } = ReplayBufferEncoder.Auto;

        public bool DrawCursor { get; set; } = true;

        public string DesktopAudioDevice { get; set; } = "";
        public string MicrophoneDevice { get; set; } = "";
        public bool MicrophoneSeparateTrack { get; set; } = false;
        public int AudioBitrateKbps { get; set; } = 160;

        // Empty = ShareX screenshots folder
        public string OutputFolder { get; set; } = "";

        // Empty = temp folder
        public string BufferFolder { get; set; } = "";

        // Empty = ffmpeg-replay\ffmpeg.exe next to ShareX, then the ShareX ffmpeg
        public string FFmpegPath { get; set; } = "";

        public bool RunAfterCaptureTasks { get; set; } = true;

        public ReplayBufferUpload Upload { get; set; } = ReplayBufferUpload.ShareXDefault;
        public FileDestination UploadDestination { get; set; } = FileDestination.Streamable;
        public int UploadCustomUploaderIndex { get; set; } = 0;
        public bool StartWithShareX { get; set; } = false;

        // Moved to VideoWatermarkSettings, only read once to carry settings over
        public bool WatermarkEnabled { get; set; } = false;
        public string WatermarkImage { get; set; } = "";
        public WatermarkPosition WatermarkPosition { get; set; } = WatermarkPosition.BottomRight;

        // Watermark width as a percentage of the video width
        public int WatermarkSize { get; set; } = 15;
        public int WatermarkOpacity { get; set; } = 85;
        public int WatermarkMargin { get; set; } = 24;
        public bool WatermarkRecordings { get; set; } = true;
        public bool ShowAdvanced { get; set; } = false;
        public bool DefaultHotkeysAdded { get; set; } = false;
    }
}
