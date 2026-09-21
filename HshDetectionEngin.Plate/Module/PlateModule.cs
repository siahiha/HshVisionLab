using HshDetectionEngin.Licensing;

namespace HshDetectionEngin.Plate;

/// <summary>
/// Identity, deployment and composition metadata for the protected Iranian
/// plate capability. The host supplies the license and registers this module
/// with the runtime registry.
/// </summary>
public sealed class PlateModule
{
    public const string Id = "plate";
    public string DisplayName => "Iranian Plate Detection";
    public string ProtectedModelDirectory => Path.Combine("Models", "Plate");

    public static ProcessingModuleRegistration CreateRegistration(LicenseValidationResult license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return new ProcessingModuleRegistration(
            ProcessingType.Plate,
            "Plate detection",
            AnalysisKind.Plate,
            (_, item) =>
            {
                if (!license.Allows(LicensedFeature.Plate))
                    return Array.Empty<IProcessingPipeline>();

                PlateProcessingOptions options = item.GetOptions<PlateProcessingOptions>();
                return [new PlatePipeline(options, item.MaxFps, item.Threads)];
            },
            typeof(PlateProcessingOptions),
            "plate",
            availabilityMessage: license.Allows(LicensedFeature.Plate) ? null : license.Message);
    }
}
