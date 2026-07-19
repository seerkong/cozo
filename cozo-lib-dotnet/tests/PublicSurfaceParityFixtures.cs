using System.Reflection;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Runtime;

internal static class PublicSurfaceParityFixtures
{
    public static async Task RunAsync()
    {
        using var db = new CozoDb(engine: "mem", path: "");
        var om = new CozoOm(db);
        await om.InitSchemaAsync();

        await om.DefineTypeAsync("PublicSurfaceParent", "parent");
        await om.DefineTypeAsync("PublicSurfaceChild", "child", parentType: "PublicSurfaceParent");
        await om.DefineTypeAsync("PublicSurfaceChild", "child rewritten", parentType: null);
        Require(
            await om.IsSubtypeOfAsync("PublicSurfaceChild", "PublicSurfaceParent"),
            "legacy nullable DefineTypeAsync parent input must preserve an existing parent");

        var publicMethods = typeof(CozoOm).GetMethods(BindingFlags.Instance | BindingFlags.Public);
        Require(
            publicMethods.Any(method => method.Name == "ValidateConstraintsAsync"),
            "CozoOm must expose a public ValidateConstraintsAsync facade");
        Require(
            publicMethods.Any(method => method.Name == "TraverseAsync"),
            "CozoOm must expose a public TraverseAsync facade");
        Require(
            publicMethods.Any(method => method.Name == "InferValueType"),
            "CozoOm must expose a public InferValueType facade");
        Require(
            publicMethods.Any(method => method.Name == "DefineTypeAsync"
                                     && method.GetParameters().Any(parameter => parameter.ParameterType.Name.Contains("Patch", StringComparison.Ordinal))),
            "CozoOm must expose an explicit Keep/Set/Clear parent patch API");

        Require(
            TypeParentPatch.Keep is { Kind: TypeParentPatchKind.Keep, ParentType: null }
            && TypeParentPatch.Clear is { Kind: TypeParentPatchKind.Clear, ParentType: null }
            && TypeParentPatch.Set("PublicSurfaceParent") is { Kind: TypeParentPatchKind.Set, ParentType: "PublicSurfaceParent" },
            "typed parent patch contracts must preserve distinct Keep, Set, and Clear intents");
        await ExpectArgumentFailureAsync(
            () => Task.Run(() => TypeParentPatch.Set(" ")),
            "a Set parent patch must require a concrete parent type");

        await om.DefineTypeAsync(new DefineTypePatchInput(
            "PublicSurfaceChild",
            "child cleared",
            TypeParentPatch.Clear));
        Require(
            !await om.IsSubtypeOfAsync("PublicSurfaceChild", "PublicSurfaceParent"),
            "an explicit Clear parent patch must remove the existing parent");
        await om.DefineTypeAsync(new DefineTypePatchInput(
            "PublicSurfaceChild",
            "child reset",
            TypeParentPatch.Set("PublicSurfaceParent")));
        await om.DefineTypeAsync(new DefineTypePatchInput(
            "PublicSurfaceChild",
            "child kept",
            TypeParentPatch.Keep));
        Require(
            await om.IsSubtypeOfAsync("PublicSurfaceChild", "PublicSurfaceParent"),
            "an explicit Keep parent patch must retain the existing parent");

        await om.DefineAttributeAsync("PublicSurfaceParent", "required_name", OmValueType.String, required: true);
        await om.CreateEntityAsync("public-surface:entity", "PublicSurfaceParent", "entity");
        await om.DefineConstraintAsync("PublicSurfaceParent", "public_surface_constraint", "custom");
        om.RegisterValidator(
            "PublicSurfaceParent",
            "public_surface_constraint",
            _ => ValueTask.FromResult<string?>("public constraint failure"));
        Require(
            !(await om.ValidateConstraintsAsync("public-surface:entity", ["custom"])).Valid,
            "the public constraint facade must preserve validation errors");
        Require(
            om.InferValueType("text") == OmValueType.String
            && om.InferValueType(42) == OmValueType.Number
            && om.InferValueType(true) == OmValueType.Bool
            && om.InferValueType(null) == OmValueType.Unknown,
            "the public value type facade must preserve internal inference semantics");

        await om.DefineTypeAsync("PublicSurfaceTarget", "target");
        await om.DefineRelationAsync("public_surface_rel", "PublicSurfaceParent", "PublicSurfaceTarget");
        await om.CreateEntityAsync("public-surface:target", "PublicSurfaceTarget", "target");
        await om.LinkEntitiesAsync("public-surface:entity", "public_surface_rel", "public-surface:target", options: new WriteOptions(SkipConstraints: true));
        Require(
            (await om.TraverseAsync("public-surface:entity", [])).Select(entity => entity.Id).SequenceEqual(["public-surface:entity"])
            && (await om.TraverseAsync("public-surface:entity", ["public_surface_rel"])).Select(entity => entity.Id).SequenceEqual(["public-surface:target"]),
            "the public traversal facade must return the start node for an empty path and outgoing endpoints for a relation path");

        var alwaysTrue = new Func<OmValidationContext, ValueTask<bool>>(_ => ValueTask.FromResult(true));
        const string missingOwner = "PublicSurfaceMissingOwner";
        await ExpectCozoFailureAsync(
            () => om.AddInterceptorAsync(missingOwner, "missing_owner_action", "before", _ => ValueTask.CompletedTask),
            "missing behavior owner must fail before registration");
        await ExpectArgumentFailureAsync(
            () => om.DefineConstraintAsync("PublicSurfaceParent", "invalid_scope", "request", alwaysTrue, alwaysTrue),
            "invalid constraint scope must fail before registration");
        await ExpectArgumentFailureAsync(
            () => om.AddInterceptorAsync("PublicSurfaceParent", "invalid_phase_action", "around", _ => ValueTask.CompletedTask),
            "invalid interceptor phase must fail before registration");

        Require(
            DefinitionCount(db, "interceptor", missingOwner, "missing_owner_action") == 0
            && DefinitionCount(db, "constraint", "PublicSurfaceParent", "invalid_scope") == 0
            && DefinitionCount(db, "interceptor", "PublicSurfaceParent", "invalid_phase_action") == 0,
            "invalid behavior definitions must not leave persistent metadata");
        Require(
            om.Runtime.Registry.GetInterceptors(missingOwner, "missing_owner_action", "before").Count == 0
            && !om.Runtime.Registry.TryGetConstraint("PublicSurfaceParent", "invalid_scope", out _)
            && om.Runtime.Registry.GetInterceptors("PublicSurfaceParent", "invalid_phase_action", "before").Count == 0
            && om.Runtime.Registry.GetInterceptors("PublicSurfaceParent", "invalid_phase_action", "after").Count == 0,
            "invalid behavior definitions must not leave callback registry ghosts");
    }

    private static int DefinitionCount(CozoDb db, string kind, string owner, string name)
    {
        var script = kind switch
        {
            "constraint" => "?[type_name, constraint_name] := *om_constraint_def{type_name, constraint_name}, type_name = $owner, constraint_name = $name",
            "interceptor" => "?[type_name, action_name] := *om_interceptor_def{type_name, action_name}, type_name = $owner, action_name = $name",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported definition kind"),
        };
        using var result = db.Run(script, new { owner, name });
        return result.RootElement.GetProperty("rows").GetArrayLength();
    }

    private static async Task ExpectCozoFailureAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (CozoException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task ExpectArgumentFailureAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
