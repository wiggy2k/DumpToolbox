using System.Reflection;
using System.Text;

namespace DumpToolbox.Core;

public sealed record JolietNamingProfile(
    string Section,
    string Name,
    string ApplicationContains,
    string DataPreparerContains,
    string SystemIdMatch,
    IReadOnlySet<string> Methods);

public sealed record JolietNamingRuleSet(
    string FilePath,
    bool Enabled,
    IReadOnlyList<JolietNamingProfile> Profiles,
    IReadOnlyList<string> Warnings);

public sealed record IsoMasteringIdentity(string SystemId, string ApplicationId, string DataPreparerId);

/// <summary>
/// Runtime-configurable mastering-specific Joliet -> primary ISO9660 naming profiles.
/// When no profile matches the target disc, DumpToolbox deliberately falls back to the
/// historic generic projection rules.
/// </summary>
public static class JolietNamingRuleService
{
    public const string ExternalFileName = "JolietNamingRules.ini";
    private const string EmbeddedDefaultName = "DumpToolbox.Core.JolietNamingRules.default.ini";

    public static string ExternalFilePath => Path.Combine(AppContext.BaseDirectory, ExternalFileName);

    public static bool EnsureDefaultFileBesideExecutable(out string? error)
    {
        error = null;
        string path = ExternalFilePath;
        if (File.Exists(path))
            return true;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            Assembly asm = typeof(JolietNamingRuleService).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(EmbeddedDefaultName);
            if (stream is null) throw new InvalidOperationException($"Embedded Joliet naming rule template '{EmbeddedDefaultName}' was not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"Could not create '{path}': {ex.Message}";
            return false;
        }
    }

    public static JolietNamingRuleSet Load()
    {
        var warnings = new List<string>();
        if (!EnsureDefaultFileBesideExecutable(out string? createError))
        {
            if (!string.IsNullOrWhiteSpace(createError)) warnings.Add(createError);
            return new JolietNamingRuleSet(ExternalFilePath, false, Array.Empty<JolietNamingProfile>(), warnings);
        }

        var sections = new Dictionary<string, Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        string current = "General";
        try
        {
            foreach (string raw in File.ReadLines(ExternalFilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                {
                    current = line[1..^1].Trim();
                    sections.TryAdd(current, new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
                    continue;
                }
                int eq=line.IndexOf('=');
                if (eq<=0) continue;
                if (!sections.TryGetValue(current, out var values)) sections[current]=values=new(StringComparer.OrdinalIgnoreCase);
                values[line[..eq].Trim()] = line[(eq+1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read '{ExternalFilePath}': {ex.Message}");
            return new JolietNamingRuleSet(ExternalFilePath, false, Array.Empty<JolietNamingProfile>(), warnings);
        }

        bool enabled = GetBool(sections.TryGetValue("Joliet-Naming", out var general) ? general : null, "Enabled", true);
        var profiles = new List<JolietNamingProfile>();
        foreach (var pair in sections.Where(p => p.Key.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var v=pair.Value;
            if (!GetBool(v,"Enabled",true)) continue;
            string app=Get(v,"ApplicationContains");
            string prep=Get(v,"DataPreparerContains");
            string sys=Get(v,"SystemIdMatch","*");
            string name=Get(v,"Name",pair.Key);
            var methods=Get(v,"Methods").Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(prep) && (string.IsNullOrWhiteSpace(sys) || sys=="*"))
            {
                warnings.Add($"[{pair.Key}] ignored: it has no mastering signature selector.");
                continue;
            }
            if (methods.Count==0)
            {
                warnings.Add($"[{pair.Key}] ignored: Methods is empty.");
                continue;
            }
            profiles.Add(new(pair.Key,name,app,prep,sys,methods));
        }
        return new JolietNamingRuleSet(ExternalFilePath,enabled,profiles,warnings);
    }

    public static JolietNamingProfile? ResolveForInspection(SkeletonInspectionResult inspection, out IsoMasteringIdentity identity, out IReadOnlyList<string> warnings)
    {
        identity = ReadMasteringIdentity(inspection);
        IsoMasteringIdentity resolvedIdentity = identity;
        JolietNamingRuleSet set=Load();
        warnings=set.Warnings;
        if (!set.Enabled) return null;
        return FindMatch(set, resolvedIdentity);
    }

    internal static JolietNamingProfile? FindMatch(JolietNamingRuleSet set, IsoMasteringIdentity identity)
        => set.Enabled ? set.Profiles.FirstOrDefault(profile => Matches(profile, identity)) : null;

    public static bool ProfileAllows(JolietNamingProfile? profile, string method)
        => profile is null || profile.Methods.Contains(method) || profile.Methods.Contains("All");

    private static bool Matches(JolietNamingProfile p, IsoMasteringIdentity i)
    {
        if (!string.IsNullOrWhiteSpace(p.ApplicationContains) && !i.ApplicationId.Contains(p.ApplicationContains,StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(p.DataPreparerContains) && !i.DataPreparerId.Contains(p.DataPreparerContains,StringComparison.OrdinalIgnoreCase)) return false;
        string sys=p.SystemIdMatch.Trim();
        if (sys.Length>0 && sys!="*" && !i.SystemId.Equals(sys,StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static IsoMasteringIdentity ReadMasteringIdentity(SkeletonInspectionResult inspection)
    {
        try
        {
            using var fs=new FileStream(inspection.SkeletonPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            long physicalLba=inspection.BaseLba+16;
            byte[] payload=new byte[2048];
            if (inspection.ImageKind==SkeletonImageKind.Cooked2048)
            {
                fs.Position=checked(physicalLba*2048L); fs.ReadExactly(payload);
            }
            else
            {
                fs.Position=checked(physicalLba*2352L); byte[] raw=new byte[2352]; fs.ReadExactly(raw);
                int userOffset=raw[15]==2?24:16; Buffer.BlockCopy(raw,userOffset,payload,0,2048);
            }
            if (payload[0]!=1 || !payload.AsSpan(1,5).SequenceEqual("CD001"u8)) return new("","","");
            static string A(byte[] p,int o,int n)=>Encoding.ASCII.GetString(p,o,n).Trim(' ','\0');
            return new(A(payload,8,32),A(payload,574,128),A(payload,446,128));
        }
        catch { return new("","",""); }
    }

    private static string Get(Dictionary<string,string>? v,string k,string d="") => v is not null && v.TryGetValue(k,out string? x)?x.Trim():d;
    private static bool GetBool(Dictionary<string,string>? v,string k,bool d) => bool.TryParse(Get(v,k),out bool b)?b:d;
}
