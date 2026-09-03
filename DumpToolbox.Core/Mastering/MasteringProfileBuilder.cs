namespace DumpToolbox.Core.Mastering;

internal sealed class MasteringProfileBuilder
{
    private readonly List<string> _names = new();
    private readonly List<string> _rules = new();

    public JolietRecordOrdering JolietRecordOrdering { get; set; } = JolietRecordOrdering.PreservePrimaryRecordOrder;
    public JolietPathTableOrdering JolietPathTableOrdering { get; set; } = JolietPathTableOrdering.PreservePrimaryDirectoryOrder;
    public byte? SupplementaryRootXaFileNumber { get; set; }

    public void AddName(string name)
    {
        if (!_names.Contains(name, StringComparer.OrdinalIgnoreCase))
            _names.Add(name);
    }

    public void AddRule(string rule)
    {
        if (!_rules.Contains(rule, StringComparer.Ordinal))
            _rules.Add(rule);
    }

    public IMasteringProfile Build()
    {
        string name = _names.Count == 0 ? "Generic / evidence-only" : string.Join(" + ", _names);
        return new MasteringProfile(name, _rules.ToArray(), JolietRecordOrdering, JolietPathTableOrdering, SupplementaryRootXaFileNumber);
    }
}
