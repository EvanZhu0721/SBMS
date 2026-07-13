using System;
using System.Globalization;

namespace SBMSGui
{
    internal struct Resolution
    {
        public int Width;
        public int Height;
    }

    internal static class ResolutionMath
    {
        public static Resolution CalculatePhysicalSource(Resolution primary, Resolution target, double primarySize, double targetSize)
        {
            double primaryPhysicalWidth = CalculatePhysicalWidth(primary, primarySize);
            double targetPhysicalWidth = CalculatePhysicalWidth(target, targetSize);
            if (primaryPhysicalWidth <= 0.0 || targetPhysicalWidth <= 0.0 || target.Width <= 0)
            {
                return new Resolution { Width = 1, Height = 1 };
            }

            double primaryPixelsPerInchX = primary.Width / primaryPhysicalWidth;
            int width = RoundEven(targetPhysicalWidth * primaryPixelsPerInchX);
            int height = RoundEven(width * target.Height / (double)target.Width);
            return new Resolution { Width = Math.Max(width, 1), Height = Math.Max(height, 1) };
        }

        public static double CalculatePhysicalWidth(Resolution resolution, double diagonalInches)
        {
            double width = resolution.Width;
            double height = resolution.Height;
            double diagonalPixels = Math.Sqrt(width * width + height * height);
            return diagonalPixels <= 0.0 ? 0.0 : diagonalInches * width / diagonalPixels;
        }

        public static Resolution CalculateQualitySource(Resolution primary, Resolution target, double primarySize, double targetSize)
        {
            Resolution physical = CalculatePhysicalSource(primary, target, primarySize, targetSize);
            int bestScale = 1;
            int bestError = int.MaxValue;
            for (int scale = 1; scale <= 4; ++scale)
            {
                int width = target.Width * scale;
                int error = Math.Abs(width - physical.Width);
                if (error < bestError)
                {
                    bestError = error;
                    bestScale = scale;
                }
            }
            return new Resolution { Width = target.Width * bestScale, Height = target.Height * bestScale };
        }

        public static bool IsExact2x(Resolution source, Resolution target)
        {
            return source.Width == target.Width * 2 && source.Height == target.Height * 2;
        }

        public static int RoundEven(double value)
        {
            int rounded = (int)Math.Round(value);
            return (rounded % 2 == 0) ? rounded : rounded + 1;
        }

        public static int GreatestCommonDivisor(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return Math.Max(a, 1);
        }

        public static bool TryParseResolution(string text, out Resolution resolution)
        {
            resolution = new Resolution();
            if (text == null)
            {
                return false;
            }
            string[] parts = text.Trim().ToLowerInvariant().Split('x');
            if (parts.Length != 2)
            {
                return false;
            }
            int width;
            int height;
            if (!int.TryParse(parts[0].Trim(), out width) || !int.TryParse(parts[1].Trim(), out height))
            {
                return false;
            }
            resolution.Width = width;
            resolution.Height = height;
            return true;
        }

        public static bool TryParseSize(string text, out double value)
        {
            value = 0.0;
            if (text == null)
            {
                return false;
            }
            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseAspect(string text, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (text == null)
            {
                return false;
            }
            string normalized = text.Trim().Replace('：', ':').Replace('/', ':');
            string[] parts = normalized.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0].Trim(), out width) ||
                !int.TryParse(parts[1].Trim(), out height) ||
                width <= 0 ||
                height <= 0)
            {
                return false;
            }
            return true;
        }

        public static string Format(Resolution resolution)
        {
            return resolution.Width + "x" + resolution.Height;
        }

        public static void BuildParts(Resolution resolution, out int horizontal, out string aspect, out string orientation)
        {
            bool portrait = resolution.Height > resolution.Width;
            horizontal = portrait ? resolution.Height : resolution.Width;
            int aspectW = Math.Max(resolution.Width, resolution.Height);
            int aspectH = Math.Min(resolution.Width, resolution.Height);
            int divisor = GreatestCommonDivisor(aspectW, aspectH);
            aspect = (aspectW / divisor).ToString(CultureInfo.InvariantCulture) + ":" + (aspectH / divisor).ToString(CultureInfo.InvariantCulture);
            orientation = portrait ? "竖屏" : "横屏";
        }
    }
}
