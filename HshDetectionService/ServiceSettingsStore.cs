using System.Text.Json;
using HshDetectionEngin;

namespace HshDetectionService;

public sealed class ServiceSettingsStore
{
    private readonly object _gate = new();
    private readonly ServicePaths _paths;
    private ServiceSettingsDocument _service = new();
    private AppSettings _detection = new();

    public ServiceSettingsStore(ServicePaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
        Reload();
    }

    public ServiceSettingsDocument Service
    {
        get { lock (_gate) return JsonClone(_service); }
    }

    public AppSettings Detection
    {
        get { lock (_gate) return JsonClone(_detection); }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _service = LoadFile(_paths.ServiceSettingsPath, new ServiceSettingsDocument());
            _service.Http ??= new ServiceHttpSettings();
            _service.Http.ListenUrls ??= ["http://127.0.0.1:5080"];
            _service.Http.CorsOrigins ??= ["http://127.0.0.1:5173", "http://localhost:5173"];
            _service.Security ??= new ServiceSecuritySettings();
            _service.Runtime ??= new ServiceRuntimeSettings();
            _service.Association ??= new ServiceAssociationSettings();
            _service.Retention ??= new ServiceRetentionSettings();
            _service.Triggers ??= [];
            NormalizeTriggers(_service.Triggers);
            if (string.IsNullOrWhiteSpace(_service.ServiceNodeId)) _service.ServiceNodeId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(_service.Security.ApiKey)) _service.Security.ApiKey = Guid.NewGuid().ToString("N");

            _detection = LoadFile(_paths.DetectionSettingsPath, new AppSettings());
            _detection.Cameras ??= [];
            foreach (CameraSettings camera in _detection.Cameras)
            {
                camera.Id = string.IsNullOrWhiteSpace(camera.Id) ? Guid.NewGuid().ToString("N") : camera.Id;
                camera.EnsureProcessingDefaults();
            }

            if (!File.Exists(_paths.ServiceSettingsPath)) SaveServiceUnsafe();
            if (!File.Exists(_paths.DetectionSettingsPath)) SaveDetectionUnsafe();
        }
    }

    public void SaveDetection(AppSettings settings, long expectedRevision = -1)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (expectedRevision >= 0 && _service.Revision != expectedRevision)
                throw new ConfigurationConflictException(_service.Revision, expectedRevision);

            foreach (CameraSettings camera in settings.Cameras ?? []) camera.EnsureProcessingDefaults();
            _detection = settings;
            _service.Revision++;
            SaveDetectionUnsafe();
            SaveServiceUnsafe();
        }
    }

    public void SaveService(ServiceSettingsDocument settings, long expectedRevision = -1)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (expectedRevision >= 0 && _service.Revision != expectedRevision)
                throw new ConfigurationConflictException(_service.Revision, expectedRevision);

            settings.Revision = _service.Revision + 1;
            settings.ServiceNodeId = string.IsNullOrWhiteSpace(settings.ServiceNodeId) ? _service.ServiceNodeId : settings.ServiceNodeId;
            settings.Security ??= new ServiceSecuritySettings();
            settings.Http ??= new ServiceHttpSettings();
            settings.Http.ListenUrls ??= ["http://127.0.0.1:5080"];
            settings.Http.CorsOrigins ??= ["http://127.0.0.1:5173", "http://localhost:5173"];
            settings.Runtime ??= new ServiceRuntimeSettings();
            settings.Retention ??= new ServiceRetentionSettings();
            settings.Triggers ??= [];
            NormalizeTriggers(settings.Triggers);
            _service = settings;
            SaveServiceUnsafe();
        }
    }

    private void SaveDetectionUnsafe()
    {
        WriteAtomic(_paths.DetectionSettingsPath, JsonSerializer.Serialize(_detection, ServiceJson.Options));
    }

    private void SaveServiceUnsafe()
    {
        WriteAtomic(_paths.ServiceSettingsPath, JsonSerializer.Serialize(_service, ServiceJson.Options));
    }

    private static T LoadFile<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ServiceJson.Options) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
        else File.Move(temporary, path);
    }

    private static T JsonClone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ServiceJson.Options), ServiceJson.Options)!;
    }

    private static void NormalizeTriggers(IList<TriggerDefinition> triggers)
    {
        foreach (TriggerDefinition trigger in triggers)
        {
            trigger.Id = string.IsNullOrWhiteSpace(trigger.Id) ? Guid.NewGuid().ToString("N") : trigger.Id;
            trigger.Name = string.IsNullOrWhiteSpace(trigger.Name) ? "Trigger" : trigger.Name.Trim();
            trigger.CameraIds ??= [];
            trigger.TaskIds ??= [];
            trigger.Kinds ??= [];
            trigger.Actions ??= [];
            trigger.CooldownSeconds = Math.Clamp(trigger.CooldownSeconds, 0, 3600);
        }
    }
}

public sealed class ConfigurationConflictException : InvalidOperationException
{
    public ConfigurationConflictException(long actualRevision, long expectedRevision)
        : base($"Configuration revision conflict. Expected {expectedRevision}, actual {actualRevision}.")
    {
        ActualRevision = actualRevision;
        ExpectedRevision = expectedRevision;
    }

    public long ActualRevision { get; }
    public long ExpectedRevision { get; }
}
