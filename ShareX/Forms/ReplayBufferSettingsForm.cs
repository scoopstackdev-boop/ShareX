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
using ShareX.ScreenCaptureLib;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public class ReplayBufferSettingsForm : Form
    {
        private const string NoDevice = "None";

        private static readonly int[] LengthPresets = { 30, 60, 120, 180, 300, 600, 900, 1800 };
        private static readonly int[] FPSPresets = { 30, 60, 120, 144 };

        private readonly ReplayBufferSettings settings;
        private Rectangle area;
        private bool updating;

        private TableLayoutPanel basic, advanced;
        private ComboBox cbMode, cbMonitor, cbApp, cbLength, cbQuality, cbFPS, cbDesktopAudio, cbMicrophone, cbCodec, cbPreset;
        private NumericUpDown nudBuffer, nudClip, nudBitrate, nudAudioBitrate;
        private CheckBox cbAfterCapture, cbStartWithShareX, cbAdvanced, cbCursor, cbMicSeparate;
        private TextBox txtOutput, txtBuffer, txtFFmpeg;
        private Label lblArea, lblModeNote, lblQualityInfo, lblAudioNote, lblCustomNote;
        private Control[] monitorRow, appRow, areaRow, destinationRow, customUploaderRow;
        private ComboBox cbEncoder, cbUpload, cbDestination, cbCustomUploader;
        private readonly FileDestination[] destinations = (FileDestination[])Enum.GetValues(typeof(FileDestination));

        private List<KeyValuePair<string, string>> openApps = new List<KeyValuePair<string, string>>();

        public ReplayBufferSettingsForm(ReplayBufferSettings settings)
        {
            this.settings = settings;
            area = settings.Area;

            Text = "ShareX - Replay buffer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            ShowInTaskbar = true;

            BuildLayout();
            LoadValues();

            ShareXResources.ApplyTheme(this, true);
        }

        #region Layout

        private void BuildLayout()
        {
            FlowLayoutPanel root = new FlowLayoutPanel()
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };
            Controls.Add(root);

            basic = NewTable();
            advanced = NewTable();
            root.Controls.Add(basic);
            root.Controls.Add(advanced);

            // What to record
            AddHeader(basic, "Capture");
            cbMode = AddCombo(basic, "Source", new[] { "Monitor", "Application", "Active window", "Region" }, out _);
            cbMode.SelectedIndexChanged += (s, e) => UpdateVisibility();

            List<string> monitors = new List<string>() { "Monitor with cursor" };
            Screen[] screens = ReplayBufferManager.GetScreens();
            for (int i = 0; i < screens.Length; i++)
            {
                monitors.Add($"Monitor {i + 1}: {screens[i].Bounds.Width}x{screens[i].Bounds.Height}{(screens[i].Primary ? " (primary)" : "")}");
            }
            cbMonitor = AddCombo(basic, "Monitor", monitors.ToArray(), out monitorRow);
            cbMonitor.SelectedIndexChanged += (s, e) => UpdateInfo();

            cbApp = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
            cbApp.TextChanged += (s, e) => UpdateInfo();
            Button btnRefresh = new Button() { Text = "Refresh", AutoSize = true };
            btnRefresh.Click += (s, e) => RefreshApps();
            appRow = AddRow(basic, "Application", cbApp, btnRefresh);

            Button btnArea = new Button() { Text = "Select region...", AutoSize = true };
            btnArea.Click += (s, e) => SelectArea();
            lblArea = new Label() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 3, 3) };
            areaRow = AddRow(basic, "Region", btnArea, lblArea);

            lblModeNote = AddNote(basic, "");

            // Clip
            AddHeader(basic, "Video");
            cbLength = AddCombo(basic, "Duration", LengthPresets.Select(x => ReplayBufferManager.FormatDuration(x)).Concat(new[] { "Custom" }).ToArray(), out _);
            cbLength.SelectedIndexChanged += (s, e) => LengthPresetChanged();

            cbQuality = AddCombo(basic, "Quality", new[] { "Small file size (HEVC)", "Balanced (H.264)", "High quality (H.264)", "Custom" }, out Control[] qualityRow);
            lblQualityInfo = new Label() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 3, 3) };
            basic.Controls.Add(lblQualityInfo, 2, basic.GetRow(cbQuality));
            cbQuality.SelectedIndexChanged += (s, e) => { UpdateCustomEnabled(); UpdateInfo(); };

            cbFPS = AddCombo(basic, "FPS", FPSPresets.Select(x => x.ToString()).ToArray(), out _);
            cbFPS.SelectedIndexChanged += (s, e) => UpdateInfo();

            // Audio
            AddHeader(basic, "Audio");
            cbDesktopAudio = AddCombo(basic, "Desktop audio", new[] { NoDevice }, out _);
            cbMicrophone = AddCombo(basic, "Microphone", new[] { NoDevice }, out _);
            lblAudioNote = AddNote(basic, "Loading devices...");

            // Saving
            AddHeader(basic, "Output");
            txtOutput = AddFolder(basic, "Folder", "Screenshots folder");

            cbUpload = AddCombo(basic, "Upload", new[] { "Same as ShareX", "Off", "Upload to" }, out _);
            cbUpload.SelectedIndexChanged += (s, e) => UpdateUploadVisibility();

            cbDestination = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            cbDestination.Items.AddRange(destinations.Select(x => (object)x.GetLocalizedDescription()).ToArray());
            cbDestination.SelectedIndexChanged += (s, e) => UpdateUploadVisibility();
            Button btnDestinations = new Button() { Text = "Accounts...", AutoSize = true };
            btnDestinations.Click += (s, e) => TaskHelpers.OpenUploadersConfigWindow();
            destinationRow = AddRow(basic, "Destination", cbDestination, btnDestinations);

            cbCustomUploader = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            Button btnCustomUploaders = new Button() { Text = "Edit...", AutoSize = true };
            btnCustomUploaders.Click += (s, e) => TaskHelpers.OpenCustomUploaderSettingsWindow();
            customUploaderRow = AddRow(basic, "Custom uploader", cbCustomUploader, btnCustomUploaders);
            cbAfterCapture = AddCheck(basic, "Run after capture tasks");
            cbStartWithShareX = AddCheck(basic, "Start replay buffer on startup");

            Button btnHotkeys = new Button() { Text = "Hotkey settings...", AutoSize = true };
            btnHotkeys.Click += (s, e) => Program.MainForm?.OpenHotkeySettings();
            AddRow(basic, "Hotkeys", btnHotkeys, new Label() { Text = "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 3, 3) });

            cbAdvanced = AddCheck(basic, "Advanced");
            cbAdvanced.Margin = new Padding(3, 14, 3, 3);
            cbAdvanced.CheckedChanged += (s, e) => advanced.Visible = cbAdvanced.Checked;

            // Advanced
            AddHeader(advanced, "Advanced");
            nudBuffer = AddNumeric(advanced, "Buffer duration (seconds)", 10, 3600, 10);
            nudClip = AddNumeric(advanced, "Clip duration (seconds)", 5, 3600, 5);
            nudBuffer.ValueChanged += (s, e) => ExactLengthChanged();
            nudClip.ValueChanged += (s, e) => ExactLengthChanged();

            cbEncoder = AddCombo(advanced, "Encoder", new[] { "Auto", "NVIDIA (NVENC)", "AMD (AMF)", "Intel (Quick Sync)", "CPU (x264)" }, out _);
            cbCodec = AddCombo(advanced, "Codec", new[] { "H.264", "HEVC", "AV1 (newer GPUs only)" }, out _);
            nudBitrate = AddNumeric(advanced, "Bitrate (kbps)", 1000, 150000, 500);
            cbPreset = AddCombo(advanced, "Preset", new[] { "P1 (fastest)", "P2", "P3", "P4", "P5", "P6", "P7 (slowest)" }, out _);
            lblCustomNote = AddNote(advanced, "Only used with Custom quality.");
            cbCodec.SelectedIndexChanged += (s, e) => UpdateInfo();
            nudBitrate.ValueChanged += (s, e) => UpdateInfo();

            cbCursor = AddCheck(advanced, "Show cursor");
            cbMicSeparate = AddCheck(advanced, "Separate microphone track");
            nudAudioBitrate = AddNumeric(advanced, "Audio bitrate (kbps)", 64, 320, 32);
            nudAudioBitrate.ValueChanged += (s, e) => UpdateInfo();
            txtBuffer = AddFolder(advanced, "Buffer folder", "Temp folder");

            txtFFmpeg = new TextBox() { Width = 300, PlaceholderText = "Automatic" };
            Button btnFFmpeg = new Button() { Text = "...", Width = 32 };
            btnFFmpeg.Click += (s, e) => FileHelpers.BrowseFile(txtFFmpeg, "", false, "ffmpeg.exe|ffmpeg.exe");
            AddRow(advanced, "FFmpeg path", txtFFmpeg, btnFFmpeg);

            // Buttons
            FlowLayoutPanel buttons = new FlowLayoutPanel()
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 14, 0, 0)
            };
            Button btnCancel = new Button() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
            Button btnOK = new Button() { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
            btnOK.Click += (s, e) => SaveValues();
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnOK);
            root.Controls.Add(buttons);

            AcceptButton = btnOK;
            CancelButton = btnCancel;
        }

        private static TableLayoutPanel NewTable()
        {
            TableLayoutPanel table = new TableLayoutPanel()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            return table;
        }

        private void AddHeader(TableLayoutPanel table, string text)
        {
            Label label = new Label()
            {
                Text = text,
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                Margin = new Padding(0, table.RowCount == 0 ? 0 : 14, 0, 4)
            };
            table.Controls.Add(label, 0, table.RowCount);
            table.SetColumnSpan(label, 3);
            table.RowCount++;
        }

        private Control[] AddRow(TableLayoutPanel table, string label, Control control, Control extra = null)
        {
            Label lbl = new Label() { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
            table.Controls.Add(lbl, 0, table.RowCount);
            table.Controls.Add(control, 1, table.RowCount);
            if (extra != null) table.Controls.Add(extra, 2, table.RowCount);
            table.RowCount++;
            return extra != null ? new[] { lbl, control, extra } : new[] { lbl, control };
        }

        private NumericUpDown AddNumeric(TableLayoutPanel table, string label, int min, int max, int step)
        {
            NumericUpDown nud = new NumericUpDown() { Minimum = min, Maximum = max, Increment = step, Width = 100, TextAlign = HorizontalAlignment.Center };
            AddRow(table, label, nud);
            return nud;
        }

        private ComboBox AddCombo(TableLayoutPanel table, string label, string[] items, out Control[] row)
        {
            ComboBox cb = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            cb.Items.AddRange(items);
            row = AddRow(table, label, cb);
            return cb;
        }

        private CheckBox AddCheck(TableLayoutPanel table, string text)
        {
            CheckBox cb = new CheckBox() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            table.Controls.Add(cb, 0, table.RowCount);
            table.SetColumnSpan(cb, 3);
            table.RowCount++;
            return cb;
        }

        private Label AddNote(TableLayoutPanel table, string text)
        {
            Label lbl = new Label() { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 3) };
            table.Controls.Add(lbl, 1, table.RowCount);
            table.SetColumnSpan(lbl, 2);
            table.RowCount++;
            return lbl;
        }

        private TextBox AddFolder(TableLayoutPanel table, string label, string hint)
        {
            TextBox txt = new TextBox() { Width = 300, PlaceholderText = hint };
            Button btn = new Button() { Text = "...", Width = 32 };
            btn.Click += (s, e) => FileHelpers.BrowseFolder(txt);
            AddRow(table, label, txt, btn);
            return txt;
        }

        #endregion Layout

        #region Values

        private static int Clamp(int value, NumericUpDown nud)
        {
            return (int)Math.Max(nud.Minimum, Math.Min(nud.Maximum, value));
        }

        private void LoadValues()
        {
            updating = true;

            cbMode.SelectedIndex = (int)settings.CaptureMode;
            cbMonitor.SelectedIndex = settings.MonitorIndex + 1 < cbMonitor.Items.Count ? Math.Max(0, settings.MonitorIndex + 1) : 0;
            cbApp.Text = settings.AppExe;
            nudBuffer.Value = Clamp(settings.BufferSeconds, nudBuffer);
            nudClip.Value = Clamp(settings.ClipSeconds, nudClip);
            cbQuality.SelectedIndex = (int)settings.Quality;

            int fpsIndex = Array.IndexOf(FPSPresets, settings.FPS);
            if (fpsIndex < 0)
            {
                cbFPS.Items.Add(settings.FPS.ToString());
                fpsIndex = cbFPS.Items.Count - 1;
            }
            cbFPS.SelectedIndex = fpsIndex;

            cbCodec.SelectedIndex = (int)settings.Codec;
            nudBitrate.Value = Clamp(settings.BitrateKbps, nudBitrate);
            cbPreset.SelectedIndex = Math.Max(1, Math.Min(7, settings.Preset)) - 1;
            cbEncoder.SelectedIndex = (int)settings.Encoder;
            cbUpload.SelectedIndex = (int)settings.Upload;
            cbDestination.SelectedIndex = Math.Max(0, Array.IndexOf(destinations, settings.UploadDestination));
            LoadCustomUploaders();
            cbCursor.Checked = settings.DrawCursor;
            cbMicSeparate.Checked = settings.MicrophoneSeparateTrack;
            nudAudioBitrate.Value = Clamp(settings.AudioBitrateKbps, nudAudioBitrate);
            txtOutput.Text = settings.OutputFolder;
            txtBuffer.Text = settings.BufferFolder;
            txtFFmpeg.Text = settings.FFmpegPath;
            cbAfterCapture.Checked = settings.RunAfterCaptureTasks;
            cbStartWithShareX.Checked = settings.StartWithShareX;
            cbAdvanced.Checked = settings.ShowAdvanced;
            advanced.Visible = settings.ShowAdvanced;

            SetDevices(new List<string>());

            updating = false;

            SyncLengthPreset();
            UpdateAreaLabel();
            UpdateCustomEnabled();
            UpdateVisibility();
        }

        private void ReadInto(ReplayBufferSettings s)
        {
            s.CaptureMode = (ReplayBufferCaptureMode)Math.Max(0, cbMode.SelectedIndex);
            s.MonitorIndex = cbMonitor.SelectedIndex - 1;
            s.AppExe = GetAppExe();
            s.Area = area;
            s.BufferSeconds = (int)nudBuffer.Value;
            s.ClipSeconds = (int)Math.Min(nudClip.Value, nudBuffer.Value);
            s.Quality = (ReplayBufferQuality)Math.Max(0, cbQuality.SelectedIndex);
            s.FPS = GetFPS();
            s.Codec = (ReplayBufferCodec)Math.Max(0, cbCodec.SelectedIndex);
            s.BitrateKbps = (int)nudBitrate.Value;
            s.Preset = cbPreset.SelectedIndex + 1;
            s.Encoder = (ReplayBufferEncoder)Math.Max(0, cbEncoder.SelectedIndex);
            s.Upload = (ReplayBufferUpload)Math.Max(0, cbUpload.SelectedIndex);
            s.UploadDestination = destinations[Math.Max(0, cbDestination.SelectedIndex)];
            s.UploadCustomUploaderIndex = Math.Max(0, cbCustomUploader.SelectedIndex);
            s.DrawCursor = cbCursor.Checked;
            s.DesktopAudioDevice = GetDevice(cbDesktopAudio);
            s.MicrophoneDevice = GetDevice(cbMicrophone);
            s.MicrophoneSeparateTrack = cbMicSeparate.Checked;
            s.AudioBitrateKbps = (int)nudAudioBitrate.Value;
            s.OutputFolder = txtOutput.Text.Trim();
            s.BufferFolder = txtBuffer.Text.Trim();
            s.FFmpegPath = txtFFmpeg.Text.Trim();
            s.RunAfterCaptureTasks = cbAfterCapture.Checked;
            s.StartWithShareX = cbStartWithShareX.Checked;
            s.ShowAdvanced = cbAdvanced.Checked;
        }

        private void SaveValues()
        {
            ReplayBufferCaptureMode mode = (ReplayBufferCaptureMode)Math.Max(0, cbMode.SelectedIndex);

            if (mode == ReplayBufferCaptureMode.App && string.IsNullOrEmpty(GetAppExe()))
            {
                cbApp.Focus();
                lblModeNote.Text = "No app selected.";
                return;
            }

            if (mode == ReplayBufferCaptureMode.Area && (area.Width < 16 || area.Height < 16))
            {
                lblModeNote.Text = "No region selected.";
                return;
            }

            ReadInto(settings);
            DialogResult = DialogResult.OK;
            Close();
        }

        private int GetFPS()
        {
            string text = (cbFPS.SelectedItem as string) ?? "60";
            return int.TryParse(text.Split(' ')[0], out int fps) ? fps : 60;
        }

        // Accepts "Title (game.exe)" from the list or a typed exe name
        private string GetAppExe()
        {
            string text = cbApp.Text.Trim();
            if (text.Length == 0) return "";

            int open = text.LastIndexOf('(');
            if (text.EndsWith(")") && open >= 0)
            {
                text = text.Substring(open + 1, text.Length - open - 2).Trim();
            }

            if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                text += ".exe";
            }

            return text;
        }

        private static string GetDevice(ComboBox cb)
        {
            string value = cb.SelectedItem as string;
            return string.IsNullOrEmpty(value) || value == NoDevice ? "" : value;
        }

        #endregion Values

        #region Behaviour

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            RefreshApps();

            string ffmpegPath = ReplayBufferManager.GetFFmpegPath(settings);

            if (!File.Exists(ffmpegPath))
            {
                lblAudioNote.Text = "ffmpeg.exe not found.";
                return;
            }

            List<string> devices = await Task.Run(() =>
            {
                try
                {
                    using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath))
                    {
                        return ffmpeg.GetDirectShowDevices().AudioDevices;
                    }
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex);
                    return new List<string>();
                }
            });

            if (IsDisposed) return;

            SetDevices(devices);
            lblAudioNote.Text = devices.Count == 0 ? "No audio devices." :
                "Desktop audio requires a loopback device (virtual-audio-capturer, Stereo Mix).";
        }

        private void RefreshApps()
        {
            string current = cbApp.Text;
            openApps = ReplayBufferManager.GetOpenApps();

            cbApp.BeginUpdate();
            cbApp.Items.Clear();
            foreach (KeyValuePair<string, string> app in openApps)
            {
                cbApp.Items.Add($"{app.Value} ({app.Key})");
            }
            cbApp.EndUpdate();

            cbApp.Text = current;
        }

        private void SelectArea()
        {
            // Hiding a modal dialog would close it, so make it invisible to the screenshot instead
            Opacity = 0;
            Refresh();
            System.Threading.Thread.Sleep(150);

            try
            {
                if (RegionCaptureTasks.GetRectangleRegion(out Rectangle rect, Program.DefaultTaskSettings.CaptureSettings.SurfaceOptions) && rect.Width >= 16 && rect.Height >= 16)
                {
                    area = rect;
                }
            }
            finally
            {
                Opacity = 1;
                Activate();
            }

            UpdateAreaLabel();
            UpdateInfo();
        }

        private void UpdateAreaLabel()
        {
            lblArea.Text = area.Width >= 16 && area.Height >= 16 ? $"{area.X}, {area.Y}, {area.Width}x{area.Height}" : "None";
        }

        private void LoadCustomUploaders()
        {
            cbCustomUploader.Items.Clear();

            List<CustomUploaderItem> uploaders = Program.UploadersConfig?.CustomUploadersList;

            if (uploaders != null)
            {
                cbCustomUploader.Items.AddRange(uploaders.Select(x => (object)x.ToString()).ToArray());
            }

            if (cbCustomUploader.Items.Count == 0)
            {
                cbCustomUploader.Items.Add("None");
            }

            cbCustomUploader.SelectedIndex = Math.Min(Math.Max(0, settings.UploadCustomUploaderIndex), cbCustomUploader.Items.Count - 1);
        }

        private void UpdateUploadVisibility()
        {
            if (cbUpload == null || cbDestination == null || destinationRow == null || customUploaderRow == null) return;

            bool upload = cbUpload.SelectedIndex == (int)ReplayBufferUpload.Destination;
            bool custom = upload && cbDestination.SelectedIndex >= 0 && destinations[cbDestination.SelectedIndex] == FileDestination.CustomFileUploader;

            SetVisible(destinationRow, upload);
            SetVisible(customUploaderRow, custom);
        }

        private void UpdateVisibility()
        {
            ReplayBufferCaptureMode mode = (ReplayBufferCaptureMode)Math.Max(0, cbMode.SelectedIndex);

            SetVisible(monitorRow, mode == ReplayBufferCaptureMode.Monitor);
            SetVisible(appRow, mode == ReplayBufferCaptureMode.App);
            SetVisible(areaRow, mode == ReplayBufferCaptureMode.Area);
            UpdateUploadVisibility();

            switch (mode)
            {
                case ReplayBufferCaptureMode.App:
                    lblModeNote.Text = "Captures the window even when it is covered. Exclusive fullscreen is not supported.";
                    break;
                case ReplayBufferCaptureMode.ActiveWindow:
                    lblModeNote.Text = "Window that is active when the replay buffer starts.";
                    break;
                case ReplayBufferCaptureMode.Area:
                    lblModeNote.Text = "";
                    break;
                default:
                    lblModeNote.Text = "";
                    break;
            }

            UpdateInfo();
        }

        private static void SetVisible(Control[] row, bool visible)
        {
            foreach (Control c in row) c.Visible = visible;
        }

        private void LengthPresetChanged()
        {
            if (updating) return;

            int index = cbLength.SelectedIndex;

            if (index >= 0 && index < LengthPresets.Length)
            {
                updating = true;
                nudBuffer.Value = Clamp(LengthPresets[index], nudBuffer);
                nudClip.Value = Clamp(LengthPresets[index], nudClip);
                updating = false;
            }
            else if (!cbAdvanced.Checked)
            {
                cbAdvanced.Checked = true;
            }

            UpdateInfo();
        }

        private void ExactLengthChanged()
        {
            if (updating) return;

            if (nudClip.Value > nudBuffer.Value)
            {
                updating = true;
                nudClip.Value = nudBuffer.Value;
                updating = false;
            }

            SyncLengthPreset();
            UpdateInfo();
        }

        private void SyncLengthPreset()
        {
            updating = true;
            int index = nudBuffer.Value == nudClip.Value ? Array.IndexOf(LengthPresets, (int)nudBuffer.Value) : -1;
            cbLength.SelectedIndex = index >= 0 ? index : LengthPresets.Length;
            updating = false;
        }

        private void UpdateCustomEnabled()
        {
            bool custom = cbQuality.SelectedIndex == (int)ReplayBufferQuality.Custom;
            cbCodec.Enabled = nudBitrate.Enabled = cbPreset.Enabled = custom;
            lblCustomNote.Visible = !custom;

            if (custom && !cbAdvanced.Checked && !updating)
            {
                cbAdvanced.Checked = true;
            }
        }

        private void UpdateInfo()
        {
            if (updating || lblQualityInfo == null) return;

            ReplayBufferSettings temp = new ReplayBufferSettings();
            ReadInto(temp);

            Size size = ReplayBufferManager.GetTargetSize(temp);
            ReplayBufferManager.ResolveEncoding(temp, size.Width, size.Height, out ReplayBufferCodec codec, out int bitrate, out int preset);

            if (temp.Quality != ReplayBufferQuality.Custom)
            {
                // Show what the preset picked
                updating = true;
                cbCodec.SelectedIndex = (int)codec;
                nudBitrate.Value = Clamp(bitrate, nudBitrate);
                cbPreset.SelectedIndex = preset - 1;
                updating = false;
            }

            double mbPerMinute = (bitrate + (double)nudAudioBitrate.Value) * 60 / 8 / 1000;
            double clipMB = mbPerMinute * temp.ClipSeconds / 60;
            lblQualityInfo.Text = $"{bitrate} kbps, ~{clipMB:0} MB per clip";
        }

        private void SetDevices(List<string> devices)
        {
            FillDevices(cbDesktopAudio, devices, settings.DesktopAudioDevice);
            FillDevices(cbMicrophone, devices, settings.MicrophoneDevice);
        }

        private static void FillDevices(ComboBox cb, List<string> devices, string selected)
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            cb.Items.Add(NoDevice);
            cb.Items.AddRange(devices.Cast<object>().ToArray());

            if (!string.IsNullOrEmpty(selected) && !devices.Contains(selected))
            {
                // Keep a saved device even if it is unplugged right now
                cb.Items.Add(selected);
            }

            cb.SelectedItem = string.IsNullOrEmpty(selected) ? NoDevice : selected;
            cb.EndUpdate();
        }

        #endregion Behaviour
    }
}
