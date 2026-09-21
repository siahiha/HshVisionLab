namespace HshDetectionEngin;

public sealed class Camera : CameraRuntime
{
    public CameraSourceSettings Source { get; }
    public CameraMotionSettings Motion { get; }
    public IList<NamedRoi> Roi => Settings.Rois;
    public CameraSettings Configuration => Settings;

    public Camera(
        CameraSettings settings,
        IFrameSource? source = null,
        ProcessingRegistry? processingRegistry = null,
        HshDetectionEngin.Licensing.LicenseValidationResult? license = null)
        : base(settings, source, processingRegistry, license)
    {
        Source = new CameraSourceSettings(Settings);
        Motion = new CameraMotionSettings(Settings);
    }

    public Camera(string name, string sourceUrl)
        : this(new CameraSettings { Name = name, SourceUrl = sourceUrl }) { }
}

public sealed class CameraSourceSettings
{
    private readonly CameraSettings _settings;
    internal CameraSourceSettings(CameraSettings settings) => _settings = settings;
    public string Url { get => _settings.SourceUrl; set => _settings.SourceUrl = value ?? string.Empty; }
    public string Transport { get => _settings.Transport; set => _settings.Transport = value ?? "TCP"; }
}

public sealed class CameraMotionSettings
{
    private readonly CameraSettings _settings;
    internal CameraMotionSettings(CameraSettings settings) => _settings = settings;
    public bool Enabled { get => _settings.MotionGateEnabled; set => _settings.MotionGateEnabled = value; }
    public int SamplingFps { get => _settings.MotionFps; set => _settings.MotionFps = Math.Max(1, value); }
    public double Threshold { get => _settings.MotionThreshold; set => _settings.MotionThreshold = value; }
    public double ChangedPercent { get => _settings.MotionChangedPercent; set => _settings.MotionChangedPercent = value; }
    public double RoiScalePercent { get => _settings.MotionRoiScalePercent; set => _settings.MotionRoiScalePercent = value; }
    public int HoldMilliseconds { get => _settings.MotionHoldMs; set => _settings.MotionHoldMs = Math.Max(100, value); }
}
