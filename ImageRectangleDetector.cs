using OpenCvSharp;
using System;

namespace QuickClip;

internal readonly record struct DetectedRectangle(int X, int Y, int Width, int Height);

internal static class ImageRectangleDetector
{
    private const int MinimumSideLength = 10;
    private const double MinimumRectangularity = 0.70;
    private const double FullImageAreaRatio = 0.98;

    internal static DetectedRectangle? FindLargest(byte[] bgraPixels, int width, int height)
    {
        if (width < MinimumSideLength || height < MinimumSideLength)
        {
            return null;
        }

        int requiredBufferLength = checked(width * height * 4);
        if (bgraPixels.Length < requiredBufferLength)
        {
            throw new ArgumentException("The BGRA pixel buffer is too small.", nameof(bgraPixels));
        }

        using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgraPixels);
        using var grayscale = new Mat();
        using var binary = new Mat();
        using var inverted = new Mat();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

        Cv2.CvtColor(source, grayscale, ColorConversionCodes.BGRA2GRAY);
        Cv2.Threshold(
            grayscale,
            binary,
            0,
            255,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);

        // Close one-pixel gaps before contour extraction, then inspect both polarities.
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        Cv2.BitwiseNot(binary, inverted);
        Cv2.MorphologyEx(inverted, inverted, MorphTypes.Close, kernel);

        DetectedRectangle? largest = null;
        long largestArea = 0;
        FindLargestContour(binary, width, height, ref largest, ref largestArea);
        FindLargestContour(inverted, width, height, ref largest, ref largestArea);

        return largest;
    }

    private static void FindLargestContour(
        Mat binary,
        int imageWidth,
        int imageHeight,
        ref DetectedRectangle? largest,
        ref long largestArea)
    {
        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        long imageArea = (long)imageWidth * imageHeight;

        foreach (Point[] contour in contours)
        {
            double perimeter = Cv2.ArcLength(contour, closed: true);
            if (perimeter <= 0)
            {
                continue;
            }

            Point[] polygon = Cv2.ApproxPolyDP(contour, perimeter * 0.02, closed: true);
            if (polygon.Length != 4 || !Cv2.IsContourConvex(polygon))
            {
                continue;
            }

            Rect bounds = Cv2.BoundingRect(polygon);
            if (bounds.Width < MinimumSideLength || bounds.Height < MinimumSideLength)
            {
                continue;
            }

            long boundingArea = (long)bounds.Width * bounds.Height;
            double contourArea = Math.Abs(Cv2.ContourArea(polygon));
            if (contourArea / boundingArea < MinimumRectangularity)
            {
                continue;
            }

            // The white background itself is also a contour; it is not a detected object.
            bool isFullImageFrame = boundingArea >= imageArea * FullImageAreaRatio
                && bounds.X <= 1
                && bounds.Y <= 1
                && bounds.Right >= imageWidth - 1
                && bounds.Bottom >= imageHeight - 1;

            if (isFullImageFrame || boundingArea <= largestArea)
            {
                continue;
            }

            largestArea = boundingArea;
            largest = new DetectedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }
}
