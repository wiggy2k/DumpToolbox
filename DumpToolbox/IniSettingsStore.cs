using System.Text;

namespace DumpToolbox;

/// <summary>
/// Tiny dependency-free INI store for user interface preferences.  The file is
/// created at runtime and is deliberately not a build/release asset.
/// </summary>
internal sealed class IniSettingsStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    private IniSettingsStore(string filePath, bool createdOnOpen)
    {
        FilePath = filePath;
        CreatedOnOpen = createdOnOpen;
        Load();
    }

    public string FilePath { get; }
    public bool CreatedOnOpen { get; }

    public static IniSettingsStore Open()
    {
        string portablePath = Path.Combine(AppContext.BaseDirectory, "DumpToolbox.ini");
        if (TryPreparePath(portablePath, out bool portableCreated))
            return new IniSettingsStore(portablePath, portableCreated);

        string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
            localRoot = Path.GetTempPath();

        string fallbackPath = Path.Combine(localRoot, "DumpToolbox", "DumpToolbox.ini");
        if (!TryPreparePath(fallbackPath, out bool fallbackCreated))
            throw new IOException($"Unable to create DumpToolbox.ini beside the application or under '{localRoot}'.");

        return new IniSettingsStore(fallbackPath, fallbackCreated);
    }

    public string Get(string section, string key, string defaultValue = "")
        => _sections.TryGetValue(section, out Dictionary<string, string>? values) &&
           values.TryGetValue(key, out string? value)
            ? value
            : defaultValue;

    public int GetInt(string section, string key, int defaultValue)
        => int.TryParse(Get(section, key), out int value) ? value : defaultValue;

    public double GetDouble(string section, string key, double defaultValue)
        => double.TryParse(Get(section, key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : defaultValue;

    public bool GetBool(string section, string key, bool defaultValue)
    {
        string value = Get(section, key);
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? true
            : value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ||
              value.Equals("no", StringComparison.OrdinalIgnoreCase)
                ? false
                : defaultValue;
    }

    public void Set(string section, string key, string? value)
    {
        if (!_sections.TryGetValue(section, out Dictionary<string, string>? values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = values;
        }

        values[key] = Sanitize(value ?? string.Empty);
    }

    public void Set(string section, string key, int value)
        => Set(section, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Set(string section, string key, double value)
        => Set(section, key, value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    public void Set(string section, string key, bool value)
        => Set(section, key, value ? "true" : "false");

    public void RemoveSection(string section) => _sections.Remove(section);

    public void RemoveKey(string section, string key)
    {
        if (!_sections.TryGetValue(section, out Dictionary<string, string>? values))
            return;

        values.Remove(key);
        if (values.Count == 0)
            _sections.Remove(section);
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = FilePath + ".tmp";
        using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine("; DumpToolbox user settings");
            writer.WriteLine("; Generated automatically. It is safe to delete this file to reset all settings.");
            writer.WriteLine();

            foreach ((string section, Dictionary<string, string> values) in _sections.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine($"[{section}]");
                foreach ((string key, string value) in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    writer.WriteLine($"{key}={Sanitize(value)}");
                writer.WriteLine();
            }
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(FilePath))
            return;

        string section = "General";
        foreach (string rawLine in File.ReadLines(FilePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                if (section.Length == 0)
                    section = "General";
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Length > 0)
                Set(section, key, value);
        }
    }

    private static bool TryPreparePath(string path, out bool created)
    {
        created = false;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            bool existed = File.Exists(path);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            created = !existed;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Sanitize(string value)
        => value.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
}
