using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace HshDetectionService;

/// <summary>One worker owns this storage; it is reused until the viewport size changes.</summary>
internal sealed class ReusableVideoFrameBuffer : IDisposable
{
    private Bitmap? _bgr;
    private Graphics? _graphics;
    private readonly Mat _i420 = new();
    private byte[] _sample = Array.Empty<byte>();

    public int Width => _bgr?.Width ?? 0;
    public int Height => _bgr?.Height ?? 0;

    public byte[] Prepare(Bitmap source, Size maxBounds)
    {
        Size size = VideoFrameGeometry.Fit(source.Size, maxBounds);
        if (_bgr is null || _bgr.Size != size)
        {
            _graphics?.Dispose();
            _bgr?.Dispose();
            _bgr = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            _graphics = Graphics.FromImage(_bgr);
            _graphics.CompositingMode = CompositingMode.SourceCopy;
            _graphics.CompositingQuality = CompositingQuality.HighSpeed;
            _graphics.InterpolationMode = InterpolationMode.Bilinear;
            _graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            _sample = new byte[size.Width * size.Height * 3 / 2];
        }

        _graphics!.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        _graphics.Flush(FlushIntention.Sync);
        BitmapData data = _bgr.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            using var view = new Mat(size.Height, size.Width, DepthType.Cv8U, 3, data.Scan0, data.Stride);
            CvInvoke.CvtColor(view, _i420, ColorConversion.Bgr2YuvI420);
            Marshal.Copy(_i420.DataPointer, _sample, 0, _sample.Length);
        }
        finally { _bgr.UnlockBits(data); }
        return _sample;
    }

    public void Dispose()
    {
        _graphics?.Dispose();
        _graphics = null;
        _bgr?.Dispose();
        _bgr = null;
        _i420.Dispose();
        _sample = Array.Empty<byte>();
    }
}

internal sealed class Vp8CameraFrameEncoder : IVideoFrameEncoder
{
    private VP8Codec? _codec;
    private readonly ReusableVideoFrameBuffer _buffer = new();
    private Size _previousSize;

    public EncodedVideoFrame Encode(Bitmap source, Size maxBounds, bool forceKeyFrame)
    {
        byte[] sample = _buffer.Prepare(source, maxBounds);
        var size = new Size(_buffer.Width, _buffer.Height);
        if (_codec is null || size != _previousSize)
        {
            // SIPSorcery.VP8 10.0.16 retains grow-only Y/U/V arrays, but its
            // keyframe encoder requires exact plane lengths. A forced keyframe
            // alone therefore fails after downsizing ("srcY size mismatch").
            _codec?.Dispose();
            _codec = new VP8Codec();
            _previousSize = size;
            forceKeyFrame = true;
        }
        if (forceKeyFrame) _codec.ForceKeyFrame();
        byte[] encoded = _codec.EncodeVideo(size.Width, size.Height, sample, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);
        return new EncodedVideoFrame(size.Width, size.Height, encoded, encoded.Length > 0 && (encoded[0] & 1) == 0);
    }

    public void Dispose()
    {
        _codec?.Dispose();
        _codec = null;
        _buffer.Dispose();
    }
}
