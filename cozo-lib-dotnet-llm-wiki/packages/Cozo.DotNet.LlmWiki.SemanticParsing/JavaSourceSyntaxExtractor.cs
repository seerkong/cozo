namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Retains Java syntax needed by framework-level semantic derivation. This component deliberately
/// knows nothing about business relations, validation semantics, persistence, or lifecycle rules.
/// </summary>
internal static class JavaSourceSyntaxExtractor
{
    internal const string QuerySource = """
        (field_declaration
          type: (_) @member.type
          declarator: (variable_declarator name: (identifier) @member.name)) @member.field
        (record_declaration
          name: (identifier) @record.owner
          parameters: (formal_parameters
            (formal_parameter
              type: (_) @record.type
              name: (identifier) @record.name) @record.component))
        (method_declaration
          type: (_) @return.type
          name: (identifier) @return.method
          parameters: (formal_parameters) @return.params) @return.declaration
        (method_declaration
          name: (identifier) @parameter.method
          parameters: (formal_parameters
            (formal_parameter
              type: (_) @parameter.type
              name: (identifier) @parameter.name) @parameter.declaration) @parameter.params)
        (local_variable_declaration
          type: (_) @local.type
          declarator: (variable_declarator name: (identifier) @local.name)) @local.declaration
        (enum_constant name: (identifier) @enum.name) @enum.constant
        (assignment_expression
          left: [(identifier) (field_access)] @assignment.left
          right: (_) @assignment.right) @assignment.expression
        (method_invocation
          object: (_) @setter.receiver
          name: (identifier) @setter.name
          arguments: (argument_list) @setter.arguments) @setter.expression
        (method_invocation
          name: (identifier) @setter.name
          arguments: (argument_list) @setter.arguments) @setter.expression
        (if_statement
          condition: (_) @guard.condition
          consequence: (_) @guard.consequence) @guard.statement
        """;

    internal static ParsedJavaSourceSyntax Extract(IReadOnlyList<TreeSitterQueryMatchResult> matches)
    {
        var members = new List<ParsedJavaMemberSyntax>();
        var constants = new List<ParsedJavaEnumConstantSyntax>();
        var assignments = new List<ParsedJavaAssignmentSyntax>();
        var typeUses = new List<ParsedJavaTypeUseSyntax>();
        var guards = new List<ParsedJavaGuardSyntax>();
        var stateMutations = new List<ParsedJavaStateMutationSyntax>();

        foreach (var match in matches)
        {
            var captures = match.Captures.ToDictionary(capture => capture.CaptureName, StringComparer.Ordinal);
            if (captures.TryGetValue("member.field", out var field)
                && captures.TryGetValue("member.name", out var fieldName)
                && captures.TryGetValue("member.type", out var fieldType))
            {
                members.Add(new ParsedJavaMemberSyntax(
                    "field",
                    fieldName.Text,
                    fieldType.Text,
                    field.Text,
                    "",
                    field.StartLine,
                    field.EndLine));
                continue;
            }

            if (captures.TryGetValue("record.component", out var component)
                && captures.TryGetValue("record.name", out var componentName)
                && captures.TryGetValue("record.type", out var componentType)
                && captures.TryGetValue("record.owner", out var recordOwner))
            {
                members.Add(new ParsedJavaMemberSyntax(
                    "record_component",
                    componentName.Text,
                    componentType.Text,
                    component.Text,
                    recordOwner.Text,
                    component.StartLine,
                    component.EndLine));
                continue;
            }

            if (captures.TryGetValue("enum.constant", out var constant)
                && captures.TryGetValue("enum.name", out var constantName))
            {
                constants.Add(new ParsedJavaEnumConstantSyntax(
                    constantName.Text,
                    constant.Text,
                    constant.StartLine,
                    constant.EndLine));
                continue;
            }

            if (captures.TryGetValue("return.declaration", out var returnDeclaration)
                && captures.TryGetValue("return.method", out var returnMethod)
                && captures.TryGetValue("return.type", out var returnType)
                && captures.TryGetValue("return.params", out var returnParams))
            {
                typeUses.Add(new ParsedJavaTypeUseSyntax(
                    "return",
                    returnMethod.Text,
                    returnType.Text,
                    returnDeclaration.Text,
                    returnMethod.Text,
                    CountParameters(returnParams.Text),
                    returnDeclaration.StartLine,
                    returnDeclaration.EndLine));
                continue;
            }

            if (captures.TryGetValue("parameter.declaration", out var parameterDeclaration)
                && captures.TryGetValue("parameter.method", out var parameterMethod)
                && captures.TryGetValue("parameter.type", out var parameterType)
                && captures.TryGetValue("parameter.name", out var parameterName)
                && captures.TryGetValue("parameter.params", out var parameterParams))
            {
                typeUses.Add(new ParsedJavaTypeUseSyntax(
                    "parameter",
                    parameterName.Text,
                    parameterType.Text,
                    parameterDeclaration.Text,
                    parameterMethod.Text,
                    CountParameters(parameterParams.Text),
                    parameterDeclaration.StartLine,
                    parameterDeclaration.EndLine));
                continue;
            }

            if (captures.TryGetValue("local.declaration", out var localDeclaration)
                && captures.TryGetValue("local.type", out var localType)
                && captures.TryGetValue("local.name", out var localName))
            {
                typeUses.Add(new ParsedJavaTypeUseSyntax(
                    "local",
                    localName.Text,
                    localType.Text,
                    localDeclaration.Text,
                    "",
                    0,
                    localDeclaration.StartLine,
                    localDeclaration.EndLine));
                continue;
            }

            if (captures.TryGetValue("assignment.expression", out var expression)
                && captures.TryGetValue("assignment.left", out var left)
                && captures.TryGetValue("assignment.right", out var right))
            {
                assignments.Add(new ParsedJavaAssignmentSyntax(
                    left.Text,
                    right.Text,
                    expression.Text,
                    expression.StartLine,
                    expression.EndLine));
                stateMutations.Add(new ParsedJavaStateMutationSyntax(
                    "assignment",
                    ReceiverFromAssignmentLeft(left.Text),
                    LastIdentifier(left.Text),
                    right.Text,
                    expression.Text,
                    expression.StartLine,
                    expression.EndLine));
                continue;
            }

            if (captures.TryGetValue("setter.expression", out var setterExpression)
                && captures.TryGetValue("setter.name", out var setterName)
                && captures.TryGetValue("setter.arguments", out var setterArguments)
                && SetterProperty(setterName.Text) is { Length: > 0 } property)
            {
                var nameOffset = setterExpression.Text.IndexOf(setterName.Text, StringComparison.Ordinal);
                if (!captures.ContainsKey("setter.receiver")
                    && nameOffset > 0
                    && setterExpression.Text[..nameOffset].Contains('.', StringComparison.Ordinal))
                {
                    continue;
                }
                var argument = FirstArgument(setterArguments.Text);
                if (argument.Length == 0)
                {
                    continue;
                }
                stateMutations.Add(new ParsedJavaStateMutationSyntax(
                    "setter",
                    captures.TryGetValue("setter.receiver", out var setterReceiver)
                        ? setterReceiver.Text
                        : "this",
                    property,
                    argument,
                    setterExpression.Text,
                    setterExpression.StartLine,
                    setterExpression.EndLine));
                continue;
            }

            if (captures.TryGetValue("guard.statement", out var statement)
                && captures.TryGetValue("guard.condition", out var condition)
                && captures.TryGetValue("guard.consequence", out var consequence))
            {
                guards.Add(new ParsedJavaGuardSyntax(
                    condition.Text,
                    consequence.Text,
                    statement.Text,
                    statement.StartLine,
                    statement.EndLine));
            }
        }

        return new ParsedJavaSourceSyntax(
            members.OrderBy(member => member.StartLine).ThenBy(member => member.Name, StringComparer.Ordinal).ToArray(),
            constants.OrderBy(constant => constant.StartLine).ThenBy(constant => constant.Name, StringComparer.Ordinal).ToArray(),
            assignments.OrderBy(assignment => assignment.StartLine).ThenBy(assignment => assignment.LeftText, StringComparer.Ordinal).ToArray())
        {
            TypeUses = typeUses
                .OrderBy(typeUse => typeUse.StartLine)
                .ThenBy(typeUse => typeUse.UsageKind, StringComparer.Ordinal)
                .ThenBy(typeUse => typeUse.Name, StringComparer.Ordinal)
                .ToArray(),
            Guards = guards
                .OrderBy(guard => guard.StartLine)
                .ThenBy(guard => guard.ConditionText, StringComparer.Ordinal)
                .ToArray(),
            StateMutations = stateMutations
                .GroupBy(
                    mutation => (
                        mutation.MutationKind,
                        mutation.ReceiverText,
                        mutation.PropertyName,
                        mutation.ValueText,
                        mutation.StartLine,
                        mutation.EndLine),
                    StateMutationComparer.Instance)
                .Select(group => group.First())
                .OrderBy(mutation => mutation.StartLine)
                .ThenBy(mutation => mutation.MutationKind, StringComparer.Ordinal)
                .ThenBy(mutation => mutation.PropertyName, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static int CountParameters(string parameterListText)
    {
        var inner = parameterListText.Trim();
        if (inner.StartsWith('(')) inner = inner[1..];
        if (inner.EndsWith(')')) inner = inner[..^1];
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return 0;
        }

        var depth = 0;
        var count = 1;
        foreach (var ch in inner)
        {
            if (ch is '<' or '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is '>' or ')' or ']' or '}')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string SetterProperty(string methodName)
    {
        if (!methodName.StartsWith("set", StringComparison.Ordinal) || methodName.Length <= 3)
        {
            return "";
        }
        var property = methodName[3..];
        if (property.Length == 0 || !char.IsLetter(property[0]))
        {
            return "";
        }
        return char.ToLowerInvariant(property[0]) + property[1..];
    }

    private static string ReceiverFromAssignmentLeft(string leftText)
    {
        var dot = leftText.LastIndexOf('.');
        return dot > 0 ? leftText[..dot].Trim() : "this";
    }

    private static string FirstArgument(string argumentListText)
    {
        var inner = argumentListText.Trim();
        if (inner.StartsWith('(')) inner = inner[1..];
        if (inner.EndsWith(')')) inner = inner[..^1];
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return "";
        }
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < inner.Length; index++)
        {
            var ch = inner[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch is '(' or '[' or '{' or '<')
            {
                depth++;
            }
            else if (ch is ')' or ']' or '}' or '>')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                return inner[..index].Trim();
            }
        }
        return inner;
    }

    private static string LastIdentifier(string value)
    {
        var end = -1;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (char.IsLetterOrDigit(value[index]) || value[index] is '_' or '$')
            {
                end = index;
                break;
            }
        }
        if (end < 0)
        {
            return "";
        }
        var start = end;
        while (start >= 0 && (char.IsLetterOrDigit(value[start]) || value[start] is '_' or '$'))
        {
            start--;
        }
        return value[(start + 1)..(end + 1)];
    }

    private sealed class StateMutationComparer :
        IEqualityComparer<(string MutationKind, string ReceiverText, string PropertyName, string ValueText, int StartLine, int EndLine)>
    {
        public static readonly StateMutationComparer Instance = new();

        public bool Equals(
            (string MutationKind, string ReceiverText, string PropertyName, string ValueText, int StartLine, int EndLine) x,
            (string MutationKind, string ReceiverText, string PropertyName, string ValueText, int StartLine, int EndLine) y) =>
            StringComparer.Ordinal.Equals(x.MutationKind, y.MutationKind)
            && StringComparer.Ordinal.Equals(x.ReceiverText, y.ReceiverText)
            && StringComparer.Ordinal.Equals(x.PropertyName, y.PropertyName)
            && StringComparer.Ordinal.Equals(x.ValueText, y.ValueText)
            && x.StartLine == y.StartLine
            && x.EndLine == y.EndLine;

        public int GetHashCode(
            (string MutationKind, string ReceiverText, string PropertyName, string ValueText, int StartLine, int EndLine) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.MutationKind),
                StringComparer.Ordinal.GetHashCode(value.ReceiverText),
                StringComparer.Ordinal.GetHashCode(value.PropertyName),
                StringComparer.Ordinal.GetHashCode(value.ValueText),
                value.StartLine,
                value.EndLine);
    }
}
