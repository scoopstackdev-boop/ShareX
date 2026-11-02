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
using System.Text.RegularExpressions;
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

        // Watermark only, used for screen recordings
        public static string Apply(string videoPath, Rectangle captureArea = default)
        {
            return ProcessVideo(videoPath, captureArea, true, 0);
        }

        // One re-encode for the watermark and the size limit, replacing the file. Returns null on success or when there is nothing to do, otherwise the reason.
        public static string ProcessVideo(string videoPath, Rectangle captureArea, bool watermark, int maxSizeMB)
        {
            VideoWatermarkSettings s = Settings;
            string image = null;

            if (watermark && s.Enabled && !string.IsNullOrWhiteSpace(s.Image))
            {
                image = FileHelpers.GetAbsolutePath(s.Image);

                if (!File.Exists(image))
                {
                    return "image not found: " + image;
                }
            }

            long maxBytes = maxSizeMB > 0 ? (long)maxSizeMB * 1024 * 1024 : 0;
            bool limitSize = maxBytes > 0 && File.Exists(videoPath) && new FileInfo(videoPath).Length > maxBytes;

            if (image == null && !limitSize)
            {
                return null;
            }

            ReplayBufferSettings replaySettings = Program.Settings.ReplayBufferSettings ?? new ReplayBufferSettings();
            string ffmpegPath = ReplayBufferManager.GetFFmpegPath(replaySettings);

            if (!File.Exists(ffmpegPath))
            {
                return "ffmpeg.exe not found: " + ffmpegPath;
            }

            ProbeResult probe = Probe(ffmpegPath, videoPath);

            if (probe.Width <= 0 || probe.Height <= 0)
            {
                return "video size unknown";
            }

            int width = probe.Width;
            int height = probe.Height;
            int videoBitrate = Math.Max(2000, probe.Bitrate);
            int audioBitrate = 0;

            if (limitSize)
            {
                if (probe.Duration.TotalSeconds <= 0)
                {
                    return "clip length unknown";
                }

                // 4% left for the container
                double totalKbps = maxBytes * 8 * 0.96 / probe.Duration.TotalSeconds / 1000;
                audioBitrate = totalKbps < 1500 ? 96 : 128;
                videoBitrate = (int)(totalKbps - audioBitrate * Math.Max(1, probe.AudioTracks));

                if (videoBitrate < 150)
                {
                    return $"clip too long for {maxSizeMB} MB";
                }

                // A low bitrate at full resolution looks worse than a smaller sharp video
                int maxHeight = videoBitrate < 700 ? 480 : videoBitrate < 1500 ? 720 : videoBitrate < 3500 ? 1080 : int.MaxValue;

                if (height > maxHeight)
                {
                    width = Math.Max(2, (int)Math.Round(width * (double)maxHeight / height / 2) * 2);
                    height = maxHeight;
                }
            }

            string scale = width != probe.Width || height != probe.Height ? $"scale={width}:{height}:flags=lanczos" : "null";
            string filter;
            bool animated = false;

            if (image != null)
            {
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

                Rectangle? rect = GetVideoWatermarkRect(s, captureArea, new Size(width, height), imageSize);

                if (rect == null)
                {
                    // That spot of the screen was not recorded
                    image = null;
                }
                else
                {
                    Rectangle r = rect.Value;
                    string opacity = (Math.Max(5, Math.Min(100, s.Opacity)) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
                    animated = Path.GetExtension(image).Equals(".gif", StringComparison.OrdinalIgnoreCase);
                    string shortest = animated ? ":shortest=1" : "";
                    filter = $"[1:v]scale={r.Width}:{r.Height},format=rgba,colorchannelmixer=aa={opacity}[wm];[0:v]{scale}[base];[base][wm]overlay={r.X}:{r.Y}{shortest},format={{PIX}}[v]";
                    return Encode(ffmpegPath, videoPath, image, animated, filter, videoBitrate, audioBitrate, limitSize, maxBytes, replaySettings);
                }
            }

            if (!limitSize)
            {
                return null;
            }

            filter = $"[0:v]{scale},format={{PIX}}[v]";
            return Encode(ffmpegPath, videoPath, null, false, filter, videoBitrate, audioBitrate, true, maxBytes, replaySettings);
        }

        // Watermark rectangle in output video pixels, null when a screen-anchored watermark is outside the recording
        private static Rectangle? GetVideoWatermarkRect(VideoWatermarkSettings s, Rectangle captureArea, Size video, Size imageSize)
        {
            if (s.Anchor == WatermarkAnchor.Screen && captureArea.Width > 0 && captureArea.Height > 0)
            {
                Rectangle screen = Screen.FromRectangle(captureArea).Bounds;
                Rectangle onScreen = GetWatermarkRect(s, screen.Size, imageSize);
                onScreen.Offset(screen.Location);

                double scaleX = (double)video.Width / captureArea.Width;
                double scaleY = (double)video.Height / captureArea.Height;
                Rectangle rect = new Rectangle((int)Math.Round((onScreen.X - captureArea.X) * scaleX), (int)Math.Round((onScreen.Y - captureArea.Y) * scaleY),
                    Math.Max(2, (int)(onScreen.Width * scaleX) / 2 * 2), Math.Max(2, (int)(onScreen.Height * scaleY) / 2 * 2));

                if (!rect.IntersectsWith(new Rectangle(Point.Empty, video)))
                {
                    return null;
                }

                return rect;
            }

            return GetWatermarkRect(s, video, imageSize);
        }

        private static string Encode(string ffmpegPath, string videoPath, string image, bool animated, string filter, int videoBitrate, int audioBitrate,
            bool limitSize, long maxBytes, ReplayBufferSettings replaySettings)
        {
            string tempPath = Path.Combine(Path.GetDirectoryName(videoPath), Path.GetFileNameWithoutExtension(videoPath) + "-processed" + Path.GetExtension(videoPath));
            ReplayBufferEncoder encoder = ReplayBufferManager.ResolveEncoder(replaySettings, ffmpegPath);
            string result = null;

            // A second try at a lower bitrate if the first one lands over the limit
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int bitrate = attempt == 0 ? videoBitrate : (int)(videoBitrate * 0.85);

                result = Run(ffmpegPath, videoPath, image, animated, filter, bitrate, audioBitrate, limitSize, encoder, tempPath);

                if (result != null && encoder != ReplayBufferEncoder.CPU)
                {
                    DebugHelper.WriteLine("Re-encode with " + ReplayBufferManager.GetEncoderName(encoder) + " failed: " + result);
                    encoder = ReplayBufferEncoder.CPU;
                    result = Run(ffmpegPath, videoPath, image, animated, filter, bitrate, audioBitrate, limitSize, encoder, tempPath);
                }

                if (result != null || !limitSize || new FileInfo(tempPath).Length <= maxBytes)
                {
                    break;
                }
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

        private struct ProbeResult
        {
            public int Width;
            public int Height;
            public int Bitrate;
            public int AudioTracks;
            public TimeSpan Duration;
        }

        // Reads the video details straight from ffmpeg's output, independent of its formatting changes
        private static ProbeResult Probe(string ffmpegPath, string videoPath)
        {
            ProbeResult result = new ProbeResult();

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

                    Match size = Regex.Match(output, @"Video: .*?, (\d{2,5})x(\d{2,5})");

                    if (size.Success)
                    {
                        result.Width = int.Parse(size.Groups[1].Value);
                        result.Height = int.Parse(size.Groups[2].Value);
                    }

                    Match duration = Regex.Match(output, @"Duration: (\d+):(\d{2}):(\d{2}(?:\.\d+)?)");

                    if (duration.Success)
                    {
                        result.Duration = TimeSpan.FromHours(int.Parse(duration.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(duration.Groups[2].Value)) +
                            TimeSpan.FromSeconds(double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture));
                    }

                    Match bitrate = Regex.Match(output, @"bitrate: (\d+) kb/s");

                    if (bitrate.Success)
                    {
                        result.Bitrate = int.Parse(bitrate.Groups[1].Value);
                    }

                    result.AudioTracks = Regex.Matches(output, @"Stream #.*?: Audio:").Count;
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return result;
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

        private static string Run(string ffmpegPath, string videoPath, string image, bool animated, string filter, int videoBitrate, int audioBitrate,
            bool limitSize, ReplayBufferEncoder encoder, string outputPath)
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

            // A size limit needs a hard bitrate cap, otherwise peaks can go 50% over
            string rate = limitSize ?
                $"-b:v {videoBitrate}k -maxrate {videoBitrate}k -bufsize {videoBitrate}k" :
                $"-b:v {videoBitrate}k -maxrate {videoBitrate * 3 / 2}k -bufsize {videoBitrate * 2}k";
            string audio = limitSize ? $"-c:a aac -b:a {audioBitrate}k" : "-c:a copy";

            // An animated GIF loops for the whole video, a still image is held after its single frame
            string imageInput = image == null ? "" : animated ? $"-ignore_loop 0 -i \"{image}\"" : $"-i \"{image}\"";
            string graph = filter.Replace("{PIX}", pixelFormat);

            string args = $"-hide_banner -loglevel error -nostdin -y -i \"{videoPath}\" {imageInput} -filter_complex \"{graph}\" " +
                $"-map \"[v]\" -map 0:a? {videoEncoder} {rate} {audio} -movflags +faststart \"{outputPath}\"";

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
