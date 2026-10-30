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

namespace ShareX
{
    public enum WatermarkPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Center,
        Custom
    }

    public enum WatermarkAnchor
    {
        // Placed inside whatever was recorded, always visible
        Recording,
        // Placed at a fixed spot on the screen, only visible when that spot was recorded
        Screen
    }

    public class VideoWatermarkSettings
    {
        public bool Enabled { get; set; } = false;
        public string Image { get; set; } = "";
        public WatermarkPosition Position { get; set; } = WatermarkPosition.BottomRight;
        public WatermarkAnchor Anchor { get; set; } = WatermarkAnchor.Recording;

        // Percentage of the video size (the width on a 16:9 video), so small, wide and tall captures stay in proportion
        public int Size { get; set; } = 15;
        public int Opacity { get; set; } = 85;

        // Distance from the edge, same unit as Size
        public int Margin { get; set; } = 2;

        // Custom position: 0 = left/top edge, 100 = right/bottom edge, inside the margin
        public double CustomX { get; set; } = 100;
        public double CustomY { get; set; } = 100;

        public bool ApplyToClips { get; set; } = true;
        public bool ApplyToRecordings { get; set; } = true;

        // Settings from 20.2.0.5, where the watermark lived in the replay buffer settings
        public bool Migrated { get; set; } = false;
    }
}
