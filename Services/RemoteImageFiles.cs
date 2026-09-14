namespace Gelato.Services;

/// <summary>Publishes complete image URL sidecars without truncating cached images.</summary>
public static class RemoteImageFiles
{
    private static readonly object[] Gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    private static object Gate(string path) =>
        Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path)) % (uint)Gates.Length];

    public static void EnsurePlaceholder(string path)
    {
        lock (Gate(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // OpenOrCreate never truncates an image another request already downloaded.
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    public static void WriteUrl(string imagePath, string url)
    {
        var path = imagePath + ".url";
        lock (Gate(path))
        {
            if (File.Exists(path) && File.ReadAllText(path) == url) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, url);
                // Same-directory rename makes readers see either complete version.
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
