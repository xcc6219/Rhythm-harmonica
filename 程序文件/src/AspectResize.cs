using System;
using System.Drawing;

namespace HarmonicaStudio
{
    internal static class AspectResize
    {
        internal static Rectangle Fit(Rectangle proposal, int edge, double ratio, Size minimum, Size maximum)
        {
            if (ratio <= 0 || Double.IsNaN(ratio) || Double.IsInfinity(ratio)) throw new ArgumentOutOfRangeException("ratio");
            bool vertical = edge == 3 || edge == 6;
            double width = vertical ? proposal.Height * ratio : proposal.Width;
            double lower = Math.Max(minimum.Width, minimum.Height * ratio);
            double upper = Math.Min(maximum.Width, maximum.Height * ratio);
            bool minimumFits = upper >= lower;
            if (upper < lower) lower = upper;
            width = Math.Max(lower, Math.Min(width, upper));
            int w = Math.Max(1, (int)Math.Round(width)), h = Math.Max(1, (int)Math.Round(width / ratio));
            w = Math.Max(minimumFits ? minimum.Width : 1, Math.Min(maximum.Width, w));
            h = Math.Max(minimumFits ? minimum.Height : 1, Math.Min(maximum.Height, h));
            int left = edge == 1 || edge == 4 || edge == 7 ? proposal.Right - w : proposal.Left;
            int top = edge == 3 || edge == 4 || edge == 5 ? proposal.Bottom - h : proposal.Top;
            return new Rectangle(left, top, w, h);
        }
    }
}
