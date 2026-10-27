using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcrfAnnotationTool.Services;

public sealed record ApplicationSettings(
    double DomainAnnotationHorizontalOffset,
    double DomainAnnotationVerticalOffset,
    bool? IsPdfAnnotationEditingEnabled = null,
    bool Language = false);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApplicationSettings))]
internal partial class ApplicationSettingsJsonContext : JsonSerializerContext
{
}

public static class ApplicationSettingsStore
{
    private const double DefaultDomainAnnotationOffset = 8;

    private static string SettingsDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcrfAnnotationTool");

    private static string SettingsFilePath => Path.Combine(
        SettingsDirectoryPath,
        "settings.json");

    public static ApplicationSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return CreateDefault();

            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(SettingsFilePath),
                ApplicationSettingsJsonContext.Default.ApplicationSettings);
            if (settings is null ||
                !IsValidOffset(settings.DomainAnnotationHorizontalOffset) ||
                !IsValidOffset(settings.DomainAnnotationVerticalOffset))
            {
                return CreateDefault();
            }

            return settings;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static bool TrySave(ApplicationSettings settings, out string? error)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectoryPath);
            var json = JsonSerializer.Serialize(settings, ApplicationSettingsJsonContext.Default.ApplicationSettings);
            File.WriteAllText(SettingsFilePath, json);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ApplicationSettings CreateDefault() => new(
        DefaultDomainAnnotationOffset,
        DefaultDomainAnnotationOffset,
        false);

    private static bool IsValidOffset(double value) =>
        double.IsFinite(value) && value >= 0;
}
