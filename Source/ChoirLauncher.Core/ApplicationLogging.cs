using System.Text;

namespace ChoirLauncher.Core;

public sealed class ApplicationLog
{
    private readonly string path;
    private readonly object gate = new();

    public ApplicationLog(ManagerStoragePaths storage)
    {
        storage.EnsureCreated();
        path = System.IO.Path.Combine(storage.Logs, $"ChoirLauncher-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    public string Path => path;

    public void Write(string level, string eventName, string message)
    {
        var safe = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.UtcNow:O} level={level} event={eventName} build={BuildInfo.BuildId} message=\"{safe.Replace("\"", "'", StringComparison.Ordinal)}\"{Environment.NewLine}";
        lock (gate)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
            stream.Flush(true);
        }
    }
}
