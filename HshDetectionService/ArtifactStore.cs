using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace HshDetectionService;

public sealed class ArtifactStore
{
    private readonly ServicePaths _paths;

    public ArtifactStore(ServicePaths paths) => _paths = paths;

    public EventArtifactDescriptor SaveBitmap(string eventId, string type, Bitmap bitmap, long sourceFrameSequence, DateTime retentionUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        string artifactId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_paths.ArtifactDirectory, eventId);
        Directory.CreateDirectory(directory);
        string fileName = artifactId + ".jpg";
        string absolutePath = Path.Combine(directory, fileName);
        string temporaryPath = absolutePath + ".tmp";

        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            bitmap.Save(stream, ImageFormat.Jpeg);
            stream.Flush(true);
        }

        File.Move(temporaryPath, absolutePath, true);
        FileInfo info = new(absolutePath);
        string hash;
        using (FileStream stream = File.OpenRead(absolutePath))
            hash = Convert.ToHexString(SHA256.HashData(stream));

        return new EventArtifactDescriptor
        {
            ArtifactId = artifactId,
            Type = type,
            ContentType = "image/jpeg",
            Width = bitmap.Width,
            Height = bitmap.Height,
            SourceFrameSequence = sourceFrameSequence,
            Sha256 = hash,
            SizeBytes = info.Length,
            RetentionUntilUtc = retentionUntilUtc,
            RelativePath = Path.GetRelativePath(_paths.Root, absolutePath),
            DownloadUrl = $"/api/v1/events/{eventId}/artifacts/{artifactId}"
        };
    }

    public string Resolve(EventArtifactDescriptor artifact)
    {
        string root = Path.GetFullPath(_paths.Root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(_paths.Root, artifact.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact path is outside the service data root.");
        return path;
    }

    public void DeleteExpired(DateTime utcNow)
    {
        if (!Directory.Exists(_paths.ArtifactDirectory)) return;
        foreach (string directory in Directory.EnumerateDirectories(_paths.ArtifactDirectory))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < utcNow.AddDays(-1))
                    Directory.Delete(directory, true);
            }
            catch { }
        }
    }
}
