using System.IO;
using System.Text.Json;
using emMDee.Models;

namespace emMDee.Services;

/// <summary>
/// Manages session persistence: saves and loads the list of open files
/// and window state to a JSON file in %AppData%/emMDee.
/// </summary>
public class SessionManager
{
    private readonly string _sessionFilePath;

    public SessionManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "emMDee");
        Directory.CreateDirectory(appDir);
        _sessionFilePath = Path.Combine(appDir, "session.json");
    }

    public void Save(SessionData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sessionFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save session: {ex.Message}");
        }
    }

    public SessionData? Load()
    {
        try
        {
            if (!File.Exists(_sessionFilePath))
                return null;

            var json = File.ReadAllText(_sessionFilePath);
            return JsonSerializer.Deserialize<SessionData>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load session: {ex.Message}");
            return null;
        }
    }
}
