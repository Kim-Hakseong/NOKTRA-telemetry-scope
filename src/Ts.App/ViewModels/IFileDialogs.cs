namespace Ts.App.ViewModels;

/// <summary>A file type offered in a picker.</summary>
public readonly record struct FileFilter(string Name, string[] Patterns)
{
    public static FileFilter Definitions => new("Channel definition", new[] { "*.yaml", "*.yml" });

    public static FileFilter Recordings => new("Telemetry recording", new[] { "*.tsr" });

    public static FileFilter CommaSeparated => new("CSV", new[] { "*.csv" });
}

/// <summary>
/// File pickers, behind an interface so the view model stays testable and free of window handles.
/// </summary>
public interface IFileDialogs
{
    Task<string?> OpenAsync(string title, FileFilter filter);

    Task<string?> SaveAsync(string title, string suggestedName, FileFilter filter);
}
