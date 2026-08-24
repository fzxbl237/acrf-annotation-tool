using System.Text.Json;

namespace AcrfAnnotationTool.Services;

public sealed record ApplicationSettings(
    double DomainAnnotationHorizontalOffset,
    double DomainAnnotationVerticalOffset);

public static class ApplicationSettingsStore
{
    private const double DefaultDomainAnnotationOffset = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

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

            var settings = JsonSerializer.Deserialize<ApplicationSettings>(
                File.ReadAllText(SettingsFilePath),
                JsonOptions);
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
            var json = JsonSerializer.Serialize(settings, JsonOptions);
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
        DefaultDomainAnnotationOffset);

    private static bool IsValidOffset(double value) =>
        double.IsFinite(value) && value >= 0;
}
