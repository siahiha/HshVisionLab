using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

/// <summary>Reads metadata from the configured Plate model without exposing model-loading details to the UI.</summary>
public static class PlateModelInspector
{
    // Dynamic YOLO exports do not publish an enum of accepted image sizes. The
    // model publishes its stride instead, so these are the supported UI choices
    // shared by the desktop and service model catalogs.
    private static readonly int[] DynamicSquareInputSizes = [320, 416, 480, 512, 640];
    private static readonly ConcurrentDictionary<string, Lazy<int[]>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static int? TryGetSquareInputSize(string configuredName)
    {
        int first = GetSquareInputSizes(configuredName).FirstOrDefault();
        return first > 0 ? first : null;
    }

    public static IReadOnlyList<int> GetSquareInputSizes(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "best.onnx";

        return Cache.GetOrAdd(
            name,
            static key => new Lazy<int[]>(() => Inspect(key), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// Returns catalog-safe choices without synchronously constructing an
    /// ONNX session. The service model endpoint must stay responsive while
    /// cameras are running; exact inspection remains available through
    /// <see cref="GetSquareInputSizes"/> for local/configuration workflows.
    /// </summary>
    public static IReadOnlyList<int> GetCatalogSquareInputSizes(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "best.onnx";

        if (Cache.TryGetValue(name, out Lazy<int[]>? cached) && cached.IsValueCreated)
        {
            int[] inspected = cached.Value;
            if (inspected.Length > 0) return inspected;
        }

        // Fixed-size exports in the shipped catalog encode the tensor size in
        // their filename (for example best_416_static...). If no fixed size
        // is declared, expose the stride-aligned dynamic choices. This is
        // safe for YOLO dynamic-input exports and avoids opening a large model
        // session from an HTTP request.
        string stem = Path.GetFileNameWithoutExtension(name);
        foreach (string token in stem.Split('_', '-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out int size) || size < 32 || size > 2048 || size % 32 != 0) continue;
            return [size];
        }

        return DynamicSquareInputSizes;
    }

    private static int[] Inspect(string name)
    {
        string? temporaryModel = null;

        try
        {
            string modelPath;
            string? resolvedPath = PlateModelPaths.Find(name);
            if (resolvedPath is null)
            {
                return [];
            }

            if (resolvedPath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase))
            {
                temporaryModel = SecureModelLoader.Materialize(resolvedPath);
                modelPath = temporaryModel;
            }
            else
            {
                modelPath = resolvedPath;
            }

            using var session = new InferenceSession(modelPath);
            var input = session.InputMetadata.Values.FirstOrDefault();
            if (input is null || input.Dimensions.Length < 4)
                return [];

            var dimensions = input.Dimensions;
            int height = dimensions[^2];
            int width = dimensions[^1];
            if (height > 0 && width > 0 && height == width)
                return [height];

            // A dynamic YOLO tensor accepts stride-aligned square sizes. ONNX
            // does not contain a finite list, so expose the same safe choices
            // that the desktop form has historically offered, filtered by the
            // model's declared stride when available.
            int stride = ReadStride(session);
            return DynamicSquareInputSizes.Where(size => size % stride == 0).ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (temporaryModel is not null)
            {
                try { File.Delete(temporaryModel); } catch { }
            }
        }
    }

    private static int ReadStride(InferenceSession session)
    {
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("stride", out string? raw) &&
            int.TryParse(raw, out int stride) && stride > 0)
            return stride;

        return 32;
    }
}
