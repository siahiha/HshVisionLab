namespace HshDetectionEngin;

/// <summary>
/// Context supplied when a processing module creates a pipeline instance.
/// It contains only camera/ROI information that is common to all modules.
/// Module-specific services are captured by the registration delegate.
/// </summary>
public sealed class ProcessingCreationContext
{
    public required CameraSettings Camera { get; init; }
    public required string TargetName { get; init; }
}

public sealed record ProcessingModuleDescriptor(
    ProcessingType Type,
    string DisplayName,
    AnalysisKind Kind,
    Type? OptionsType = null,
    string EditorKey = "options");

/// <summary>
/// Describes and creates one kind of processing pipeline.
/// The host registers modules once; the runtime does not need to know concrete
/// pipeline types such as Plate or Face.
/// </summary>
public interface IProcessingModule
{
    ProcessingType Type { get; }
    string DisplayName { get; }
    AnalysisKind Kind { get; }
    Type? OptionsType => null;
    string EditorKey => "options";
    string? AvailabilityMessage => null;

    /// <summary>
    /// Metadata copied to each detection produced by this module. The runtime
    /// computes it once while creating the pipeline binding, not per frame.
    /// </summary>
    IReadOnlyDictionary<string, object?> GetDetectionMetadata(CameraProcessingSettings settings) =>
        new Dictionary<string, object?>(StringComparer.Ordinal);

    IEnumerable<IProcessingPipeline> CreatePipelines(
        ProcessingCreationContext context,
        CameraProcessingSettings settings);
}

/// <summary>Simple adapter for registering a pipeline factory without adding boilerplate.</summary>
public sealed class ProcessingModuleRegistration : IProcessingModule
{
    private readonly Func<ProcessingCreationContext, CameraProcessingSettings, IEnumerable<IProcessingPipeline>> _factory;
    private readonly Func<CameraProcessingSettings, IReadOnlyDictionary<string, object?>>? _metadataFactory;

    public ProcessingType Type { get; }
    public string DisplayName { get; }
    public AnalysisKind Kind { get; }
    public Type? OptionsType { get; }
    public string EditorKey { get; }
    public string? AvailabilityMessage { get; }

    public ProcessingModuleRegistration(
        ProcessingType type,
        string displayName,
        AnalysisKind kind,
        Func<ProcessingCreationContext, CameraProcessingSettings, IEnumerable<IProcessingPipeline>> factory,
        Type? optionsType = null,
        string editorKey = "options",
        Func<CameraProcessingSettings, IReadOnlyDictionary<string, object?>>? metadataFactory = null,
        string? availabilityMessage = null)
    {
        if (string.IsNullOrWhiteSpace(type.Value)) throw new ArgumentException("A processing module type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A processing module display name is required.", nameof(displayName));

        Type = type;
        DisplayName = displayName.Trim();
        Kind = kind;
        OptionsType = optionsType;
        EditorKey = string.IsNullOrWhiteSpace(editorKey) ? "options" : editorKey.Trim();
        AvailabilityMessage = availabilityMessage;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _metadataFactory = metadataFactory;
    }

    public IEnumerable<IProcessingPipeline> CreatePipelines(
        ProcessingCreationContext context,
        CameraProcessingSettings settings) =>
        _factory(context, settings) ?? Array.Empty<IProcessingPipeline>();

    public IReadOnlyDictionary<string, object?> GetDetectionMetadata(CameraProcessingSettings settings) =>
        _metadataFactory?.Invoke(settings) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// Central registry for processing capabilities. IDs are stable configuration
/// values; display names are only for the UI.
/// </summary>
public sealed class ProcessingRegistry
{
    private readonly Dictionary<ProcessingType, IProcessingModule> _modules = new();
    private readonly Dictionary<ProcessingType, ProcessingModuleDescriptor> _descriptors = new();

    public IReadOnlyCollection<IProcessingModule> Modules => _modules.Values.ToArray();
    public IReadOnlyCollection<ProcessingModuleDescriptor> Descriptors => _descriptors.Values.ToArray();

    public void Register(IProcessingModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules[module.Type] = module;
        _descriptors[module.Type] = new ProcessingModuleDescriptor(
            module.Type,
            module.DisplayName,
            module.Kind,
            module.OptionsType,
            module.EditorKey);
    }

    public void RegisterDescriptor(ProcessingModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Type.Value)) throw new ArgumentException("A processing module type is required.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName)) throw new ArgumentException("A processing module display name is required.", nameof(descriptor));
        _descriptors[descriptor.Type] = descriptor;
    }

    public bool Remove(ProcessingType type)
    {
        if (string.IsNullOrWhiteSpace(type.Value)) return false;
        _descriptors.Remove(type);
        return _modules.Remove(type);
    }

    public bool TryGet(ProcessingType type, out IProcessingModule? module) =>
        _modules.TryGetValue(type, out module);
}
