using System.Reflection;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin.Capture;

internal static partial class Program
{
    private static Tuple<CapProp, int>[] DecoderOpenProperties(bool liveRtsp) =>
        (Tuple<CapProp, int>[])typeof(FrameSource)
            .GetMethod("GetFfmpegOpenProperties", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [liveRtsp])!;

    private static void DecoderThreadOpenSettings()
    {
        foreach (bool liveRtsp in new[] { false, true })
        {
            Tuple<CapProp, int>[] properties = DecoderOpenProperties(liveRtsp);
            Check(properties.Count(p => p.Item1 == CapProp.OpenTimeoutMsec && p.Item2 == 5000) == 1 &&
                properties.Count(p => p.Item1 == CapProp.ReadTimeoutMsec && p.Item2 == 5000) == 1,
                "Decoder configuration changed capture timeouts.");
            Tuple<CapProp, int>[] decoderThreads = properties.Where(p => (int)p.Item1 == 70).ToArray();
            Check(liveRtsp
                    ? decoderThreads.Length == 1 && decoderThreads[0].Item2 == 1 && properties.Length == 3
                    : decoderThreads.Length == 0 && properties.Length == 2,
                "RTSP should always request one decoder thread at open time.");
        }
    }

    private static void DecoderThreadFormatOptions()
    {
        const string environmentName = "OPENCV_FFMPEG_CAPTURE_OPTIONS";
        MethodInfo readNativeEnvironment = typeof(FrameSource)
            .GetMethod("GetNativeEnvironmentVariable", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? nativeBefore = (string?)readNativeEnvironment.Invoke(null, [environmentName]);
        string? managedBefore = Environment.GetEnvironmentVariable(environmentName);
        MethodInfo optionsMethod = typeof(FrameSource)
            .GetMethod("GetFfmpegCaptureOptions", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string transport in new[] { "TCP", "UDP" })
        {
            string options = (string)optionsMethod.Invoke(null, [transport])!;
            string expected = $"rtsp_transport;{transport.ToLowerInvariant()}|fflags;nobuffer|max_delay;0";
            Check(options == expected,
                "RTSP format options contain an ineffective codec option or changed transport.");
            _ = DecoderOpenProperties(liveRtsp: true);
        }
        Check((string?)readNativeEnvironment.Invoke(null, [environmentName]) == nativeBefore &&
            Environment.GetEnvironmentVariable(environmentName) == managedBefore,
            "Constructing capture options changed process-wide FFmpeg settings.");
    }

    private static void DecoderThreadNativeOpen()
    {
        // A local generated AVI verifies the actual bundled native backend
        // accepts property 70. It does not require a camera or network access.
        string fixturePath = Path.Combine(Path.GetTempPath(), $"decoder-threads-{Guid.NewGuid():N}.avi");
        const string environmentName = "OPENCV_FFMPEG_CAPTURE_OPTIONS";
        MethodInfo readNativeEnvironment = typeof(FrameSource)
            .GetMethod("GetNativeEnvironmentVariable", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? nativeBefore = (string?)readNativeEnvironment.Invoke(null, [environmentName]);
        try
        {
            var size = new Size(64, 48);
            using (var writer = new VideoWriter(fixturePath, (int)VideoCapture.API.Ffmpeg,
                VideoWriter.Fourcc('M', 'J', 'P', 'G'), 8, size, true))
            {
                Check(writer.IsOpened, "The native FFmpeg writer could not create a local MJPG fixture.");
                using var frame = new Mat(size.Height, size.Width, DepthType.Cv8U, 3);
                for (int index = 0; index < 8; index++)
                {
                    frame.SetTo(new MCvScalar(index * 20, 40, 100));
                    writer.Write(frame);
                }
            }

            using (var capture = new VideoCapture(fixturePath, VideoCapture.API.Ffmpeg,
                DecoderOpenProperties(liveRtsp: true)))
            {
                Check(capture.IsOpened && capture.Get((CapProp)70) == 1,
                    "The bundled native FFmpeg backend did not apply the open-only decoder thread cap.");
                using var decoded = new Mat();
                Check(capture.Grab() && capture.Retrieve(decoded) && decoded.Size == size,
                    "The decoder thread cap prevented native frame decoding.");
                var diagnostics = (FrameSource.CaptureDiagnostics)typeof(FrameSource)
                    .GetMethod("ReadCaptureDiagnostics", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [capture])!;
                Check(diagnostics.DecoderThreads == 1 && diagnostics.Width == size.Width &&
                    diagnostics.Height == size.Height && Math.Abs(diagnostics.FramesPerSecond - 8) < .01,
                    "Capture diagnostics did not report effective native stream properties.");
            }

            // Opening ordinary playback afterward must still use its normal
            // settings; the live-only request must not escape through globals.
            using var playback = new VideoCapture(fixturePath, VideoCapture.API.Ffmpeg,
                DecoderOpenProperties(liveRtsp: false));
            Check(playback.IsOpened, "A preceding live decoder request affected ordinary file playback.");
            Check((string?)readNativeEnvironment.Invoke(null, [environmentName]) == nativeBefore,
                "Opening the native decoder fixture changed shared FFmpeg capture options.");
        }
        finally
        {
            if (File.Exists(fixturePath)) File.Delete(fixturePath);
        }
    }
}
