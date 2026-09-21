namespace HshDetectionService;

public sealed class ServicePaths
{
    public ServicePaths()
    {
        string? configured = Environment.GetEnvironmentVariable("HSH_DETECTION_SERVICE_DATA_ROOT");
        Root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HshVision", "DetectionService")
            : Path.GetFullPath(configured);

        ConfigDirectory = Path.Combine(Root, "config");
        DatabaseDirectory = Path.Combine(Root, "database");
        BackupDirectory = Path.Combine(DatabaseDirectory, "backups");
        ModelDirectory = Path.Combine(Root, "models");
        LicenseDirectory = Path.Combine(Root, "license");
        MediaDirectory = Path.Combine(Root, "media");
        ArtifactDirectory = Path.Combine(MediaDirectory, "event-artifacts");
        LogDirectory = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string ConfigDirectory { get; }
    public string DatabaseDirectory { get; }
    public string BackupDirectory { get; }
    public string ModelDirectory { get; }
    public string LicenseDirectory { get; }
    public string MediaDirectory { get; }
    public string ArtifactDirectory { get; }
    public string LogDirectory { get; }
    public string DetectionSettingsPath => Path.Combine(ConfigDirectory, "settings.json");
    public string ServiceSettingsPath => Path.Combine(ConfigDirectory, "service-settings.json");
    public string FaceDatabasePath => Path.Combine(DatabaseDirectory, "face-database.db");
    public string EventDatabasePath => Path.Combine(DatabaseDirectory, "events.db");
    public string LicensePath => Path.Combine(LicenseDirectory, "license.hshlic");

    public void EnsureDirectories()
    {
        foreach (string path in new[]
        {
            Root, ConfigDirectory, DatabaseDirectory, BackupDirectory, ModelDirectory,
            LicenseDirectory, MediaDirectory, ArtifactDirectory, LogDirectory
        })
            Directory.CreateDirectory(path);
    }
}
