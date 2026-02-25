namespace Cozo.DotNet.Om.Query;

public sealed class OmQueryRegistry
{
    private readonly Dictionary<string, NamedQueryDefinition> _queries = new(StringComparer.Ordinal);

    public OmQueryRegistry Register(NamedQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("NamedQuery name is required.", nameof(definition));
        }

        var key = Key(definition.Name, definition.Version);
        _queries[key] = definition;
        return this;
    }

    public bool TryGet(string name, out NamedQueryDefinition definition, string? version = null)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            return _queries.TryGetValue(Key(name, version!), out definition!);
        }

        var matches = _queries.Values
            .Where(q => string.Equals(q.Name, name, StringComparison.Ordinal))
            .OrderByDescending(q => q.Version, StringComparer.Ordinal)
            .ToArray();
        definition = matches.FirstOrDefault()!;
        return definition is not null;
    }

    public IReadOnlyList<NamedQueryDefinition> List() =>
        _queries.Values.OrderBy(q => q.Name, StringComparer.Ordinal).ThenBy(q => q.Version, StringComparer.Ordinal).ToArray();

    private static string Key(string name, string version) => $"{name}\u0001{version}";
}
