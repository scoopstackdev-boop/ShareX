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

using ShareX.HelpersLib;
using ShareX.MediaLib;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ShareX
{
    // Stamps an image on finished videos: replay clips and screen recordings
    public static class WatermarkManager
    {
        public static VideoWatermarkSettings Settings
        {
            get
            {
                if (Program.Settings.VideoWatermarkSettings == null)
                {
                    Program.Settings.VideoWatermarkSettings = new VideoWatermarkSettings();
                }

                VideoWatermarkSettings settings = Program.Settings.VideoWatermarkSettings;

                if (!settings.Migrated)
                {
                    ReplayBufferSettings old = Program.Settings.ReplayBufferSettings;

                    if (old != null && !string.IsNullOrEmpty(old.WatermarkImage))
                    {
                        settings.Enabled = old.WatermarkEnabled;
                        settings.Image = old.WatermarkImage;
                        settings.Position = old.WatermarkPosition;
                        settings.Size = old.WatermarkSize;
                        settings.Opacity = old.WatermarkOpacity;
                        settings.ApplyToRecordings = old.WatermarkRecordings;
                    }

                    settings.Migrated = true;
                }

                return settings;
            }
        }

        public static bool ApplyToClips
        {
            get
            {
                VideoWatermarkSettings s = Settings;
                return s.Enabled && s.ApplyToClips;
            }
        }

        public static bool ApplyToRecordings
        {
            get
            {
                VideoWatermarkSettings s = Settings;
                return s.Enabled && s.ApplyToRecordings;
            }
        }

        public static void OpenSettings()
        {
            using (WatermarkSettingsForm form = new WatermarkSettingsForm(Settings))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    SettingManager.SaveApplicationConfigAsync();
                }
            }
        }

        // Re-encodes the video with the watermark on top and replaces the file. Returns null on success, otherwise the reason.
        public static string Apply(string videoPath)
        {
            VideoWatermarkSettings s = Settings;

            if (!s.Enabled || string.IsNullOrWhiteSpace(s.Image))
            {
                return null;
            }

            string image = FileHelpers.GetAbsolutePath(s.Image);

            if (!File.Exists(image))
            {
                return "image not found: " + image;
            }

            ReplayBufferSettings replaySettings = Program.Settings.ReplayBufferSettings ?? new ReplayBufferSettings();
            string ffmpegPath = ReplayBufferManager.GetFFmpegPath(replaySettings);

            if (!File.Exists(ffmpegPath))
            {
                return "ffmpeg.exe not found: " + ffmpegPath;
            }

            int width = 1920;
            int bitrate = 12000;

            try
            {
                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath))
                {
                    VideoInfo info = ffmpeg.GetVideoInfo(videoPath);

                    if (info != null)
                    {
                        if (info.VideoResolution.Width > 0) width = info.VideoResolution.Width;
                        if (info.Bitrate > 0) bitrate = Math.Max(2000, info.Bitrate);
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            int size = Math.Max(1, Math.Min(100, s.Size));
            int watermarkWidth = Math.Max(16, (int)(width * size / 100.0) / 2 * 2);
            string opacity = (Math.Max(5, Math.Min(100, s.Opacity)) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            int m = (int)(width * Math.Max(0, Math.Min(20, s.Margin)) / 100.0);
            bool animated = Path.GetExtension(image).Equals(".gif", StringComparison.OrdinalIgnoreCase);

            string position;
            switch (s.Position)
            {
                case WatermarkPosition.TopLeft: position = $"{m}:{m}"; break;
                case WatermarkPosition.TopRight: position = $"W-w-{m}:{m}"; break;
                case WatermarkPosition.BottomLeft: position = $"{m}:H-h-{m}"; break;
                case WatermarkPosition.Center: position = "(W-w)/2:(H-h)/2"; break;
                default: position = $"W-w-{m}:H-h-{m}"; break;
            }

            string tempPath = Path.Combine(Path.GetDirectoryName(videoPath), Path.GetFileNameWithoutExtension(videoPath) + "-watermark" + Path.GetExtension(videoPath));
            ReplayBufferEncoder encoder = ReplayBufferManager.ResolveEncoder(replaySettings, ffmpegPath);

            string result = Run(ffmpegPath, videoPath, image, animated, watermarkWidth, opacity, position, bitrate, encoder, tempPath);

            if (result != null && encoder != ReplayBufferEncoder.CPU)
            {
                DebugHelper.WriteLine("Watermark with " + ReplayBufferManager.GetEncoderName(encoder) + " failed: " + result);
                result = Run(ffmpegPath, videoPath, image, animated, watermarkWidth, opacity, position, bitrate, ReplayBufferEncoder.CPU, tempPath);
            }

            if (result != null)
            {
                FileHelpers.DeleteFile(tempPath);
                return result;
            }

            try
            {
                File.Delete(videoPath);
                File.Move(tempPath, videoPath);
            }
            catch (Exception e)
            {
                return e.Message;
            }

            return null;
        }

        private static string Run(string ffmpegPath, string videoPath, string image, bool animated, int watermarkWidth, string opacity, string position,
            int bitrate, ReplayBufferEncoder encoder, string outputPath)
        {
            string pixelFormat = encoder == ReplayBufferEncoder.Intel ? "nv12" : "yuv420p";
            string videoEncoder;

            switch (encoder)
            {
                case ReplayBufferEncoder.NVIDIA: videoEncoder = "-c:v h264_nvenc -preset p5 -rc vbr"; break;
                case ReplayBufferEncoder.AMD: videoEncoder = "-c:v h264_amf -quality balanced -rc vbr_peak"; break;
                case ReplayBufferEncoder.Intel: videoEncoder = "-c:v h264_qsv -preset medium"; break;
                default: videoEncoder = "-c:v libx264 -preset veryfast"; break;
            }

            // An animated GIF loops for the whole video, a still image is held after its single frame
            string imageInput = animated ? $"-ignore_loop 0 -i \"{image}\"" : $"-i \"{image}\"";
            string shortest = animated ? ":shortest=1" : "";
            string filter = $"[1:v]scale={watermarkWidth}:-2,format=rgba,colorchannelmixer=aa={opacity}[wm];[0:v][wm]overlay={position}{shortest},format={pixelFormat}[v]";

            string args = $"-hide_banner -loglevel error -nostdin -y -i \"{videoPath}\" {imageInput} -filter_complex \"{filter}\" " +
                $"-map \"[v]\" -map 0:a? {videoEncoder} -b:v {bitrate}k -maxrate {bitrate * 3 / 2}k -bufsize {bitrate * 2}k -c:a copy -movflags +faststart \"{outputPath}\"";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    if (p.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    {
                        return string.IsNullOrWhiteSpace(stderr) ? "ffmpeg exit code " + p.ExitCode : stderr.Trim().Lines().LastOrDefault();
                    }
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }

            return null;
        }
    }
}
