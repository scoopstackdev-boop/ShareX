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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    // Rolling capture: ddagrab hands GPU frames straight to NVENC, ffmpeg writes short
    // MPEG-TS segments, old segments are deleted, and saving is a stream copy of the newest ones.
    public static class ReplayBufferManager
    {
        public const int SegmentSeconds = 2;
        private const string SegmentPrefix = "rb_";

        public static bool IsRunning
        {
            get
            {
                Process p = process;
                return p != null && !p.HasExited;
            }
        }

        public static bool IsWaiting => waitTimer != null;

        public static bool IsActive => IsRunning || IsWaiting;

        public static event Action StateChanged;

        private static readonly object processLock = new object();
        private static Process process;
        private static string segmentFolder;
        private static int keepSegments;
        private static System.Threading.Timer pruneTimer;
        private static bool stopping;
        private static int savesInProgress;
        private static string lastError = "";
        private static IntPtr jobHandle = IntPtr.Zero;
        private static System.Threading.Timer waitTimer;
        private static DateTime startTime;
        private static readonly Dictionary<string, bool> windowCaptureSupport = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private const string WaitForApp = "wait";

        private class CaptureTarget
        {
            public string Input;
            public int Width;
            public int Height;
            public bool NeedsWindowCapture;
        }

        private static ReplayBufferSettings Settings
        {
            get
            {
                if (Program.Settings.ReplayBufferSettings == null)
                {
                    Program.Settings.ReplayBufferSettings = new ReplayBufferSettings();
                }

                return Program.Settings.ReplayBufferSettings;
            }
        }

        public static void Toggle()
        {
            if (IsActive)
            {
                Stop();
                TaskHelpers.ShowNotificationTip("Replay buffer stopped.", "ShareX - Replay buffer", 2000);
            }
            else
            {
                Start(true);
            }
        }

        public static void OpenSettings()
        {
            using (ReplayBufferSettingsForm form = new ReplayBufferSettingsForm(Settings))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    SettingManager.SaveApplicationConfigAsync();
                    Restart();
                }
            }
        }

        public static void AutoStart()
        {
            if (Settings.StartWithShareX && !IsActive)
            {
                Start(false);
            }
        }

        public static bool Start(bool notify)
        {
            lock (processLock)
            {
                if (IsRunning) return true;

                ReplayBufferSettings s = Settings;
                string ffmpegPath = GetFFmpegPath(s);

                if (!File.Exists(ffmpegPath))
                {
                    TaskHelpers.ShowNotificationTip("ffmpeg.exe not found: " + ffmpegPath, "ShareX - Replay buffer", 5000);
                    return false;
                }

                string targetError = ResolveTarget(s, out CaptureTarget target);

                if (targetError == WaitForApp)
                {
                    BeginWait(notify);
                    return false;
                }

                if (targetError != null)
                {
                    TaskHelpers.ShowNotificationTip(targetError, "ShareX - Replay buffer", 5000);
                    return false;
                }

                if (target.NeedsWindowCapture && !SupportsWindowCapture(ffmpegPath))
                {
                    TaskHelpers.ShowNotificationTip("ffmpeg build has no window capture support: " + ffmpegPath, "ShareX - Replay buffer", 6000);
                    return false;
                }

                try
                {
                    segmentFolder = GetBufferFolder(s);
                    Directory.CreateDirectory(segmentFolder);
                    DeleteSegments();
                }
                catch (Exception e)
                {
                    TaskHelpers.ShowNotificationTip("Buffer folder error: " + e.Message, "ShareX - Replay buffer", 5000);
                    return false;
                }

                keepSegments = (int)Math.Ceiling(Math.Max(s.BufferSeconds, s.ClipSeconds) / (double)SegmentSeconds) + 2;
                lastError = "";
                stopping = false;

                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    FileName = ffmpegPath,
                    Arguments = BuildArguments(s, segmentFolder, target),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    WorkingDirectory = segmentFolder
                };

                DebugHelper.WriteLine("Replay buffer: ffmpeg " + psi.Arguments);

                Process p = new Process() { StartInfo = psi, EnableRaisingEvents = true };
                p.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        lastError = e.Data.Trim();
                        DebugHelper.WriteLine("Replay buffer ffmpeg: " + lastError);
                    }
                };
                p.Exited += Process_Exited;

                try
                {
                    p.Start();
                }
                catch (Exception e)
                {
                    p.Dispose();
                    TaskHelpers.ShowNotificationTip("ffmpeg failed to start: " + e.Message, "ShareX - Replay buffer", 5000);
                    return false;
                }

                AttachToJob(p);

                try
                {
                    // Keeps capture smooth when a game saturates the CPU
                    p.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
                catch
                {
                }

                p.BeginErrorReadLine();
                process = p;
                startTime = DateTime.UtcNow;

                pruneTimer = new System.Threading.Timer(_ => PruneSegments(), null, SegmentSeconds * 1000, SegmentSeconds * 1000);
            }

            if (notify)
            {
                TaskHelpers.ShowNotificationTip($"Replay buffer started ({FormatDuration(Settings.BufferSeconds)}).", "ShareX - Replay buffer", 2000);
            }

            StateChanged?.Invoke();
            return true;
        }

        public static void Stop()
        {
            Process p;
            System.Threading.Timer timer;

            CancelWait();

            lock (processLock)
            {
                p = process;
                timer = pruneTimer;
                process = null;
                pruneTimer = null;
                stopping = true;
            }

            timer?.Dispose();

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        // q makes ffmpeg finish the current segment cleanly
                        p.StandardInput.WriteLine("q");
                        p.StandardInput.Flush();

                        if (!p.WaitForExit(3000))
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (Volatile.Read(ref savesInProgress) == 0)
            {
                DeleteSegments();
            }

            StateChanged?.Invoke();
        }

        public static void Restart()
        {
            if (IsActive)
            {
                Stop();
                Start(false);
            }
        }

        public static void Save(TaskSettings taskSettings)
        {
            if (!IsRunning)
            {
                TaskHelpers.ShowNotificationTip("Replay buffer is not running.", "ShareX - Replay buffer", 2000);
                return;
            }

            ReplayBufferSettings s = Settings;
            if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

            // Stops the pruner deleting segments while they are being copied
            Interlocked.Increment(ref savesInProgress);

            string[] segments;

            try
            {
                int wanted = (int)Math.Ceiling(s.ClipSeconds / (double)SegmentSeconds) + 1;
                segments = GetSegments().Where(x => new FileInfo(x).Length > 0).ToArray();
                segments = segments.Skip(Math.Max(0, segments.Length - wanted)).ToArray();
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                segments = new string[0];
            }

            if (segments.Length == 0)
            {
                Interlocked.Decrement(ref savesInProgress);
                TaskHelpers.ShowNotificationTip("Replay buffer is empty.", "ShareX - Replay buffer", 2000);
                return;
            }

            TaskMetadata metadata = new TaskMetadata();
            string outputPath = null;
            string error = null;
            string ffmpegPath = GetFFmpegPath(s);
            string outputFolder = string.IsNullOrWhiteSpace(s.OutputFolder) ? TaskHelpers.GetScreenshotsFolder(taskSettings, metadata) : FileHelpers.GetAbsolutePath(s.OutputFolder);

            Task.Run(() =>
            {
                string listPath = null;

                try
                {
                    Directory.CreateDirectory(outputFolder);
                    string fileName = "Replay_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".mp4";
                    outputPath = Path.Combine(outputFolder, fileName);

                    for (int i = 2; File.Exists(outputPath); i++)
                    {
                        outputPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileName) + "_" + i + ".mp4");
                    }

                    listPath = Path.Combine(segmentFolder ?? Path.GetTempPath(), "save_" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(listPath, string.Join("\n", segments.Select(x => x.Replace("\\", "/"))) + "\n");

                    // The segments are one continuous MPEG-TS stream split into files, so joining them
                    // byte for byte keeps the original timestamps and A/V sync. Stream copy only, no re-encode.
                    string args = $"-hide_banner -loglevel error -nostdin -y -f mpegts -i \"concatf:{listPath.Replace("\\", "/")}\" -map 0 -c copy -bsf:a aac_adtstoasc -avoid_negative_ts make_zero -movflags +faststart \"{outputPath}\"";

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

                        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                        {
                            error = string.IsNullOrWhiteSpace(stderr) ? "ffmpeg exit code " + p.ExitCode : stderr.Trim().Lines().LastOrDefault();
                            outputPath = null;
                        }
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                    outputPath = null;
                }
                finally
                {
                    if (listPath != null)
                    {
                        try { File.Delete(listPath); } catch { }
                    }

                    Interlocked.Decrement(ref savesInProgress);
                }
            }).ContinueInCurrentContext(() =>
            {
                if (outputPath == null)
                {
                    TaskHelpers.ShowNotificationTip("Replay save failed: " + error, "ShareX - Replay buffer", 5000);
                    return;
                }

                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.ActionCompleted, taskSettings);

                if (s.RunAfterCaptureTasks)
                {
                    WorkerTask task = WorkerTask.CreateFileJobTask(outputPath, metadata, taskSettings);
                    TaskManager.Start(task);
                }
                else
                {
                    TaskHelpers.ShowNotificationTip("Replay saved: " + Path.GetFileName(outputPath), "ShareX - Replay buffer", 3000);
                }
            });
        }

        public static void AddDefaultHotkeys(List<HotkeySettings> hotkeys)
        {
            ReplayBufferSettings s = Settings;

            if (s.DefaultHotkeysAdded || hotkeys == null) return;

            if (!hotkeys.Any(x => x.TaskSettings != null && x.TaskSettings.Job == HotkeyType.SaveReplayBuffer))
            {
                hotkeys.Add(new HotkeySettings(HotkeyType.SaveReplayBuffer, Keys.Control | Keys.Alt | Keys.F10));
            }

            if (!hotkeys.Any(x => x.TaskSettings != null && x.TaskSettings.Job == HotkeyType.ToggleReplayBuffer))
            {
                hotkeys.Add(new HotkeySettings(HotkeyType.ToggleReplayBuffer, Keys.Control | Keys.Alt | Keys.Shift | Keys.F10));
            }

            s.DefaultHotkeysAdded = true;
        }

        public static string FormatDuration(int seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
            if (t.Minutes > 0) return t.Seconds > 0 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Minutes} min";
            return $"{t.Seconds}s";
        }

        public static string GetBufferFolder(ReplayBufferSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.BufferFolder))
            {
                return Path.Combine(FileHelpers.GetAbsolutePath(s.BufferFolder), "ShareX-ReplayBuffer");
            }

            return Path.Combine(Path.GetTempPath(), "ShareX-ReplayBuffer");
        }

        public static Screen[] GetScreens()
        {
            // Same ordering ShareX uses for ddagrab output_idx
            return Screen.AllScreens.OrderBy(x => !x.Primary).ToArray();
        }

        private static string BuildArguments(ReplayBufferSettings s, string folder, CaptureTarget target)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder args = new StringBuilder("-hide_banner -loglevel error -nostats ");

            List<string> audioDevices = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.DesktopAudioDevice)) audioDevices.Add(s.DesktopAudioDevice);
            if (!string.IsNullOrWhiteSpace(s.MicrophoneDevice) && !s.MicrophoneDevice.Equals(s.DesktopAudioDevice, StringComparison.Ordinal)) audioDevices.Add(s.MicrophoneDevice);

            foreach (string device in audioDevices)
            {
                args.Append("-f dshow -thread_queue_size 1024 -rtbufsize 64M -audio_buffer_size 50 ");
                args.Append($"-i audio={Helpers.EscapeCLIText(device)} ");
            }

            // ddagrab and gfxcapture both keep frames on the GPU, NVENC reads them without a copy to system memory
            args.Append("-f lavfi -thread_queue_size 1024 ");
            args.Append($"-i {target.Input} ");

            int videoInput = audioDevices.Count;
            args.Append($"-map {videoInput}:v ");

            if (audioDevices.Count == 1)
            {
                args.Append("-map 0:a ");
            }
            else if (audioDevices.Count == 2)
            {
                if (s.MicrophoneSeparateTrack)
                {
                    args.Append("-map 0:a -map 1:a ");
                }
                else
                {
                    args.Append("-filter_complex \"[0:a][1:a]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]\" -map \"[aout]\" ");
                }
            }

            ResolveEncoding(s, target.Width, target.Height, out ReplayBufferCodec codec, out int bitrate, out int preset);
            int fps = GetFPS(s);

            string encoder;
            switch (codec)
            {
                case ReplayBufferCodec.HEVC: encoder = "hevc_nvenc"; break;
                case ReplayBufferCodec.AV1: encoder = "av1_nvenc"; break;
                default: encoder = "h264_nvenc"; break;
            }

            int gop = fps * SegmentSeconds;

            args.Append($"-c:v {encoder} -preset p{preset} -tune hq -rc vbr ");
            args.Append(string.Format(ci, "-b:v {0}k -maxrate {1}k -bufsize {2}k ", bitrate, bitrate * 3 / 2, bitrate * 2));
            // A keyframe at every segment start so any run of segments joins cleanly
            args.Append($"-g {gop} -forced-idr 1 -force_key_frames \"expr:gte(t,n_forced*{SegmentSeconds})\" ");

            if (codec == ReplayBufferCodec.HEVC)
            {
                args.Append("-tag:v hvc1 ");
            }

            if (audioDevices.Count > 0)
            {
                args.Append(string.Format(ci, "-c:a aac -b:a {0}k -ac 2 ", Math.Max(64, s.AudioBitrateKbps)));
            }

            string pattern = Path.Combine(folder, SegmentPrefix + "%08d.ts").Replace("\\", "/");
            args.Append($"-f segment -segment_time {SegmentSeconds} -segment_format mpegts -reset_timestamps 0 \"{pattern}\"");

            return args.ToString();
        }

        public static string GetFFmpegPath(ReplayBufferSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.FFmpegPath))
            {
                return FileHelpers.GetAbsolutePath(s.FFmpegPath);
            }

            // Newer ffmpeg installed by the mod script, needed for app and window capture
            string replayFFmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg-replay", "ffmpeg.exe");

            if (File.Exists(replayFFmpeg))
            {
                return replayFFmpeg;
            }

            return Program.DefaultTaskSettings.CaptureSettings.FFmpegOptions.FFmpegPath;
        }

        public static int GetFPS(ReplayBufferSettings s)
        {
            return Math.Max(10, Math.Min(240, s.FPS));
        }

        // Presets are tuned for 1080p60 and scaled by pixel count and frame rate
        public static void ResolveEncoding(ReplayBufferSettings s, int width, int height, out ReplayBufferCodec codec, out int bitrate, out int preset)
        {
            int baseBitrate;

            switch (s.Quality)
            {
                case ReplayBufferQuality.SmallFiles:
                    codec = ReplayBufferCodec.HEVC;
                    baseBitrate = 5000;
                    preset = 5;
                    break;
                case ReplayBufferQuality.BestQuality:
                    codec = ReplayBufferCodec.H264;
                    baseBitrate = 16000;
                    preset = 6;
                    break;
                case ReplayBufferQuality.Custom:
                    codec = s.Codec;
                    bitrate = Math.Max(500, s.BitrateKbps);
                    preset = Math.Max(1, Math.Min(7, s.Preset));
                    return;
                default:
                    codec = ReplayBufferCodec.H264;
                    baseBitrate = 8000;
                    preset = 4;
                    break;
            }

            double pixels = Math.Max(1, width) * (double)Math.Max(1, height);
            double scale = (pixels / (1920.0 * 1080.0)) * (GetFPS(s) / 60.0);
            // Bitrate needs grow slower than pixel count
            double kbps = baseBitrate * Math.Pow(Math.Max(0.1, scale), 0.75);
            bitrate = Math.Max(1500, (int)(Math.Round(kbps / 500) * 500));
        }

        public static Size GetTargetSize(ReplayBufferSettings s)
        {
            Screen[] screens = GetScreens();

            switch (s.CaptureMode)
            {
                case ReplayBufferCaptureMode.Area:
                    if (!s.Area.IsEmpty) return s.Area.Size;
                    break;
                case ReplayBufferCaptureMode.App:
                    IntPtr hwnd = FindAppWindow(s.AppExe);
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r)) return new Size(r.Right - r.Left, r.Bottom - r.Top);
                    break;
                case ReplayBufferCaptureMode.Monitor:
                    if (s.MonitorIndex >= 0 && s.MonitorIndex < screens.Length) return screens[s.MonitorIndex].Bounds.Size;
                    break;
            }

            return Screen.FromPoint(Cursor.Position).Bounds.Size;
        }

        // Returns null on success, WaitForApp when the app has no window yet, otherwise the reason
        private static string ResolveTarget(ReplayBufferSettings s, out CaptureTarget target)
        {
            target = new CaptureTarget();
            Screen[] screens = GetScreens();
            int fps = GetFPS(s);
            string cursor = s.DrawCursor ? "1" : "0";

            switch (s.CaptureMode)
            {
                case ReplayBufferCaptureMode.App:
                case ReplayBufferCaptureMode.ActiveWindow:
                {
                    IntPtr hwnd;

                    if (s.CaptureMode == ReplayBufferCaptureMode.App)
                    {
                        if (string.IsNullOrWhiteSpace(s.AppExe)) return "No app selected.";
                        hwnd = FindAppWindow(s.AppExe);
                        if (hwnd == IntPtr.Zero) return WaitForApp;
                    }
                    else
                    {
                        hwnd = GetForegroundWindow();
                        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow()) return "No active window.";
                    }

                    if (!GetWindowRect(hwnd, out RECT rect)) return "Window size unavailable.";

                    target.Width = rect.Right - rect.Left;
                    target.Height = rect.Bottom - rect.Top;
                    target.NeedsWindowCapture = true;
                    // Windows Graphics Capture: only this window, even when covered, follows moves
                    target.Input = $"gfxcapture=hwnd={hwnd.ToInt64()}:max_framerate={fps}:capture_cursor={cursor}:width=-2:height=-2";
                    return null;
                }
                case ReplayBufferCaptureMode.Area:
                {
                    if (s.Area.Width < 16 || s.Area.Height < 16) return "No area selected.";

                    int monitorIndex = 0;
                    Rectangle area = Rectangle.Empty;
                    int maxIntersection = 0;

                    for (int i = 0; i < screens.Length; i++)
                    {
                        Rectangle intersection = Rectangle.Intersect(screens[i].Bounds, s.Area);
                        int size = intersection.Width * intersection.Height;

                        if (size > maxIntersection)
                        {
                            maxIntersection = size;
                            monitorIndex = i;
                            area = new Rectangle(intersection.X - screens[i].Bounds.X, intersection.Y - screens[i].Bounds.Y, intersection.Width, intersection.Height);
                        }
                    }

                    if (maxIntersection == 0) return "Area is outside the screen.";

                    // Encoders need even dimensions
                    area.Width -= area.Width % 2;
                    area.Height -= area.Height % 2;

                    target.Width = area.Width;
                    target.Height = area.Height;
                    target.Input = $"ddagrab=output_idx={monitorIndex}:draw_mouse={(s.DrawCursor ? "true" : "false")}:framerate={fps}:offset_x={area.X}:offset_y={area.Y}:video_size={area.Width}x{area.Height}";
                    return null;
                }
                default:
                {
                    int monitorIndex = s.MonitorIndex;

                    if (monitorIndex < 0 || monitorIndex >= screens.Length)
                    {
                        Screen cursorScreen = Screen.FromPoint(Cursor.Position);
                        monitorIndex = Math.Max(0, Array.FindIndex(screens, x => x.DeviceName == cursorScreen.DeviceName));
                    }

                    target.Width = screens[monitorIndex].Bounds.Width;
                    target.Height = screens[monitorIndex].Bounds.Height;
                    target.Input = $"ddagrab=output_idx={monitorIndex}:draw_mouse={(s.DrawCursor ? "true" : "false")}:framerate={fps}";
                    return null;
                }
            }
        }

        public static IntPtr FindAppWindow(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return IntPtr.Zero;

            string name = Path.GetFileNameWithoutExtension(exe.Trim());

            foreach (Process p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        IntPtr hwnd = p.MainWindowHandle;
                        if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd)) return hwnd;
                    }
                    catch
                    {
                    }
                }
            }

            return IntPtr.Zero;
        }

        // Apps with a visible main window, as (exe, title)
        public static List<KeyValuePair<string, string>> GetOpenApps()
        {
            List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int ownId = Environment.ProcessId;

            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == ownId || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || !IsWindowVisible(p.MainWindowHandle)) continue;

                        string exe = p.ProcessName + ".exe";
                        if (seen.Add(exe)) apps.Add(new KeyValuePair<string, string>(exe, p.MainWindowTitle));
                    }
                    catch
                    {
                    }
                }
            }

            return apps.OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void BeginWait(bool notify)
        {
            if (waitTimer != null) return;

            string exe = Settings.AppExe;

            waitTimer = new System.Threading.Timer(_ =>
            {
                if (FindAppWindow(exe) != IntPtr.Zero)
                {
                    Program.MainForm?.InvokeSafe(() =>
                    {
                        if (waitTimer != null)
                        {
                            CancelWait();
                            Start(true);
                        }
                    });
                }
            }, null, 3000, 3000);

            if (notify)
            {
                TaskHelpers.ShowNotificationTip($"Waiting for {exe}.", "ShareX - Replay buffer", 3000);
            }

            StateChanged?.Invoke();
        }

        private static void CancelWait()
        {
            System.Threading.Timer timer = waitTimer;
            waitTimer = null;
            timer?.Dispose();
        }

        private static bool SupportsWindowCapture(string ffmpegPath)
        {
            lock (windowCaptureSupport)
            {
                if (windowCaptureSupport.TryGetValue(ffmpegPath, out bool cached)) return cached;
            }

            bool supported = false;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -h filter=gfxcapture")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    Task<string> stderr = p.StandardError.ReadToEndAsync();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    supported = (stdout + stderr.Result).Contains("Filter gfxcapture", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            lock (windowCaptureSupport)
            {
                windowCaptureSupport[ffmpegPath] = supported;
            }

            return supported;
        }

        private static string[] GetSegments()
        {
            string folder = segmentFolder;

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return new string[0];
            }

            // Zero padded numbers, so ordinal order is recording order
            return Directory.GetFiles(folder, SegmentPrefix + "*.ts").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void PruneSegments()
        {
            if (Volatile.Read(ref savesInProgress) > 0) return;

            try
            {
                string[] segments = GetSegments();

                for (int i = 0; i < segments.Length - keepSegments; i++)
                {
                    try { File.Delete(segments[i]); } catch { }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void DeleteSegments()
        {
            try
            {
                foreach (string file in GetSegments())
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
            }
        }

        private static void Process_Exited(object sender, EventArgs e)
        {
            Process p = sender as Process;
            bool unexpected;

            lock (processLock)
            {
                unexpected = !stopping && ReferenceEquals(p, process);

                if (unexpected)
                {
                    process = null;
                    pruneTimer?.Dispose();
                    pruneTimer = null;
                }
            }

            if (unexpected)
            {
                ReplayBufferSettings s = Settings;
                bool ranLongEnough = (DateTime.UtcNow - startTime).TotalSeconds > 10;

                if (s.CaptureMode == ReplayBufferCaptureMode.App && ranLongEnough)
                {
                    // The game closed or restarted: pick it up again when its window comes back
                    TaskHelpers.ShowNotificationTip($"{s.AppExe} closed. Waiting for it to open.", "ShareX - Replay buffer", 3000);
                    Program.MainForm?.InvokeSafe(() => BeginWait(false));
                }
                else
                {
                    string reason = string.IsNullOrWhiteSpace(lastError) ? "ffmpeg exited" : lastError;
                    TaskHelpers.ShowNotificationTip("Replay buffer stopped: " + reason, "ShareX - Replay buffer", 6000);
                }

                StateChanged?.Invoke();
            }
        }

        #region Job object, so ffmpeg closes with ShareX even after a crash

        private static void AttachToJob(Process p)
        {
            try
            {
                if (jobHandle == IntPtr.Zero)
                {
                    IntPtr job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return;

                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)length))
                    {
                        CloseHandle(job);
                        return;
                    }

                    jobHandle = job;
                }

                AssignProcessToJobObject(jobHandle, p.Handle);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        #endregion
    }
}
