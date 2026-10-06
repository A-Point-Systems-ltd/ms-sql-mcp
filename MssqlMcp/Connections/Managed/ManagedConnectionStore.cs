using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mssql.McpServer.Connections.Managed;

/// <summary>The managed connections file exists but cannot be used. The file is never overwritten in that state.</summary>
public sealed class ManagedConnectionFileException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads and writes the managed connections file. Several server processes can share the file (one per Claude
/// window or tab), so every change is a read-modify-write under a cross-process mutex, written atomically
/// (temp file + replace) with the previous version kept as <c>.bak</c>.
/// </summary>
public sealed class ManagedConnectionStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _mutexName;

    public ManagedConnectionStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant()));
        _mutexName = @"Local\APoint-ms-sql-managed-" + Convert.ToHexString(hash, 0, 8);
    }

    public string Path { get; }

    /// <summary>Last write time of the file, or null when it does not exist. Used to notice other processes' edits.</summary>
    public DateTime? LastWriteUtc => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;

    /// <summary>The current entries; empty when the file does not exist. Throws <see cref="ManagedConnectionFileException"/> when unusable.</summary>
    public IReadOnlyList<ManagedConnection> Load()
    {
        string content;
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            content = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ManagedConnectionFileException($"The managed connections file cannot be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        ManagedConnectionsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ManagedConnectionsFile>(content, Json);
        }
        catch (JsonException ex)
        {
            throw new ManagedConnectionFileException($"The managed connections file is not valid JSON ({ex.Message}). Fix or delete it; it is left untouched.");
        }

        if (file is null || file.Connections.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)))
        {
            throw new ManagedConnectionFileException("The managed connections file has an entry without a name. Fix or delete it; it is left untouched.");
        }

        if (file.Version > ManagedConnectionsFile.CurrentVersion)
        {
            throw new ManagedConnectionFileException($"The managed connections file was written by a newer version (format {file.Version}). Update APoint-ms-sql.");
        }

        return file.Connections;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the current entries and saves them, under the cross-process lock.
    /// <paramref name="change"/> returns Save=false to skip the write (e.g. validation failed).
    /// </summary>
    public T Mutate<T>(Func<List<ManagedConnection>, (bool Save, T Result)> change)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-change; the atomic write means the file is either old or new, never torn.
            acquired = true;
        }

        if (!acquired)
        {
            throw new ManagedConnectionFileException("Another APoint-ms-sql process is changing the connections; try again.");
        }

        try
        {
            var entries = Load().ToList();
            var (save, result) = change(entries);
            if (save)
            {
                Write(entries);
            }

            return result;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void Write(List<ManagedConnection> entries)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        _ = Directory.CreateDirectory(dir);
        var sorted = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(new ManagedConnectionsFile { Connections = sorted }, Json);
        var temp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(Path)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(Path))
            {
                File.Replace(temp, Path, Path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new ManagedConnectionFileException($"The managed connections file cannot be written: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stale temp file is harmless and overwritten next time.
        }
    }
}
