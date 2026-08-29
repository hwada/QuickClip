using System;

namespace QuickClip;

internal readonly record struct DetectedRectangle(int X, int Y, int Width, int Height);

internal static class ImageRectangleDetector
{
    private const int MinimumSideLength = 16;
    private const double MinimumEdgeCoverage = 0.35;

    internal static DetectedRectangle? FindLargest(byte[] bgraPixels, int width, int height)
    {
        if (width < MinimumSideLength || height < MinimumSideLength)
        {
            return null;
        }

        byte[] grayscale = ConvertToGrayscale(bgraPixels, width, height);
        int threshold = CalculateOtsuThreshold(grayscale);
        bool[] edges = CreateBinaryEdges(grayscale, width, height, threshold);
        bool[] connectedEdges = Dilate(edges, width, height);

        return FindLargestRectangularComponent(edges, connectedEdges, width, height);
    }

    private static byte[] ConvertToGrayscale(byte[] pixels, int width, int height)
    {
        int pixelCount = checked(width * height);
        if (pixels.Length < checked(pixelCount * 4))
        {
            throw new ArgumentException("The BGRA pixel buffer is too small.", nameof(pixels));
        }

        var grayscale = new byte[pixelCount];

        for (int pixelIndex = 0, byteIndex = 0;
             pixelIndex < pixelCount;
             pixelIndex++, byteIndex += 4)
        {
            int blue = pixels[byteIndex];
            int green = pixels[byteIndex + 1];
            int red = pixels[byteIndex + 2];
            grayscale[pixelIndex] = (byte)((red * 77 + green * 150 + blue * 29) >> 8);
        }

        return grayscale;
    }

    private static int CalculateOtsuThreshold(byte[] grayscale)
    {
        var histogram = new int[256];
        long totalIntensity = 0;

        foreach (byte intensity in grayscale)
        {
            histogram[intensity]++;
            totalIntensity += intensity;
        }

        long backgroundIntensity = 0;
        int backgroundCount = 0;
        double maximumVariance = -1;
        int bestThreshold = 127;

        for (int threshold = 0; threshold < histogram.Length; threshold++)
        {
            backgroundCount += histogram[threshold];
            if (backgroundCount == 0)
            {
                continue;
            }

            int foregroundCount = grayscale.Length - backgroundCount;
            if (foregroundCount == 0)
            {
                break;
            }

            backgroundIntensity += (long)threshold * histogram[threshold];
            double backgroundMean = (double)backgroundIntensity / backgroundCount;
            double foregroundMean = (double)(totalIntensity - backgroundIntensity) / foregroundCount;
            double difference = backgroundMean - foregroundMean;
            double variance = (double)backgroundCount * foregroundCount * difference * difference;

            if (variance > maximumVariance)
            {
                maximumVariance = variance;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static bool[] CreateBinaryEdges(byte[] grayscale, int width, int height, int threshold)
    {
        var edges = new bool[grayscale.Length];

        for (int y = 1; y < height - 1; y++)
        {
            int rowStart = y * width;

            for (int x = 1; x < width - 1; x++)
            {
                int index = rowStart + x;
                bool value = grayscale[index] > threshold;

                edges[index] = value != (grayscale[index - 1] > threshold)
                    || value != (grayscale[index + 1] > threshold)
                    || value != (grayscale[index - width] > threshold)
                    || value != (grayscale[index + width] > threshold);
            }
        }

        return edges;
    }

    private static bool[] Dilate(bool[] edges, int width, int height)
    {
        var dilated = new bool[edges.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int index = y * width + x;
                if (!edges[index])
                {
                    continue;
                }

                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    int neighborRow = (y + offsetY) * width;
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        dilated[neighborRow + x + offsetX] = true;
                    }
                }
            }
        }

        return dilated;
    }

    private static DetectedRectangle? FindLargestRectangularComponent(
        bool[] originalEdges,
        bool[] connectedEdges,
        int width,
        int height)
    {
        var visited = new bool[connectedEdges.Length];
        var queue = new int[connectedEdges.Length];
        DetectedRectangle? largest = null;
        long largestArea = 0;

        for (int start = 0; start < connectedEdges.Length; start++)
        {
            if (!connectedEdges[start] || visited[start])
            {
                continue;
            }

            int queueStart = 0;
            int queueEnd = 0;
            queue[queueEnd++] = start;
            visited[start] = true;

            int minX = start % width;
            int maxX = minX;
            int minY = start / width;
            int maxY = minY;

            while (queueStart < queueEnd)
            {
                int current = queue[queueStart++];
                int currentX = current % width;
                int currentY = current / width;

                minX = Math.Min(minX, currentX);
                maxX = Math.Max(maxX, currentX);
                minY = Math.Min(minY, currentY);
                maxY = Math.Max(maxY, currentY);

                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    int neighborY = currentY + offsetY;
                    if ((uint)neighborY >= (uint)height)
                    {
                        continue;
                    }

                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        int neighborX = currentX + offsetX;
                        if ((uint)neighborX >= (uint)width)
                        {
                            continue;
                        }

                        int neighbor = neighborY * width + neighborX;
                        if (connectedEdges[neighbor] && !visited[neighbor])
                        {
                            visited[neighbor] = true;
                            queue[queueEnd++] = neighbor;
                        }
                    }
                }
            }

            int rectangleWidth = maxX - minX + 1;
            int rectangleHeight = maxY - minY + 1;
            long area = (long)rectangleWidth * rectangleHeight;
            int detectedLeft = minX + 1;
            int detectedTop = minY + 1;
            int detectedRight = maxX - 1;
            int detectedBottom = maxY - 1;

            if (rectangleWidth >= MinimumSideLength
                && rectangleHeight >= MinimumSideLength
                && area > largestArea
                && HasFourRectangleEdges(
                    originalEdges,
                    width,
                    height,
                    detectedLeft,
                    detectedTop,
                    detectedRight,
                    detectedBottom))
            {
                largestArea = area;
                largest = new DetectedRectangle(
                    detectedLeft,
                    detectedTop,
                    detectedRight - detectedLeft + 1,
                    detectedBottom - detectedTop + 1);
            }
        }

        return largest;
    }

    private static bool HasFourRectangleEdges(
        bool[] edges,
        int imageWidth,
        int imageHeight,
        int left,
        int top,
        int right,
        int bottom)
    {
        int horizontalLength = right - left + 1;
        int verticalLength = bottom - top + 1;
        int topCoverage = CountHorizontalCoverage(edges, imageWidth, imageHeight, left, right, top);
        int bottomCoverage = CountHorizontalCoverage(edges, imageWidth, imageHeight, left, right, bottom);
        int leftCoverage = CountVerticalCoverage(edges, imageWidth, imageHeight, top, bottom, left);
        int rightCoverage = CountVerticalCoverage(edges, imageWidth, imageHeight, top, bottom, right);

        return topCoverage >= horizontalLength * MinimumEdgeCoverage
            && bottomCoverage >= horizontalLength * MinimumEdgeCoverage
            && leftCoverage >= verticalLength * MinimumEdgeCoverage
            && rightCoverage >= verticalLength * MinimumEdgeCoverage;
    }

    private static int CountHorizontalCoverage(
        bool[] edges,
        int width,
        int height,
        int left,
        int right,
        int centerY)
    {
        int count = 0;

        for (int x = left; x <= right; x++)
        {
            bool found = false;
            for (int y = Math.Max(0, centerY - 2); y <= Math.Min(height - 1, centerY + 2); y++)
            {
                found |= edges[y * width + x];
            }

            if (found)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVerticalCoverage(
        bool[] edges,
        int width,
        int height,
        int top,
        int bottom,
        int centerX)
    {
        int count = 0;

        for (int y = top; y <= bottom; y++)
        {
            bool found = false;
            for (int x = Math.Max(0, centerX - 2); x <= Math.Min(width - 1, centerX + 2); x++)
            {
                found |= edges[y * width + x];
            }

            if (found)
            {
                count++;
            }
        }

        return count;
    }
}
