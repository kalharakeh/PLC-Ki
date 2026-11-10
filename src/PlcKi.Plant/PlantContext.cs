using Microsoft.Extensions.Hosting.WindowsServices;

namespace PlcKi.Plant;

/// <summary>
/// Where the plant PC keeps its own files, and facts about how this copy of PLC-Ki was started.
/// Data folder (settings, users, encryption keys, uploaded models):
///   running as a Windows service: C:\ProgramData\PLC-Ki
///   started by hand (dotnet run): the user's own app data folder (%LOCALAPPDATA%\PLC-Ki)
///   or wherever Plant:DataDir / the PLCKI_DATADIR environment variable points.
/// </summary>
public sealed class PlantContext
{
    public const string ServiceName = "PLC-Ki";

    public PlantContext(string dataDir, bool isService)
    {
        DataDir = Path.GetFullPath(dataDir);
        IsService = isService;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
    }

    public string DataDir { get; }
    public bool IsService { get; }
    public string AppDir { get; } = AppContext.BaseDirectory;
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string UsersFile => Path.Combine(DataDir, "users.json");
    /// <summary>Each login's own choices (theme, alarm sound, start page, ...): PreferenceStore.</summary>
    public string PrefsFile => Path.Combine(DataDir, "prefs.json");
    public string KeysDir => Path.Combine(DataDir, "keys");
    /// <summary>Machine models uploaded on the settings page.</summary>
    public string ModelsDir => Path.Combine(DataDir, "models");

    /// <summary>ANTHROPIC_API_KEY was already set in the environment when PLC-Ki started.</summary>
    public bool ApiKeyInEnvironment { get; init; }
    /// <summary>An API key was saved on the settings page (it then wins over the environment variable).</summary>
    public bool ApiKeyInSettings { get; set; }
    public bool PlcPasswordInEnvironment { get; init; }
    public bool PlcPasswordInSettings { get; set; }
    /// <summary>A token for the TIA helper was saved on the connections page.</summary>
    public bool TiaTokenInSettings { get; set; }

    /// <summary>Started by hand: restarts the web app inside this process (set by the server), so new settings apply.</summary>
    public Action? RestartInProcess { get; set; }

    /// <summary>Program exports, packages from the block generator, uploaded archives.</summary>
    public string WorkDir => Path.Combine(DataDir, "work");

    /// <summary>Settings were saved that only take effect after a restart.</summary>
    public bool RestartNeeded { get; set; }

    /// <summary>The settings this PLC-Ki started with: what is running now, even when settings.json changed since.</summary>
    public PlantSettings? Running { get; set; }

    public static string DefaultDataDir(bool isService) => isService && OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PLC-Ki")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PLC-Ki");

    public static bool RunningAsService() => OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    /// <summary>A model path as stored in the settings: relative to the app folder, or absolute.</summary>
    public string ResolveAppPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppDir, path);
}

/// <summary>A problem the user can fix; the message is shown on the page.</summary>
public sealed class PlantException : Exception
{
    public PlantException(string message, int status = 400) : base(message) => Status = status;
    public int Status { get; }
}
