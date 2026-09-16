// Isolated infrastructure: no AppPaths migrations, environment changes, real library, or user data.
namespace SIGFUR.Wpf.Services;

public sealed class AppPaths(string root)
{
    public string LegislationDirectory => Path.Combine(root, "legislation");
    public string LegislationDocumentsDirectory => Path.Combine(LegislationDirectory, "documents");
    public string LegislationBuiltInDirectory => Path.Combine(root, "bundled");
    public string LegislationDatabaseFile => Path.Combine(LegislationDirectory, "index.sqlite3");
    public string CacheDirectory => Path.Combine(root, "cache");
}
public sealed class LogService
{
    public Task WriteAsync(string message, Exception? exception = null) => Task.CompletedTask;
}
public static class MilitaryFormatting
{
    public static string FormatFileSize(long size) => size.ToString();
}
