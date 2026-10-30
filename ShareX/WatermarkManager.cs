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
using System.Drawing;
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
        public static string Apply(string videoPath, Rectangle captureArea = default)
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

            int width = 0;
            int height = 0;
            int bitrate = 12000;

            try
            {
                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath))
                {
                    VideoInfo info = ffmpeg.GetVideoInfo(videoPath);

                    if (info != null)
                    {
                        if (info.VideoResolution.Width > 0) width = info.VideoResolution.Width;
                        if (info.VideoResolution.Height > 0) height = info.VideoResolution.Height;
                        if (info.Bitrate > 0) bitrate = Math.Max(2000, info.Bitrate);
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            if (width <= 0 || height <= 0)
            {
                // Guessing the size would place the watermark outside small recordings
                Size probed = ProbeVideoSize(ffmpegPath, videoPath);
                width = probed.Width;
                height = probed.Height;
            }

            if (width <= 0 || height <= 0)
            {
                return "video size unknown";
            }

            Size imageSize;

            try
            {
                using (Image img = Image.FromFile(image))
                {
                    imageSize = img.Size;
                }
            }
            catch (Exception e)
            {
                return "image unreadable: " + e.Message;
            }

            Rectangle rect;

            if (s.Anchor == WatermarkAnchor.Screen && captureArea.Width > 0 && captureArea.Height > 0)
            {
                // Place it on the screen the capture came from, then shift into the video's coordinates
                Rectangle screen = Screen.FromRectangle(captureArea).Bounds;
                Rectangle onScreen = GetWatermarkRect(s, screen.Size, imageSize);
                onScreen.Offset(screen.Location);

                double scaleX = (double)width / captureArea.Width;
                double scaleY = (double)height / captureArea.Height;
                rect = new Rectangle((int)Math.Round((onScreen.X - captureArea.X) * scaleX), (int)Math.Round((onScreen.Y - captureArea.Y) * scaleY),
                    Math.Max(2, (int)(onScreen.Width * scaleX) / 2 * 2), Math.Max(2, (int)(onScreen.Height * scaleY) / 2 * 2));

                if (!rect.IntersectsWith(new Rectangle(0, 0, width, height)))
                {
                    // That spot of the screen was not recorded, nothing to add
                    return null;
                }
            }
            else
            {
                rect = GetWatermarkRect(s, new Size(width, height), imageSize);
            }
            string opacity = (Math.Max(5, Math.Min(100, s.Opacity)) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            bool animated = Path.GetExtension(image).Equals(".gif", StringComparison.OrdinalIgnoreCase);

            string tempPath = Path.Combine(Path.GetDirectoryName(videoPath), Path.GetFileNameWithoutExtension(videoPath) + "-watermark" + Path.GetExtension(videoPath));
            ReplayBufferEncoder encoder = ReplayBufferManager.ResolveEncoder(replaySettings, ffmpegPath);

            string result = Run(ffmpegPath, videoPath, image, animated, rect, opacity, bitrate, encoder, tempPath);

            if (result != null && encoder != ReplayBufferEncoder.CPU)
            {
                DebugHelper.WriteLine("Watermark with " + ReplayBufferManager.GetEncoderName(encoder) + " failed: " + result);
                result = Run(ffmpegPath, videoPath, image, animated, rect, opacity, bitrate, ReplayBufferEncoder.CPU, tempPath);
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

        // Fallback reader for the video size, independent of the ffmpeg output format
        private static Size ProbeVideoSize(string ffmpegPath, string videoPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, $"-hide_banner -nostdin -i \"{videoPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(output, @"Video: .*?, (\d{2,5})x(\d{2,5})");

                    if (match.Success)
                    {
                        return new Size(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return Size.Empty;
        }

        // Watermark placement in video pixels, shared by the encoder and the settings preview
        public static Rectangle GetWatermarkRect(VideoWatermarkSettings s, Size video, Size image)
        {
            int videoWidth = Math.Max(16, video.Width);
            int videoHeight = Math.Max(16, video.Height);
            // Sized by area: equals the width on a 16:9 video, and stays proportional on wide strips and tall windows
            double baseSize = Math.Sqrt((double)videoWidth * videoHeight * 16.0 / 9.0);

            double width = baseSize * Math.Max(1, Math.Min(100, s.Size)) / 100.0;
            double height = width * Math.Max(1, image.Height) / Math.Max(1, image.Width);
            int margin = (int)(baseSize * Math.Max(0, Math.Min(20, s.Margin)) / 100.0);

            // Never larger than the area inside the margin
            double maxWidth = Math.Max(4, videoWidth - margin * 2);
            double maxHeight = Math.Max(4, videoHeight - margin * 2);
            double fit = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
            int w = Math.Max(2, (int)(width * fit) / 2 * 2);
            int h = Math.Max(2, (int)(height * fit) / 2 * 2);

            int left = margin;
            int top = margin;
            int right = Math.Max(margin, videoWidth - w - margin);
            int bottom = Math.Max(margin, videoHeight - h - margin);
            int x, y;

            switch (s.Position)
            {
                case WatermarkPosition.TopLeft: x = left; y = top; break;
                case WatermarkPosition.TopRight: x = right; y = top; break;
                case WatermarkPosition.BottomLeft: x = left; y = bottom; break;
                case WatermarkPosition.Center: x = (videoWidth - w) / 2; y = (videoHeight - h) / 2; break;
                case WatermarkPosition.Custom:
                    x = left + (int)((right - left) * Math.Max(0, Math.Min(100, s.CustomX)) / 100.0);
                    y = top + (int)((bottom - top) * Math.Max(0, Math.Min(100, s.CustomY)) / 100.0);
                    break;
                default: x = right; y = bottom; break;
            }

            return new Rectangle(x, y, w, h);
        }

        private static string Run(string ffmpegPath, string videoPath, string image, bool animated, Rectangle rect, string opacity,
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
            string filter = $"[1:v]scale={rect.Width}:{rect.Height},format=rgba,colorchannelmixer=aa={opacity}[wm];[0:v][wm]overlay={rect.X}:{rect.Y}{shortest},format={pixelFormat}[v]";

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
