namespace Argus.App.Configuration;

/// <summary>Bound to the "Application" section of appsettings.json.</summary>
public sealed class ApplicationOptions
{
    public const string SectionName = "Application";

    public string Name { get; init; } = "Argus AI Copilot";
    public string Version { get; init; } = "0.1.0";
    public string Environment { get; init; } = "Production";
}
