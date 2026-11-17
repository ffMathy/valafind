using System.Reflection;
using Valafind.Core.Configuration;

namespace Valafind.Core.Embedding;

/// <summary>
/// Unpacks the EmbeddingGemma files that release builds embed in the executable (see <c>src/Valafind.Model</c>) into the
/// per-user models folder, so the single-file app works without a separate download. Purely local: it only copies bytes
/// out of the running executable.
/// </summary>
public static class BundledModel
{
    /// <summary>Logical-name prefix of the embedded model files; the rest of the name is the path inside the model
    /// folder (e.g. <c>ValafindModel/onnx/model_quantized.onnx</c>).</summary>
    public const string ResourcePrefix = "ValafindModel/";

    private const string PartialSuffix = ".part";

    /// <summary>The model files embedded in <paramref name="assembly"/>: path relative to the model folder (forward
    /// slashes) and manifest resource name. Empty when the build does not bundle the model.</summary>
    public static IReadOnlyList<(string RelativePath, string ResourceName)> Files(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var files = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // MSBuild's %(RecursiveDir) uses the build machine's separator; normalise it.
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            var relative = normalised[ResourcePrefix.Length..];
            if (relative.Length == 0 || relative.Split('/').Any(p => p is "" or "." or ".."))
                continue;
            files.Add((relative, name));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return files;
    }

    /// <summary>True when <paramref name="assembly"/> carries an embedded model.</summary>
    public static bool IsBundled(Assembly assembly) => Files(assembly).Count > 0;

    /// <summary>
    /// Makes sure <paramref name="targetDirectory"/> holds the bundled model: every embedded file that is missing or has
    /// a different size is (re)written, via a temporary file and an atomic rename so a crash never leaves a truncated
    /// file under the final name. Files already in place are left untouched, so after the first run this costs a few
    /// file-size probes. Returns the number of files written; 0 when nothing was needed or nothing is bundled.
    /// </summary>
    /// <exception cref="IOException">The folder or a file could not be written (e.g. the disk is full).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static int EnsureInstalled(Assembly assembly, string targetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var files = Files(assembly);
        if (files.Count == 0)
            return 0;

        var written = 0;
        foreach (var (relative, resourceName) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new IOException($"Embedded model file {relative} is unreadable.");
            var destination = Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var existing = new FileInfo(destination);
            if (existing.Exists && existing.Length == source.Length)
                continue;

            var folder = Path.GetDirectoryName(destination)!;
            AppPaths.EnsurePrivateDirectory(folder);
            var partial = destination + PartialSuffix;
            try
            {
                using (var target = new FileStream(partial,
                           AppPaths.PrivateFileOptions(FileMode.Create, FileAccess.Write, FileShare.None)))
                {
                    target.SetLength(source.Length); // fail fast (disk full) and avoid fragmentation
                    target.Position = 0;
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                    }
                    target.Flush(flushToDisk: true);
                }
                File.Move(partial, destination, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
            written++;
        }
        return written;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover .part file: harmless, overwritten next time.
        }
    }
}
