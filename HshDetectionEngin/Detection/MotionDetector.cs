using Emgu.CV.Util;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace HshDetectionEngin.Detection;

/// <summary>
/// Very cheap motion gate. It works on a tiny grayscale copy of the frame and
/// is used only to decide whether the expensive YOLO inference should run.
/// </summary>
public sealed class MotionDetector : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly double _threshold;
    private readonly double _minChangedPercent;
    private Mat? _previous;
    private Mat? _resized;
    private Mat? _current;
    private Mat? _diff;

    public double LastChangedPercent { get; private set; }

    public MotionDetector(int width = 320, int height = 180, double threshold = 18, double minChangedPercent = 0.45)
    {
        _width = Math.Max(64, width);
        _height = Math.Max(36, height);
        _threshold = Math.Clamp(threshold, 1, 255);
        _minChangedPercent = Math.Clamp(minChangedPercent, 0.01, 100);
        _previous = new Mat();
        _resized = new Mat();
        _current = new Mat();
        _diff = new Mat();
    }

    public bool HasMotion(Mat frame, Rectangle? roi = null, Point[]? polygon = null)
    {
        if (_previous is null || _resized is null || _current is null || _diff is null)
            return false;

        if (roi is { } r && r.Width > 0 && r.Height > 0)
        {
            using var roiMat = new Mat(frame, r);
            CvInvoke.Resize(roiMat, _resized, new Size(_width, _height), 0, 0, Inter.Linear);
        }
        else
        {
            CvInvoke.Resize(frame, _resized, new Size(_width, _height), 0, 0, Inter.Linear);
        }

        CvInvoke.CvtColor(_resized, _current, ColorConversion.Bgr2Gray);

        // For polygon ROI, black out pixels outside the polygon before the
        // frame-difference calculation. The ROI was resized above, so the
        // polygon must be mapped from the source ROI coordinates to the
        // resized motion-image coordinates before it is rasterized.
        Point[]? motionPolygon = null;
        if (polygon is { Length: >= 3 })
        {
            double scaleX = roi is { Width: > 0 } ? (double)_width / roi.Value.Width : 1.0;
            double scaleY = roi is { Height: > 0 } ? (double)_height / roi.Value.Height : 1.0;
            motionPolygon = polygon
                .Select(point => new Point(
                    Math.Clamp((int)Math.Round(point.X * scaleX), 0, _width - 1),
                    Math.Clamp((int)Math.Round(point.Y * scaleY), 0, _height - 1)))
                .ToArray();
        }

        using var mask = motionPolygon is { Length: >= 3 } ? new Mat(_height, _width, DepthType.Cv8U, 1) : null;
        if (mask is not null)
        {
            mask.SetTo(new MCvScalar(0));
            using var contours = new VectorOfVectorOfPoint();
            using var points = new VectorOfPoint(motionPolygon!);
            contours.Push(points);
            CvInvoke.FillPoly(mask, contours, new MCvScalar(255));
            CvInvoke.BitwiseAnd(_current, _current, _current, mask);
        }

        if (_previous.IsEmpty)
        {
            _current.CopyTo(_previous);
            LastChangedPercent = 100;
            return true;
        }

        CvInvoke.AbsDiff(_current, _previous, _diff);
        CvInvoke.Threshold(_diff, _diff, _threshold, 255, ThresholdType.Binary);

        int changed;
        int denominator;
        if (mask is not null)
        {
            CvInvoke.BitwiseAnd(_diff, mask, _diff);
            changed = CvInvoke.CountNonZero(_diff);
            denominator = Math.Max(1, CvInvoke.CountNonZero(mask));
        }
        else
        {
            changed = CvInvoke.CountNonZero(_diff);
            denominator = _width * _height;
        }

        LastChangedPercent = changed * 100.0 / denominator;
        _current.CopyTo(_previous);
        return LastChangedPercent >= _minChangedPercent;
    }

    public void Reset()
    {
        _previous?.SetTo(new MCvScalar(0));
        LastChangedPercent = 0;
    }

    public void Dispose()
    {
        _previous?.Dispose();
        _resized?.Dispose();
        _current?.Dispose();
        _diff?.Dispose();
        _previous = null;
        _resized = null;
        _current = null;
        _diff = null;
    }
}
