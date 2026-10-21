using System.Text.Json;

namespace RuntimeMetricsLab.Collector.Windows;

/// <summary>Atomic approval snapshots, including Windows readers racing heartbeat/revocation updates.</summary>
public static class WindowsApprovalFiles
{
    public static void CreateNew(string path, Action<Stream> write)
    {
        var created = false;
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            write(output);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }

    public static T Read<T>(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Approval snapshot exceeds its size bound.");
        return JsonSerializer.Deserialize<T>(input) ?? throw new InvalidDataException("Invalid approval snapshot.");
    }

    public static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value);
                output.Flush(flushToDisk: true);
            }
            ReplaceWithTransientSharingRetry(() =>
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }, Thread.Sleep);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void ReplaceWithTransientSharingRetry(Action replace, Action<TimeSpan> delay)
    {
        // Known Windows sharing/lock/remove-replaced failures leave both names
        // unchanged. Never retry arbitrary access denial or malformed data.
        for (var attempt = 0; ; attempt++)
        {
            try { replace(); return; }
            catch (IOException exception) when (attempt < 5 && (exception.HResult & 0xffff) is 32 or 33 or 1175)
            {
                delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)));
            }
        }
    }
}
