using System.Text.Json;

namespace FileMCP.Core;

public sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] WorkspaceKeys = ["C", "D", "E", "F"];

    public SettingsStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMCP");
        _path = Path.Combine(root, "settings.json");
    }

    public FileMcpSettings Load()
    {
        if (!File.Exists(_path))
        {
            var fresh = new FileMcpSettings();
            Normalize(fresh, hadWorkspaceArray: false);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<FileMcpSettings>(json, JsonOptions) ?? new FileMcpSettings();

            var hadWorkspaceArray = false;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty(nameof(FileMcpSettings.Workspaces), out var workspaces) &&
                    workspaces.ValueKind == JsonValueKind.Array &&
                    workspaces.GetArrayLength() > 0)
                {
                    hadWorkspaceArray = true;
                }
            }

            Normalize(settings, hadWorkspaceArray);
            return settings;
        }
        catch (JsonException ex)
        {
            throw new FileMcpException("Could not read FileMCP settings: " + ex.Message);
        }
    }

    public void Save(FileMcpSettings settings)
    {
        Normalize(settings, hadWorkspaceArray: settings.Workspaces.Count > 0);
        SynchronizeLegacyFields(settings);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void Normalize(FileMcpSettings settings, bool hadWorkspaceArray)
    {
        if (settings.Port is < 1 or > 65535) settings.Port = 8008;
        if (string.IsNullOrWhiteSpace(settings.Profile)) settings.Profile = "filemcp";
        if (string.IsNullOrWhiteSpace(settings.AllowedDirectory))
            settings.AllowedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FileMCP");
        if (string.IsNullOrWhiteSpace(settings.HealthAddress)) settings.HealthAddress = "127.0.0.1:0";
        if (string.IsNullOrWhiteSpace(settings.OtlpEndpoint)) settings.OtlpEndpoint = OtlpTelemetrySettings.DefaultEndpoint;
        else settings.OtlpEndpoint = settings.OtlpEndpoint.Trim();

        var existing = settings.Workspaces
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key.Trim().ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var normalized = WorkspaceKeys.Select(CreateDefaultWorkspace).ToList();

        foreach (var target in normalized)
        {
            if (!existing.TryGetValue(target.Key, out var source)) continue;
            target.Enabled = source.Enabled;
            target.TunnelId = source.TunnelId?.Trim() ?? "";
            target.Profile = string.IsNullOrWhiteSpace(source.Profile) ? target.Profile : source.Profile.Trim();
            target.Port = source.Port is >= 1 and <= 65535 ? source.Port : target.Port;
            target.AllowedDirectory = string.IsNullOrWhiteSpace(source.AllowedDirectory) ? target.AllowedDirectory : source.AllowedDirectory.Trim();
            target.HealthAddress = string.IsNullOrWhiteSpace(source.HealthAddress) ? "127.0.0.1:0" : source.HealthAddress.Trim();
        }

        if (!hadWorkspaceArray)
        {
            var legacyKey = WorkspaceKeyForPath(settings.AllowedDirectory);
            var legacy = normalized.FirstOrDefault(item => item.Key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase))
                ?? normalized.First(item => item.Key == "D");

            legacy.Enabled = true;
            legacy.TunnelId = settings.TunnelId?.Trim() ?? "";
            legacy.Profile = string.IsNullOrWhiteSpace(settings.Profile) ? legacy.Profile : settings.Profile.Trim();
            legacy.Port = settings.Port is >= 1 and <= 65535 ? settings.Port : legacy.Port;
            legacy.AllowedDirectory = settings.AllowedDirectory.Trim();
            legacy.HealthAddress = settings.HealthAddress.Trim();
        }

        EnsureUniqueRuntimeCoordinates(normalized);
        settings.Workspaces = normalized;
        SynchronizeLegacyFields(settings);
    }

    private static FileMcpWorkspaceSettings CreateDefaultWorkspace(string key)
    {
        var index = Array.IndexOf(WorkspaceKeys, key);
        var path = key == "C"
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : key + @":\";

        return new FileMcpWorkspaceSettings
        {
            Key = key,
            Enabled = false,
            TunnelId = "",
            Profile = "filemcp-" + key.ToLowerInvariant(),
            Port = 8008 + index,
            AllowedDirectory = path,
            HealthAddress = "127.0.0.1:0",
        };
    }

    private static string WorkspaceKeyForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(root) && root.Length >= 1 && char.IsAsciiLetter(root[0]))
                return char.ToUpperInvariant(root[0]).ToString();
        }
        catch { }

        return "D";
    }

    private static void EnsureUniqueRuntimeCoordinates(IReadOnlyList<FileMcpWorkspaceSettings> workspaces)
    {
        var usedPorts = new HashSet<int>();
        var usedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextPort = 8008;

        foreach (var workspace in workspaces.OrderByDescending(item => item.Enabled))
        {
            if (workspace.Port is < 1 or > 65535 || !usedPorts.Add(workspace.Port))
            {
                while (usedPorts.Contains(nextPort)) nextPort++;
                workspace.Port = nextPort;
                usedPorts.Add(workspace.Port);
            }

            var baseProfile = string.IsNullOrWhiteSpace(workspace.Profile)
                ? "filemcp-" + workspace.Key.ToLowerInvariant()
                : workspace.Profile.Trim();

            var candidate = baseProfile;
            var suffix = 2;
            while (!usedProfiles.Add(candidate))
                candidate = baseProfile + "-" + suffix++;

            workspace.Profile = candidate;
        }
    }

    private static void SynchronizeLegacyFields(FileMcpSettings settings)
    {
        var primary = settings.Workspaces.FirstOrDefault(item => item.Enabled)
            ?? settings.Workspaces.FirstOrDefault(item => item.Key == "D")
            ?? settings.Workspaces.FirstOrDefault();

        if (primary is null) return;

        settings.TunnelId = primary.TunnelId;
        settings.Profile = primary.Profile;
        settings.Port = primary.Port;
        settings.AllowedDirectory = primary.AllowedDirectory;
        settings.HealthAddress = primary.HealthAddress;
    }
}
