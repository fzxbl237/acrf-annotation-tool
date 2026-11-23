using System.Diagnostics;
using System.Text;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Opt-in diagnostics for cell editing and the autocomplete loader.
/// <para>
/// The calls compile out unless <c>ACRF_AUTOCOMPLETE_DIAG</c> is defined (add it to the project's
/// <c>DefineConstants</c> temporarily), so released builds pay neither the string formatting nor the
/// synchronous <c>File.AppendAllText</c> that used to run five times per cell edit and twice per keystroke.
/// </para>
/// </summary>
internal static class AutoCompleteDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "acrf-autocomplete.log");

    public static string LogPath => LogFilePath;

    [Conditional("ACRF_AUTOCOMPLETE_DIAG")]
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
