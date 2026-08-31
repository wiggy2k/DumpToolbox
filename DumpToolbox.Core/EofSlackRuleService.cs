using System.Reflection;
using System.Text;

namespace DumpToolbox.Core;

public sealed record EofSlackRule(
    string Section,
    string Name,
    string ApplicationContains,
    string DataPreparerContains,
    string SystemIdMatch,
    long DeltaSectors,
    string Confidence);

public sealed record EofSlackRuleSet(
    string FilePath,
    bool Enabled,
    IReadOnlyList<EofSlackRule> Rules,
    IReadOnlyList<string> Warnings);
public sealed record EofSlackAmbiguityRequest(
    string SystemId,
    string ApplicationId,
    string DataPreparerId,
    IReadOnlyList<EofSlackRule> MatchingRules,
    bool CanTryAllAndVerify);

public sealed record EofSlackAmbiguityDecision(
    string? RuleSection = null,
    bool TryAllAndVerify = false);


/// <summary>
/// Runtime-configurable mastering EOF-slack rules.  The application contains
/// no mastering-signature selection logic: rules are loaded from the external
/// EOFSlackRules.ini beside the executable each time resurrection runs.
/// </summary>
public static class EofSlackRuleService
{
    public const string ExternalFileName = "EOFSlackRules.ini";
    private const string EmbeddedDefaultName = "DumpToolbox.Core.EOFSlackRules.default.ini";

    public static string ExternalFilePath => Path.Combine(AppContext.BaseDirectory, ExternalFileName);

    public static bool EnsureDefaultFileBesideExecutable(out string? error)
    {
        error = null;
        string path = ExternalFilePath;
        if (File.Exists(path))
            return true;

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            Assembly assembly = typeof(EofSlackRuleService).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(EmbeddedDefaultName);
            if (stream is null)
                throw new InvalidOperationException($"Embedded EOF slack rule template '{EmbeddedDefaultName}' was not found.");

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"Could not create '{path}': {ex.Message}";
            return false;
        }
    }

    public static EofSlackRuleSet Load()
    {
        var warnings = new List<string>();
        if (!EnsureDefaultFileBesideExecutable(out string? createError))
        {
            if (!string.IsNullOrWhiteSpace(createError))
                warnings.Add(createError);
            return new EofSlackRuleSet(ExternalFilePath, false, Array.Empty<EofSlackRule>(), warnings);
        }

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string currentSection = "General";
        try
        {
            foreach (string raw in File.ReadLines(ExternalFilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                {
                    currentSection = line[1..^1].Trim();
                    if (!sections.ContainsKey(currentSection))
                        sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (!sections.TryGetValue(currentSection, out Dictionary<string, string>? values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[currentSection] = values;
                }
                values[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read '{ExternalFilePath}': {ex.Message}");
            return new EofSlackRuleSet(ExternalFilePath, false, Array.Empty<EofSlackRule>(), warnings);
        }

        bool globallyEnabled = GetBool(sections, "EOF-SlackData-Fix", "Enabled", true);
        var rules = new List<EofSlackRule>();
        foreach ((string sectionName, Dictionary<string, string> values) in sections
                     .Where(pair => pair.Key.StartsWith("Rule", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pair => ParseRuleNumber(pair.Key))
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!GetBool(values, "Enabled", true))
                continue;

            string app = Get(values, "ApplicationContains");
            string dataPreparer = Get(values, "DataPreparerContains");
            string system = Get(values, "SystemIdMatch", "*");
            string name = Get(values, "Name", sectionName);
            string confidence = Get(values, "Confidence");

            if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(dataPreparer))
            {
                warnings.Add($"[{sectionName}] ignored: both ApplicationContains and DataPreparerContains are empty.");
                continue;
            }

            if (!long.TryParse(Get(values, "DeltaSectors"), out long delta) || delta <= 0)
            {
                warnings.Add($"[{sectionName}] ignored: DeltaSectors must be > 0.");
                continue;
            }

            rules.Add(new EofSlackRule(sectionName, name, app, dataPreparer, system, delta, confidence));
        }

        return new EofSlackRuleSet(ExternalFilePath, globallyEnabled, rules, warnings);
    }

    public static IReadOnlyList<EofSlackRule> FindMatches(
        EofSlackRuleSet ruleSet,
        string systemId,
        string applicationId,
        string dataPreparerId)
    {
        if (!ruleSet.Enabled)
            return Array.Empty<EofSlackRule>();

        return ruleSet.Rules
            .Where(rule => Matches(rule, systemId, applicationId, dataPreparerId))
            .ToArray();
    }

    private static bool Matches(EofSlackRule rule, string systemId, string applicationId, string dataPreparerId)
    {
        if (!string.IsNullOrWhiteSpace(rule.ApplicationContains) &&
            !applicationId.Contains(rule.ApplicationContains, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(rule.DataPreparerContains) &&
            !dataPreparerId.Contains(rule.DataPreparerContains, StringComparison.OrdinalIgnoreCase))
            return false;

        string actualSystem = systemId.Trim();
        string expectedSystem = rule.SystemIdMatch.Trim();
        if (expectedSystem == "*")
            return true;
        if (expectedSystem.Equals("<blank>", StringComparison.OrdinalIgnoreCase))
            return actualSystem.Length == 0;
        return actualSystem.Equals(expectedSystem, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseRuleNumber(string section)
    {
        string suffix = section.Length > 4 ? section[4..] : string.Empty;
        return int.TryParse(suffix, out int value) ? value : int.MaxValue;
    }

    private static string Get(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        string section,
        string key,
        string defaultValue = "")
        => sections.TryGetValue(section, out Dictionary<string, string>? values)
            ? Get(values, key, defaultValue)
            : defaultValue;

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string defaultValue = "")
        => values.TryGetValue(key, out string? value) ? value : defaultValue;

    private static bool GetBool(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        string section,
        string key,
        bool defaultValue)
        => sections.TryGetValue(section, out Dictionary<string, string>? values)
            ? GetBool(values, key, defaultValue)
            : defaultValue;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        string text = Get(values, key);
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return defaultValue;
    }
}
