#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib
{
    internal class ScreenRecordAudioButton : NoFocusBorderButton
    {
        private bool isMicrophone = true;
        private bool muted;

        public bool IsMicrophone
        {
            get
            {
                return isMicrophone;
            }
            set
            {
                isMicrophone = value;
                Invalidate();
            }
        }

        public bool Muted
        {
            get
            {
                return muted;
            }
            set
            {
                muted = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = Width / 2f;
            float cy = Height / 2f;
            Color color = Enabled ? ForeColor : SystemColors.GrayText;

            using (Pen pen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (Brush brush = new SolidBrush(color))
            {
                if (IsMicrophone)
                {
                    g.FillEllipse(brush, cx - 4f, cy - 10f, 8f, 8f);
                    g.FillRectangle(brush, cx - 4f, cy - 6f, 8f, 8f);
                    g.FillEllipse(brush, cx - 4f, cy - 2f, 8f, 8f);
                    g.DrawArc(pen, cx - 8f, cy - 8f, 16f, 16f, 0f, 180f);
                    g.DrawLine(pen, cx, cy + 8f, cx, cy + 11f);
                    g.DrawLine(pen, cx - 4f, cy + 11f, cx + 4f, cy + 11f);
                }
                else
                {
                    PointF[] speaker =
                    {
                        new PointF(cx - 9f, cy - 3f), new PointF(cx - 5f, cy - 3f), new PointF(cx, cy - 8f),
                        new PointF(cx, cy + 8f), new PointF(cx - 5f, cy + 3f), new PointF(cx - 9f, cy + 3f)
                    };
                    g.FillPolygon(brush, speaker);

                    if (!Muted)
                    {
                        g.DrawArc(pen, cx, cy - 5f, 10f, 10f, -50f, 100f);
                        g.DrawArc(pen, cx - 4f, cy - 9f, 18f, 18f, -50f, 100f);
                    }
                }
            }

            if (Muted)
            {
                Color slash = Enabled ? Color.FromArgb(230, 60, 60) : SystemColors.GrayText;

                using (Pen pen = new Pen(slash, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - 10f, cy - 10f, cx + 10f, cy + 10f);
                }
            }
        }
    }
}
