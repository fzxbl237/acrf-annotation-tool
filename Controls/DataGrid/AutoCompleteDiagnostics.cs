using System.Diagnostics;
using System.Text;

namespace PatChes.Controls.DataGrid;

internal static class AutoCompleteDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "acrf-autocomplete.log");

    public static string LogPath => LogFilePath;

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:O} [P{Environment.ProcessId}] [T{Environment.CurrentManagedThreadId}] [ACRF-AUTOCOMPLETE] {message}";
        Trace.WriteLine(line);
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
