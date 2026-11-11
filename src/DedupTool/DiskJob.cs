using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DedupTool;

internal sealed class DiskJob : IDisposable
{
    private const int Version = 1;
    private readonly FileStream jobLock;
    private readonly string manifestPath;
    private string outputTemporary = "";

    internal string DirectoryPath { get; }
    internal string SnapshotPath => Path.Combine(DirectoryPath, "input.json");
    internal string DatabasePath => Path.Combine(DirectoryPath, "state.sqlite");
    internal string Destination { get; }
    internal int DocumentLimit { get; }
    internal int CacheKiB { get; }
    internal string OutputTemporary => outputTemporary;

    internal DiskJob(string input, string output)
    {
        var source = Path.GetFullPath(input);
        Destination = Path.GetFullPath(output);
        if (string.Equals(source, Destination, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Input and output must be different files.");
        DocumentLimit = ReadLimit("DEDUP_MAX_DOCUMENT_BYTES", 64 * 1024 * 1024, 1024, 256 * 1024 * 1024);
        CacheKiB = ReadLimit("DEDUP_SQLITE_CACHE_KIB", 256 * 1024, 1024, 1024 * 1024);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Destination)))[..24];
        var scratch = Environment.GetEnvironmentVariable("DEDUP_SCRATCH_ROOT");
        var root = string.IsNullOrWhiteSpace(scratch) ? Path.GetDirectoryName(Destination)! : Path.GetFullPath(scratch);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Scratch root does not exist: '{root}'.");
        DirectoryPath = Path.Combine(root, ".deduptool-" + key);
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        jobLock = new FileStream(Path.Combine(Path.GetDirectoryName(Destination)!, $".deduptool-{key}.lock"), lockOptions);
        manifestPath = Path.Combine(DirectoryPath, "manifest.json");
        try
        {
            var existed = Directory.Exists(DirectoryPath);
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(DirectoryPath);
            else
                Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The job directory must not be a symbolic link.");
            var owner = Path.Combine(DirectoryPath, "owner");
            if (existed && (!File.Exists(owner) || File.ReadAllText(owner) != "DedupTool job v1"))
                throw new IOException("Existing scratch directory is not owned by DedupTool.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!existed)
            {
                using var ownerFile = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(ownerFile);
                writer.Write("DedupTool job v1");
            }
            PrepareSnapshot(source);
        }
        catch
        {
            jobLock.Dispose();
            throw;
        }
    }

    private void PrepareSnapshot(string source)
    {
        var inputDigest = HashFile(source);
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<JobManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("Job manifest is empty.");
            if (manifest.Version != Version || manifest.Source != source || manifest.Destination != Destination ||
                manifest.InputDigest != inputDigest || manifest.DocumentLimit != DocumentLimit ||
                manifest.CacheKiB != CacheKiB || !Guid.TryParseExact(manifest.OutputToken, "N", out _) ||
                !File.Exists(SnapshotPath) || HashFile(SnapshotPath) != inputDigest)
                throw new InvalidDataException("Existing job does not match the input, destination, configuration, or pipeline version. Inspect and remove its scratch directory before starting a new job.");
            SetOutputTemporary(manifest.OutputToken);
            return;
        }

        foreach (var name in new[] { "input.json", "input.copy", "state.sqlite", "state.sqlite-journal", "state.sqlite-wal", "state.sqlite-shm", "manifest.tmp" })
            File.Delete(Path.Combine(DirectoryPath, name));
        var copy = Path.Combine(DirectoryPath, "input.copy");
        using (var sourceStream = File.OpenRead(source))
        using (var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            sourceStream.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }
        if (HashFile(copy) != inputDigest || HashFile(source) != inputDigest)
            throw new IOException("Input changed while creating the job snapshot.");
        File.Move(copy, SnapshotPath);
        var token = Guid.NewGuid().ToString("N");
        SetOutputTemporary(token);
        var manifestData = JsonSerializer.Serialize(new JobManifest(Version, source, Destination, inputDigest, DocumentLimit, CacheKiB, token));
        var temporary = Path.Combine(DirectoryPath, "manifest.tmp");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(manifestData);
            writer.Flush();
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, manifestPath);
    }

    internal void Cleanup()
    {
        File.Delete(outputTemporary);
        foreach (var name in new[] { "state.sqlite", "state.sqlite-journal", "state.sqlite-wal", "state.sqlite-shm", "input.json", "input.copy", "manifest.json", "manifest.tmp" })
            File.Delete(Path.Combine(DirectoryPath, name));
    }

    public void Dispose() => jobLock.Dispose();

    private void SetOutputTemporary(string token) =>
        outputTemporary = Path.Combine(Path.GetDirectoryName(Destination)!, $".deduptool-{token}.csv.tmp");

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int ReadLimit(string name, int fallback, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return fallback;
        if (!int.TryParse(value, out var result) || result < minimum || result > maximum)
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
        return result;
    }

    private sealed record JobManifest(int Version, string Source, string Destination, string InputDigest,
        int DocumentLimit, int CacheKiB, string OutputToken);
}
