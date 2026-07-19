using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;
using Cozo.DotNet;

namespace Cozo.DotNet.Om.Logic;

public static class TypeLogic
{
    public static async Task DefineTypeAsync(CozoOmRuntime runtime, DefineTypeInput input, CancellationToken cancellationToken = default)
    {
        var name = OmConvert.RequireName(input.Name, nameof(input.Name));
        var parent = string.IsNullOrWhiteSpace(input.ParentType)
            ? await TypeExistsAsync(runtime, name, cancellationToken)
                ? await GetParentTypeAsync(runtime, name, cancellationToken)
                : null
            : await ResolveTypeAsync(runtime, input.ParentType!, cancellationToken);
        await DefineTypeCoreAsync(runtime, name, input.Description, parent, input.Mixins, cancellationToken);
    }

    public static async Task DefineTypeAsync(CozoOmRuntime runtime, DefineTypePatchInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Parent);
        var name = OmConvert.RequireName(input.Name, nameof(input.Name));
        var parent = input.Parent.Kind switch
        {
            TypeParentPatchKind.Keep => await TypeExistsAsync(runtime, name, cancellationToken)
                ? await GetParentTypeAsync(runtime, name, cancellationToken)
                : null,
            TypeParentPatchKind.Set => await ResolveTypeAsync(runtime, input.Parent.ParentType!, cancellationToken),
            TypeParentPatchKind.Clear => null,
            _ => throw new ArgumentOutOfRangeException(nameof(input.Parent), input.Parent.Kind, "Unsupported parent patch"),
        };
        await DefineTypeCoreAsync(runtime, name, input.Description, parent, input.Mixins, cancellationToken);
    }

    private static async Task DefineTypeCoreAsync(
        CozoOmRuntime runtime,
        string name,
        string description,
        string? parent,
        IReadOnlyList<string>? mixins,
        CancellationToken cancellationToken)
    {
        if (parent is not null)
        {
            if (!await TypeExistsAsync(runtime, parent, cancellationToken))
            {
                throw new CozoException($"Parent type '{parent}' does not exist");
            }

            var ancestors = await GetAncestorsAsync(runtime, parent, cancellationToken);
            if (parent == name || ancestors.Contains(name))
            {
                throw new CozoException($"Circular inheritance detected: '{name}' -> '{parent}'");
            }
        }

        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_type", ["name"], ["description", "parent_type"]),
            LogicSupport.Params(("name", name), ("description", description), ("parent_type", parent)),
            cancellationToken: cancellationToken);

        if (mixins is not null)
        {
            var existing = await GetTypeMixinsAsync(runtime, name, cancellationToken);
            foreach (var mixin in existing)
            {
                await runtime.Store.RunAsync(
                    """
                    ?[type_name, mixin_name] <- [[$type_name, $mixin_name]]
                    :rm om_type_mixin {type_name, mixin_name}
                    """,
                    LogicSupport.Params(("type_name", name), ("mixin_name", mixin)),
                    cancellationToken: cancellationToken);
            }

            foreach (var mixin in mixins)
            {
                var mixinName = OmConvert.RequireName(mixin, nameof(mixins));
                if (!await MixinExistsAsync(runtime, mixinName, cancellationToken))
                {
                    throw new CozoException($"Mixin '{mixinName}' does not exist");
                }

                await runtime.Store.RunAsync(
                    CozoScriptBuilder.InputPut("om_type_mixin", ["type_name", "mixin_name"], []),
                    LogicSupport.Params(("type_name", name), ("mixin_name", mixinName)),
                    cancellationToken: cancellationToken);
            }
        }
    }

    public static Task DefineMixinAsync(CozoOmRuntime runtime, DefineMixinInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_mixin", ["name"], ["description"]),
            LogicSupport.Params(("name", OmConvert.RequireName(input.Name, nameof(input.Name))), ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static async Task DefineAttributeAsync(CozoOmRuntime runtime, DefineAttributeInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveTypeAsync(runtime, input.TypeName, cancellationToken);
        var attrName = await ResolveAttrAsync(runtime, typeName, input.AttrName, cancellationToken);
        var storedType = OmConvert.ValueTypeToStored(input.ValueType);
        if (input.ValueType == OmValueType.Unknown)
        {
            throw new CozoException($"Unsupported attribute value type '{input.ValueType}'");
        }

        var inherited = await GetInheritedAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        if (inherited.TryGetValue(attrName, out var inheritedDefinition))
        {
            if (inheritedDefinition.ValueType != input.ValueType)
            {
                throw new CozoException($"Cannot change value_type of '{attrName}' (inherited as {inheritedDefinition.ValueType})");
            }

            if (inheritedDefinition.Required && !input.Required)
            {
                throw new CozoException($"Cannot loosen required constraint of '{attrName}' (inherited as required)");
            }
        }

        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_attr_def", ["type_name", "attr_name"], ["value_type", "required"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("attr_name", attrName),
                ("value_type", storedType),
                ("required", input.Required)),
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(input.Description))
        {
            await runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_attr_desc", ["type_name", "attr_name"], ["description"]),
                LogicSupport.Params(("type_name", typeName), ("attr_name", attrName), ("description", input.Description)),
                cancellationToken: cancellationToken);
        }
    }

    public static async Task DefineRelationAsync(CozoOmRuntime runtime, DefineRelationInput input, CancellationToken cancellationToken = default)
    {
        var relName = await ResolveRelAsync(runtime, input.RelName, cancellationToken);
        var fromType = await ResolveTypeAsync(runtime, input.FromType, cancellationToken);
        var toType = await ResolveTypeAsync(runtime, input.ToType, cancellationToken);

        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_rel_def", ["rel_name"], ["from_type", "to_type", "directed"]),
            LogicSupport.Params(
                ("rel_name", relName),
                ("from_type", fromType),
                ("to_type", toType),
                ("directed", input.Directed)),
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(input.Description))
        {
            await runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_rel_desc", ["rel_name"], ["description"]),
                LogicSupport.Params(("rel_name", relName), ("description", input.Description)),
                cancellationToken: cancellationToken);
        }
    }

    public static Task DefineTypeAliasAsync(CozoOmRuntime runtime, string alias, string canonical, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_alias_type", ["alias"], ["canonical"]),
            LogicSupport.Params(
                ("alias", OmConvert.RequireName(alias, nameof(alias))),
                ("canonical", OmConvert.RequireName(canonical, nameof(canonical)))),
            cancellationToken: cancellationToken);
    }

    public static Task DefineRelationAliasAsync(CozoOmRuntime runtime, string alias, string canonical, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_alias_rel", ["alias"], ["canonical"]),
            LogicSupport.Params(
                ("alias", OmConvert.RequireName(alias, nameof(alias))),
                ("canonical", OmConvert.RequireName(canonical, nameof(canonical)))),
            cancellationToken: cancellationToken);
    }

    public static Task DefineAttributeAliasAsync(CozoOmRuntime runtime, string typeName, string aliasAttr, string canonicalAttr, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_alias_attr", ["type_name", "alias_attr"], ["canonical_attr"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(typeName, nameof(typeName))),
                ("alias_attr", OmConvert.RequireName(aliasAttr, nameof(aliasAttr))),
                ("canonical_attr", OmConvert.RequireName(canonicalAttr, nameof(canonicalAttr)))),
            cancellationToken: cancellationToken);
    }

    public static async Task<string> ResolveTypeAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken = default)
    {
        var current = OmConvert.RequireName(typeName, nameof(typeName));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            var result = await runtime.Store.RunAsync(
                """
                ?[canonical] :=
                  *om_alias_type{ alias: $alias, canonical }
                :limit 1
                """,
                LogicSupport.Params(("alias", current)),
                cancellationToken: cancellationToken);
            if (result.Rows.Count == 0) return current;
            current = JsonRows.StringAt(result.Rows[0], 0) ?? current;
        }

        throw new CozoException($"Type alias cycle detected at '{current}'");
    }

    public static async Task<string> ResolveRelAsync(CozoOmRuntime runtime, string relName, CancellationToken cancellationToken = default)
    {
        var current = OmConvert.RequireName(relName, nameof(relName));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            var result = await runtime.Store.RunAsync(
                """
                ?[canonical] :=
                  *om_alias_rel{ alias: $alias, canonical }
                :limit 1
                """,
                LogicSupport.Params(("alias", current)),
                cancellationToken: cancellationToken);
            if (result.Rows.Count == 0) return current;
            current = JsonRows.StringAt(result.Rows[0], 0) ?? current;
        }

        throw new CozoException($"Relation alias cycle detected at '{current}'");
    }

    public static async Task<string> ResolveAttrAsync(CozoOmRuntime runtime, string typeName, string attrName, CancellationToken cancellationToken = default)
    {
        var canonicalType = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var current = OmConvert.RequireName(attrName, nameof(attrName));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var aliasScopes = new[] { canonicalType }.Concat(await GetAncestorsAsync(runtime, canonicalType, cancellationToken)).ToArray();
        while (seen.Add(current))
        {
            foreach (var scope in aliasScopes)
            {
                var result = await runtime.Store.RunAsync(
                    """
                    ?[canonical_attr] :=
                      *om_alias_attr{ type_name: $type_name, alias_attr: $alias_attr, canonical_attr }
                    :limit 1
                    """,
                    LogicSupport.Params(("type_name", scope), ("alias_attr", current)),
                    cancellationToken: cancellationToken);
                if (result.Rows.Count == 0) continue;
                current = JsonRows.StringAt(result.Rows[0], 0) ?? current;
                goto NextAlias;
            }

            return current;

        NextAlias:
            continue;
        }

        throw new CozoException($"Attribute alias cycle detected at '{current}'");
    }

    public static async Task<IReadOnlyList<string>> GetAncestorsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        var current = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            var parent = await GetParentTypeAsync(runtime, current, cancellationToken);
            if (string.IsNullOrWhiteSpace(parent)) break;
            result.Add(parent);
            current = parent;
        }

        return result;
    }

    public static async Task<IReadOnlyList<string>> GetDescendantsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken = default)
    {
        var root = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var rows = await runtime.Store.RunAsync(
            """
            ?[name, parent_type] :=
              *om_type{ name, description: _d, parent_type }
            """,
            cancellationToken: cancellationToken);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in rows.Rows)
        {
            var name = JsonRows.StringAt(row, 0);
            var parent = JsonRows.StringAt(row, 1);
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(parent))
            {
                if (!children.TryGetValue(parent!, out var direct))
                {
                    direct = [];
                    children[parent!] = direct;
                }

                direct.Add(name!);
            }
        }

        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var direct)) continue;
            foreach (var child in direct)
            {
                if (!visited.Add(child)) continue;
                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    public static async Task<bool> IsSubtypeOfAsync(CozoOmRuntime runtime, string childType, string parentType, CancellationToken cancellationToken = default)
    {
        var child = await ResolveTypeAsync(runtime, childType, cancellationToken);
        var parent = await ResolveTypeAsync(runtime, parentType, cancellationToken);
        if (child == parent) return true;
        var ancestors = await GetAncestorsAsync(runtime, child, cancellationToken);
        return ancestors.Contains(parent);
    }

    public static async Task<TypeHierarchy> GetTypeHierarchyAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        var rows = await runtime.Store.RunAsync(
            """
            ?[name, description, parent_type] :=
              *om_type{ name, description, parent_type }
            :sort name
            """,
            cancellationToken: cancellationToken);
        var mixinRows = await runtime.Store.RunAsync(
            """
            ?[type_name, mixin_name] :=
              *om_type_mixin{ type_name, mixin_name }
            :sort type_name, mixin_name
            """,
            cancellationToken: cancellationToken);
        var mixinsByType = mixinRows.Rows
            .GroupBy(row => JsonRows.StringAt(row, 0) ?? "")
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(row => JsonRows.StringAt(row, 1) ?? "").Where(x => x.Length > 0).ToArray(), StringComparer.Ordinal);
        var mutable = rows.Rows.ToDictionary(
            row => JsonRows.StringAt(row, 0) ?? "",
            row => new MutableTypeNode(
                JsonRows.StringAt(row, 0) ?? "",
                JsonRows.StringAt(row, 1) ?? "",
                string.IsNullOrWhiteSpace(JsonRows.StringAt(row, 2)) ? null : JsonRows.StringAt(row, 2),
                mixinsByType.GetValueOrDefault(JsonRows.StringAt(row, 0) ?? "") ?? [],
                []),
            StringComparer.Ordinal);
        foreach (var node in mutable.Values)
        {
            if (node.ParentType is not null && mutable.TryGetValue(node.ParentType, out var parent))
            {
                parent.Children.Add(node.Name);
            }
        }

        var final = mutable.ToDictionary(
            p => p.Key,
            p => new TypeHierarchyNode(p.Value.Name, p.Value.Description, p.Value.ParentType, p.Value.Mixins.Order().ToArray(), p.Value.Children.Order().ToArray()),
            StringComparer.Ordinal);
        var roots = final.Values.Where(n => n.ParentType is null || !final.ContainsKey(n.ParentType)).Select(n => n.Name).Order().ToArray();
        return new TypeHierarchy(final, roots);
    }

    internal static async Task<bool> TypeExistsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var name = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        return await LogicSupport.ExistsAsync(runtime, "om_type", "name", name, cancellationToken);
    }

    internal static async Task<bool> MixinExistsAsync(CozoOmRuntime runtime, string mixinName, CancellationToken cancellationToken)
    {
        return await LogicSupport.ExistsAsync(runtime, "om_mixin", "name", mixinName, cancellationToken);
    }

    internal static async Task<string?> GetParentTypeAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[parent_type] :=
              *om_type{ name: $name, description: _d, parent_type }
            :limit 1
            """,
            LogicSupport.Params(("name", typeName)),
            cancellationToken: cancellationToken);
        return result.Rows.Count == 0 ? null : JsonRows.StringAt(result.Rows[0], 0);
    }

    internal static async Task<IReadOnlyList<string>> GetTypeMixinsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[mixin_name] :=
              *om_type_mixin{ type_name: $type_name, mixin_name }
            :sort mixin_name
            """,
            LogicSupport.Params(("type_name", typeName)),
            cancellationToken: cancellationToken);
        return result.Rows.Select(row => JsonRows.StringAt(row, 0) ?? "").Where(x => x.Length > 0).ToArray();
    }

    public static async Task<IReadOnlyDictionary<string, OmAttribute>> GetAttributeDefinitionsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var canonical = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var definitions = new Dictionary<string, OmAttribute>(await GetInheritedAttributeDefinitionsAsync(runtime, canonical, cancellationToken), StringComparer.Ordinal);
        foreach (var (attrName, attribute) in await GetOwnAttributeDefinitionsAsync(runtime, canonical, cancellationToken))
        {
            definitions[attrName] = attribute;
        }

        return definitions;
    }

    internal static async Task<IReadOnlyDictionary<string, OmAttribute>> GetInheritedAttributeDefinitionsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var canonical = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var ancestors = await GetAncestorsAsync(runtime, canonical, cancellationToken);
        var definitions = new Dictionary<string, OmAttribute>(StringComparer.Ordinal);

        var mixins = new List<string>();
        foreach (var mixin in await GetTypeMixinsAsync(runtime, canonical, cancellationToken))
        {
            if (!mixins.Contains(mixin, StringComparer.Ordinal)) mixins.Add(mixin);
        }

        foreach (var ancestor in ancestors)
        {
            foreach (var mixin in await GetTypeMixinsAsync(runtime, ancestor, cancellationToken))
            {
                if (!mixins.Contains(mixin, StringComparer.Ordinal)) mixins.Add(mixin);
            }
        }

        foreach (var mixin in mixins)
        {
            foreach (var (attrName, attribute) in await GetOwnAttributeDefinitionsAsync(runtime, mixin, cancellationToken))
            {
                definitions[attrName] = attribute;
            }
        }

        foreach (var currentType in ancestors.Reverse())
        {
            foreach (var (attrName, attribute) in await GetOwnAttributeDefinitionsAsync(runtime, currentType, cancellationToken))
            {
                definitions[attrName] = attribute;
            }
        }

        return definitions;
    }

    internal static async Task<IReadOnlyDictionary<string, OmAttribute>> GetOwnAttributeDefinitionsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, OmAttribute>(StringComparer.Ordinal);
        var descRows = await runtime.Store.RunAsync(
            """
            ?[attr_name, description] :=
              *om_attr_desc{ type_name: $type_name, attr_name, description }
            """,
            LogicSupport.Params(("type_name", typeName)),
            cancellationToken: cancellationToken);
        var descriptions = descRows.Rows.ToDictionary(
            row => JsonRows.StringAt(row, 0) ?? "",
            row => JsonRows.StringAt(row, 1) ?? "",
            StringComparer.Ordinal);

        var rows = await runtime.Store.RunAsync(
            """
            ?[attr_name, value_type, required] :=
              *om_attr_def{ type_name: $type_name, attr_name, value_type, required }
            """,
            LogicSupport.Params(("type_name", typeName)),
            cancellationToken: cancellationToken);
        foreach (var row in rows.Rows)
        {
            var attrName = JsonRows.StringAt(row, 0) ?? "";
            descriptions.TryGetValue(attrName, out var description);
            definitions[attrName] = new OmAttribute(
                typeName,
                attrName,
                OmConvert.StoredToValueType(JsonRows.StringAt(row, 1) ?? ""),
                JsonRows.BoolAt(row, 2),
                string.IsNullOrWhiteSpace(description) ? null : description);
        }

        return definitions;
    }

    internal static async Task<IReadOnlyList<string>> GetAttributeAliasesForCanonicalAsync(
        CozoOmRuntime runtime,
        string typeName,
        string canonicalAttr,
        CancellationToken cancellationToken)
    {
        var canonicalType = await ResolveTypeAsync(runtime, typeName, cancellationToken);
        var canonical = await ResolveAttrAsync(runtime, canonicalType, canonicalAttr, cancellationToken);
        var scopes = new[] { canonicalType }.Concat(await GetAncestorsAsync(runtime, canonicalType, cancellationToken)).ToArray();
        var aliases = new List<string>();
        foreach (var scope in scopes)
        {
            var rows = await runtime.Store.RunAsync(
                """
                ?[alias_attr] :=
                  *om_alias_attr{ type_name: $type_name, alias_attr, canonical_attr: _canonical_attr }
                :sort alias_attr
                """,
                LogicSupport.Params(("type_name", scope)),
                cancellationToken: cancellationToken);
            foreach (var row in rows.Rows)
            {
                var alias = JsonRows.StringAt(row, 0);
                if (string.IsNullOrWhiteSpace(alias) || aliases.Contains(alias, StringComparer.Ordinal)) continue;
                try
                {
                    if (await ResolveAttrAsync(runtime, scope, alias!, cancellationToken) == canonical)
                    {
                        aliases.Add(alias!);
                    }
                }
                catch (CozoException)
                {
                    // Cyclic aliases should remain visible to direct resolution errors,
                    // but legacy read fallback should skip them instead of breaking reads.
                }
            }
        }

        aliases.Sort(StringComparer.Ordinal);
        return aliases;
    }

    internal static async Task<OmRelation> GetRelationDefinitionAsync(CozoOmRuntime runtime, string relName, CancellationToken cancellationToken)
    {
        var canonical = await ResolveRelAsync(runtime, relName, cancellationToken);
        var rows = await runtime.Store.RunAsync(
            """
            ?[from_type, to_type, directed] :=
              *om_rel_def{ rel_name: $rel_name, from_type, to_type, directed }
            :limit 1
            """,
            LogicSupport.Params(("rel_name", canonical)),
            cancellationToken: cancellationToken);
        if (rows.Rows.Count == 0)
        {
            throw new CozoException($"Relation '{canonical}' is not defined");
        }

        var row = rows.Rows[0];
        return new OmRelation(canonical, JsonRows.StringAt(row, 0) ?? "", JsonRows.StringAt(row, 1) ?? "", JsonRows.BoolAt(row, 2));
    }

    private sealed record MutableTypeNode(string Name, string Description, string? ParentType, IReadOnlyList<string> Mixins, List<string> Children);
}
