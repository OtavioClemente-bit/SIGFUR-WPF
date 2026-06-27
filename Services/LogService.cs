namespace SIGFUR.Wpf.Services;

public sealed class LogService
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LogService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.LogDirectory);
    }

    public async Task WriteAsync(string message, Exception? exception = null)
    {
        try
        {
            await _gate.WaitAsync();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            await File.AppendAllTextAsync(_paths.ApplicationLogFile, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch { }
        finally
        {
            if (_gate.CurrentCount == 0) _gate.Release();
        }
    }
}
