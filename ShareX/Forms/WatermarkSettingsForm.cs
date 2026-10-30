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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ShareX
{
    public class WatermarkSettingsForm : Form
    {
        private const int PreviewWidth = 384;
        private readonly int PreviewHeight;
        private readonly Size screenSize;
        private Bitmap screenshot;
        private double customX, customY;
        private bool dragging;

        private readonly VideoWatermarkSettings settings;

        private CheckBox cbEnabled, cbClips, cbRecordings;
        private TextBox txtImage;
        private ComboBox cbPosition;
        private NumericUpDown nudSize, nudOpacity, nudMargin;
        private Panel pPreview;
        private Label lblStatus;
        private Bitmap previewImage;
        private string previewImagePath;

        public WatermarkSettingsForm(VideoWatermarkSettings settings)
        {
            this.settings = settings;

            // The preview shows the real screen, so placement matches what gets recorded
            Rectangle screen = Screen.FromPoint(Cursor.Position).Bounds;
            screenSize = screen.Size;
            PreviewHeight = Math.Max(100, (int)(PreviewWidth * (double)screen.Height / Math.Max(1, screen.Width)));
            screenshot = CaptureScreen(screen, new Size(PreviewWidth, PreviewHeight));

            Text = "ShareX - Watermark";
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

        private void BuildLayout()
        {
            TableLayoutPanel table = new TableLayoutPanel()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                Dock = DockStyle.Fill
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(table);

            cbEnabled = new CheckBox() { Text = "Add a watermark to videos", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
            cbEnabled.CheckedChanged += (s, e) => UpdateEnabled();
            AddFull(table, cbEnabled);

            txtImage = new TextBox() { Width = 260, PlaceholderText = "PNG, JPG or GIF" };
            txtImage.TextChanged += (s, e) => pPreview?.Invalidate();
            Button btnImage = new Button() { Text = "...", Width = 32 };
            btnImage.Click += (s, e) => FileHelpers.BrowseFile(txtImage, "", false, "Images (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif");
            AddRow(table, "Image", txtImage, btnImage);

            cbPosition = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            cbPosition.Items.AddRange(new object[] { "Top left", "Top right", "Bottom left", "Bottom right", "Center", "Custom (drag in preview)" });
            cbPosition.SelectedIndexChanged += (s, e) => pPreview?.Invalidate();
            AddRow(table, "Position", cbPosition);

            nudSize = AddNumeric(table, "Size (%)", 1, 100, 1);
            nudOpacity = AddNumeric(table, "Opacity (%)", 5, 100, 5);
            nudMargin = AddNumeric(table, "Margin (%)", 0, 20, 1);

            Label lblApply = new Label() { Text = "Apply to", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 14, 3, 4) };
            AddFull(table, lblApply);
            cbClips = new CheckBox() { Text = "Replay buffer clips", AutoSize = true };
            AddFull(table, cbClips);
            cbRecordings = new CheckBox() { Text = "Screen recordings (MP4)", AutoSize = true };
            AddFull(table, cbRecordings);

            lblStatus = new Label() { AutoSize = true, MaximumSize = new Size(PreviewWidth + 420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3),
                Text = "Drag the watermark in the preview to place it. Size follows the captured area, so small selections get a smaller watermark. Added when the video is saved; GIF recordings and screenshots are not affected." };
            AddFull(table, lblStatus);

            pPreview = new Panel() { Size = new Size(PreviewWidth, PreviewHeight), Margin = new Padding(16, 3, 3, 3), BackColor = Color.FromArgb(40, 44, 52) };
            pPreview.Paint += Preview_Paint;
            pPreview.Cursor = Cursors.Hand;
            pPreview.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && cbEnabled.Checked) { dragging = true; MoveWatermark(e.Location); } };
            pPreview.MouseMove += (s, e) => { if (dragging) MoveWatermark(e.Location); };
            pPreview.MouseUp += (s, e) => dragging = false;
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(pPreview, true);
            table.Controls.Add(pPreview, 2, 1);
            table.SetRowSpan(pPreview, 5);

            FlowLayoutPanel buttons = new FlowLayoutPanel() { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            Button btnCancel = new Button() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
            Button btnOK = new Button() { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
            btnOK.Click += (s, e) => SaveValues();
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnOK);
            AddFull(table, buttons);

            AcceptButton = btnOK;
            CancelButton = btnCancel;
        }

        private static void AddFull(TableLayoutPanel table, Control control)
        {
            table.Controls.Add(control, 0, table.RowCount);
            table.SetColumnSpan(control, 3);
            table.RowCount++;
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control, Control extra = null)
        {
            Label lbl = new Label() { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
            table.Controls.Add(lbl, 0, table.RowCount);

            if (extra != null)
            {
                FlowLayoutPanel pair = new FlowLayoutPanel() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                pair.Controls.Add(control);
                pair.Controls.Add(extra);
                table.Controls.Add(pair, 1, table.RowCount);
            }
            else
            {
                table.Controls.Add(control, 1, table.RowCount);
            }

            table.RowCount++;
        }

        private NumericUpDown AddNumeric(TableLayoutPanel table, string label, int min, int max, int step)
        {
            NumericUpDown nud = new NumericUpDown() { Minimum = min, Maximum = max, Increment = step, Width = 90, TextAlign = HorizontalAlignment.Center };
            nud.ValueChanged += (s, e) => pPreview?.Invalidate();
            AddRow(table, label, nud);
            return nud;
        }

        private static int Clamp(int value, NumericUpDown nud)
        {
            return (int)Math.Max(nud.Minimum, Math.Min(nud.Maximum, value));
        }

        private void LoadValues()
        {
            cbEnabled.Checked = settings.Enabled;
            txtImage.Text = settings.Image;
            cbPosition.SelectedIndex = (int)settings.Position;
            nudSize.Value = Clamp(settings.Size, nudSize);
            nudOpacity.Value = Clamp(settings.Opacity, nudOpacity);
            nudMargin.Value = Clamp(settings.Margin, nudMargin);
            cbClips.Checked = settings.ApplyToClips;
            cbRecordings.Checked = settings.ApplyToRecordings;
            customX = settings.CustomX;
            customY = settings.CustomY;
            UpdateEnabled();
        }

        private void SaveValues()
        {
            settings.Enabled = cbEnabled.Checked;
            settings.Image = txtImage.Text.Trim();
            settings.Position = (WatermarkPosition)Math.Max(0, cbPosition.SelectedIndex);
            settings.Size = (int)nudSize.Value;
            settings.Opacity = (int)nudOpacity.Value;
            settings.Margin = (int)nudMargin.Value;
            settings.ApplyToClips = cbClips.Checked;
            settings.ApplyToRecordings = cbRecordings.Checked;
            settings.CustomX = customX;
            settings.CustomY = customY;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateEnabled()
        {
            bool enabled = cbEnabled.Checked;

            foreach (Control c in new Control[] { txtImage, cbPosition, nudSize, nudOpacity, nudMargin, cbClips, cbRecordings })
            {
                c.Enabled = enabled;
            }

            pPreview?.Invalidate();
        }

        private Bitmap GetPreviewImage()
        {
            string path = txtImage.Text.Trim();

            if (path == previewImagePath)
            {
                return previewImage;
            }

            previewImage?.Dispose();
            previewImage = null;
            previewImagePath = path;

            try
            {
                string fullPath = FileHelpers.GetAbsolutePath(path);

                if (File.Exists(fullPath))
                {
                    // Copied out of the stream so the file stays unlocked
                    using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(fullPath)))
                    using (Image img = Image.FromStream(ms))
                    {
                        previewImage = new Bitmap(img);
                    }
                }
            }
            catch
            {
                previewImage = null;
            }

            return previewImage;
        }

        private static Bitmap CaptureScreen(Rectangle bounds, Size size)
        {
            try
            {
                using (Bitmap full = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    }

                    Bitmap small = new Bitmap(size.Width, size.Height);

                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(full, new Rectangle(Point.Empty, size));
                    }

                    return small;
                }
            }
            catch
            {
                return null;
            }
        }

        private VideoWatermarkSettings GetCurrentSettings()
        {
            return new VideoWatermarkSettings()
            {
                Position = (WatermarkPosition)Math.Max(0, cbPosition.SelectedIndex),
                Size = (int)nudSize.Value,
                Margin = (int)nudMargin.Value,
                CustomX = customX,
                CustomY = customY
            };
        }

        private Rectangle GetPreviewRect(Bitmap img)
        {
            Rectangle rect = WatermarkManager.GetWatermarkRect(GetCurrentSettings(), screenSize, img.Size);
            double scale = (double)PreviewWidth / Math.Max(1, screenSize.Width);
            return new Rectangle((int)(rect.X * scale), (int)(rect.Y * scale), Math.Max(2, (int)(rect.Width * scale)), Math.Max(2, (int)(rect.Height * scale)));
        }

        // Centers the watermark on the cursor, converted to 0-100 within the margins
        private void MoveWatermark(Point location)
        {
            Bitmap img = GetPreviewImage();
            if (img == null) return;

            VideoWatermarkSettings current = GetCurrentSettings();
            current.Position = WatermarkPosition.TopLeft;
            Rectangle topLeft = WatermarkManager.GetWatermarkRect(current, screenSize, img.Size);
            current.Position = WatermarkPosition.BottomRight;
            Rectangle bottomRight = WatermarkManager.GetWatermarkRect(current, screenSize, img.Size);

            double scale = (double)Math.Max(1, screenSize.Width) / PreviewWidth;
            double x = location.X * scale - topLeft.Width / 2.0;
            double y = location.Y * scale - topLeft.Height / 2.0;
            double rangeX = Math.Max(1, bottomRight.X - topLeft.X);
            double rangeY = Math.Max(1, bottomRight.Y - topLeft.Y);

            customX = Math.Max(0, Math.Min(100, (x - topLeft.X) / rangeX * 100));
            customY = Math.Max(0, Math.Min(100, (y - topLeft.Y) / rangeY * 100));

            if (cbPosition.SelectedIndex != (int)WatermarkPosition.Custom)
            {
                cbPosition.SelectedIndex = (int)WatermarkPosition.Custom;
            }

            pPreview.Invalidate();
        }

        private void Preview_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (screenshot != null)
            {
                g.DrawImage(screenshot, 0, 0, PreviewWidth, PreviewHeight);
            }
            else
            {
                using (Brush text = new SolidBrush(Color.FromArgb(150, 160, 170)))
                using (StringFormat center = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("Preview", Font, text, new RectangleF(0, 0, PreviewWidth, PreviewHeight), center);
                }
            }

            Bitmap img = GetPreviewImage();

            if (img == null || !cbEnabled.Checked)
            {
                return;
            }

            Rectangle rect = GetPreviewRect(img);

            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix() { Matrix33 = (float)nudOpacity.Value / 100f };
                attributes.SetColorMatrix(matrix);
                g.DrawImage(img, rect, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attributes);
            }

            if (dragging || cbPosition.SelectedIndex == (int)WatermarkPosition.Custom)
            {
                using (Pen pen = new Pen(Color.FromArgb(200, 255, 255, 255)) { DashStyle = DashStyle.Dash })
                {
                    g.DrawRectangle(pen, rect);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                previewImage?.Dispose();
                screenshot?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
