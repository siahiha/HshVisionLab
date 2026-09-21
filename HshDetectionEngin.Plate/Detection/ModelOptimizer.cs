using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

internal static class ModelOptimizer
{
    private static readonly object OptimizationGate = new();

    public static string EnsureOptimized(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Model not found: {sourcePath}");

        lock (OptimizationGate)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(sourcePath);

            // An ORT-generated model is already optimized. Never optimize it
            // again, otherwise every startup can create another suffix chain.
            if (name.EndsWith("_ort_optimized", StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            string optimizedPath = Path.Combine(dir, name + "_ort_optimized.onnx");

            if (File.Exists(optimizedPath) && new FileInfo(optimizedPath).Length > 1024 * 1024)
                return optimizedPath;

            string temp = optimizedPath + ".tmp";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                using var so = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Math.Min(2, Environment.ProcessorCount)),
                    EnableMemoryPattern = true,
                    OptimizedModelFilePath = temp
                };

                using (var session = new InferenceSession(sourcePath, so)) { }
                if (!File.Exists(temp) || new FileInfo(temp).Length < 1024 * 1024)
                    throw new InvalidOperationException("ONNX Runtime did not create a valid optimized model.");

                File.Move(temp, optimizedPath, true);
                return optimizedPath;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return sourcePath;
            }
        }
    }
}
