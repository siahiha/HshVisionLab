using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

/// <summary>Reads metadata from the configured Plate model without exposing model-loading details to the UI.</summary>
public static class PlateModelInspector
{
    public static int? TryGetSquareInputSize(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "best.onnx";

        string? temporaryModel = null;

        try
        {
            string modelPath;
            string? resolvedPath = PlateModelPaths.Find(name);
            if (resolvedPath is null)
            {
                return null;
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
                return null;

            var dimensions = input.Dimensions;
            return dimensions[2] > 0 && dimensions[3] > 0 && dimensions[2] == dimensions[3]
                ? (int)dimensions[2]
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (temporaryModel is not null)
            {
                try { File.Delete(temporaryModel); } catch { }
            }
        }
    }
}
