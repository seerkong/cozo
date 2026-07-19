using Cozo.DotNet.LlmWiki.SemanticParsing;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class JavaExtractorContractTests
{
    private const string JavaContractQuerySource = """
        (package_declaration (scoped_identifier) @package.name) @package.decl
        (import_declaration) @import
        (class_declaration name: (identifier) @class.name) @class.decl
        (interface_declaration name: (identifier) @interface.name) @interface.decl
        (enum_declaration name: (identifier) @enum.name) @enum.decl
        (record_declaration name: (identifier) @record.name parameters: (formal_parameters) @record.params) @record.decl
        (annotation_type_declaration name: (identifier) @annotation.name) @annotation.decl
        (method_declaration name: (identifier) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (constructor_declaration name: (identifier) @constructor.name parameters: (formal_parameters) @constructor.params) @constructor.decl
        (field_declaration declarator: (variable_declarator name: (identifier) @field.name)) @field.decl
        (enum_constant name: (identifier) @enumconstant.name) @enumconstant.decl
        (superclass) @extends
        (super_interfaces) @implements
        (extends_interfaces) @extends
        (modifiers) @modifier
        (method_invocation object: _ @call.receiver name: (identifier) @call.target arguments: (argument_list) @call.args) @call.site
        (method_invocation name: (identifier) @call.target arguments: (argument_list) @call.args) @call.site
        (object_creation_expression type: (_) @new.type arguments: (argument_list) @new.args) @new.site
        (assignment_expression left: (field_access) @assign.lhs)
        (field_access object: _ @access.receiver field: (_) @access.target) @access.site
        """;

    private const string JavaSample = """
        package com.acme.orders;

        import java.time.Instant;
        import java.util.List;
        import static java.util.Objects.requireNonNull;

        public class OrderService extends BaseService implements Workflow, Auditable {
            private final Repository repository;
            public int count;

            public OrderService(Repository repository) {
                this.repository = requireNonNull(repository);
            }

            public OrderDto create(String id, int quantity) {
                Order order = new Order(id, quantity);
                repository.save(order);
                this.count = this.count + 1;
                audit(order.id());
                return new OrderDto(order.id(), Instant.now());
            }

            private void audit(String id) {
                Logger.info(id);
            }

            public static class NestedHelper {
                public void touch() {
                    List<String> values = List.of("ok");
                    values.size();
                }
            }
        }

        interface Workflow {
            void start();
        }

        public interface Auditable {
            void audit(String id);
        }

        public enum OrderStatus {
            NEW,
            DONE
        }

        public record OrderDto(String id, Instant createdAt) implements Payload {
        }

        public @interface Tracked {
            String value();
        }

        class Order extends BaseOrder implements Payload {
            private final String id;

            Order(String id, int quantity) {
                this.id = id;
            }

            String id() {
                return id;
            }
        }

        interface Payload {
        }
        """;

    public static void Run(TreeSitterNativeBackend javaBackend, Action<bool, string> assert)
    {
        using var query = javaBackend.TryCompileQuery("java", JavaContractQuerySource, out var queryDiagnostic);
        assert(queryDiagnostic?.Code != "TSQUERY001",
            "Java contract query should not reference unknown tree-sitter-java nodes: " + queryDiagnostic?.Message);
        assert(query is not null,
            "Java contract query should compile against tree-sitter-java v0.23.5: " + queryDiagnostic?.Message);

        var matches = query!.ExecuteMatches(JavaSample);
        var captureNames = matches.SelectMany(match => match.Captures).Select(capture => capture.CaptureName).ToArray();
        assert(captureNames.Contains("package.name"), "Java query should capture package declarations");
        assert(captureNames.Count(capture => capture == "import") == 3, "Java query should capture all imports");
        assert(captureNames.Contains("class.name")
                && captureNames.Contains("interface.name")
                && captureNames.Contains("enum.name")
                && captureNames.Contains("record.name")
                && captureNames.Contains("annotation.name"),
            "Java query should cover class/interface/enum/record/annotation type declarations");
        assert(captureNames.Contains("constructor.name")
                && captureNames.Contains("method.name")
                && captureNames.Contains("field.name")
                && captureNames.Contains("enumconstant.name"),
            "Java query should cover constructor/method/field/enum constant declarations");
        assert(captureNames.Contains("call.target")
                && captureNames.Contains("new.type")
                && captureNames.Contains("access.target")
                && captureNames.Contains("assign.lhs"),
            "Java query should cover call/new/read/write capture sites");

        var parsed = javaBackend.Parse("OrderWorkflow.java", JavaSample, "java");
        assert(parsed.Success && parsed.BackendName == "native" && parsed.LanguageId == "java",
            "Java sample should parse directly through the native backend");
        assert(!parsed.HasErrors,
            "fixed Java extractor sample should parse without syntax errors: "
            + string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var symbols = FlattenSymbols(parsed.Symbols).ToArray();
        assert(symbols.Length > 0,
            "Java extractor should produce symbols from native Java parses; current empty output is the T2.1.1 red light");

        var package = symbols.Single(s => s.Kind == "package" && s.Name == "com.acme.orders");
        assert(package.SymKey == "java:com.acme.orders#0",
            $"package sym_key should be java:com.acme.orders#0 (got {package.SymKey})");

        var orderService = symbols.Single(s => s.Kind == "class" && s.Name == "OrderService");
        assert(orderService.Kind == "class"
                && orderService.Visibility == "public"
                && orderService.Exported
                && orderService.SymKey == "java:com.acme.orders.OrderService#0",
            $"public class should be exported with stable java sym_key (got {orderService.Kind}/{orderService.Visibility}/{orderService.SymKey})");
        assert(orderService.Signature.StartsWith("public class OrderService extends BaseService", StringComparison.Ordinal),
            "class signature should retain the declaration line");

        var repository = symbols.Single(s => s.Name == "repository");
        assert(repository.Kind == "field"
                && repository.Visibility == "private"
                && repository.SymKey == "java:com.acme.orders.OrderService.repository#0",
            "private field should carry visibility and java sym_key");
        var ctor = symbols.Single(s => s.Kind == "constructor" && s.Name == "OrderService");
        assert(ctor.SymKey == "java:com.acme.orders.OrderService.OrderService#1",
            $"constructor sym_key should carry parameter arity (got {ctor.SymKey})");
        var create = symbols.Single(s => s.Name == "create");
        assert(create.Kind == "method"
                && create.Visibility == "public"
                && create.Exported
                && create.SymKey == "java:com.acme.orders.OrderService.create#2",
            "public method should carry visibility/exported and arity in sym_key");
        var nested = symbols.Single(s => s.Name == "NestedHelper");
        assert(nested.Kind == "class"
                && nested.SymKey == "java:com.acme.orders.OrderService.NestedHelper#0",
            "nested class should carry its declaring class in sym_key");
        assert(symbols.Single(s => s.Name == "touch").SymKey == "java:com.acme.orders.OrderService.NestedHelper.touch#0",
            "nested method should carry full nested qualifier");

        assert(symbols.Single(s => s.Name == "Workflow").Kind == "interface", "interface declaration should be extracted");
        assert(symbols.Single(s => s.Name == "OrderStatus").Kind == "enum", "enum declaration should be extracted");
        assert(symbols.Single(s => s.Name == "OrderDto").Kind == "record", "record declaration should be extracted");
        assert(symbols.Single(s => s.Name == "Tracked").Kind == "annotation", "annotation type declaration should be extracted");
        assert(symbols.Single(s => s.Name == "NEW").Kind == "field"
                && symbols.Single(s => s.Name == "DONE").Kind == "field",
            "enum constants should map to field-like symbols");
        assert(symbols.All(s => s.SymKey.StartsWith("java:", StringComparison.Ordinal) && s.SymKey.Contains('#')),
            "every Java symbol should carry a java:<qualified>#arity sym_key");
        assert(symbols.All(s => s.StartLine >= 1 && s.EndLine >= s.StartLine),
            "Java symbols should carry 1-based line ranges");

        var edges = parsed.Edges;
        assert(HasEdge(edges, "OrderWorkflow.java", "com.acme.orders", "CONTAINS", 0.9, "syntax"),
            "file should CONTAINS the package");
        assert(HasEdge(edges, "com.acme.orders", "com.acme.orders.OrderService", "CONTAINS", 0.9, "syntax"),
            "package should CONTAINS top-level declarations");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "com.acme.orders.OrderService.NestedHelper", "CONTAINS", 0.9, "syntax"),
            "declaring class should CONTAINS nested declarations");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "com.acme.orders.OrderService.create", "HAS_METHOD", 0.9, "syntax"),
            "class should HAS_METHOD its methods");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "com.acme.orders.OrderService.OrderService", "HAS_METHOD", 0.9, "syntax"),
            "class should HAS_METHOD its constructor");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "com.acme.orders.OrderService.repository", "HAS_PROPERTY", 0.9, "syntax"),
            "class should HAS_PROPERTY fields");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "BaseService", "EXTENDS", 0.9, "syntax"),
            "class superclass should map to EXTENDS with syntax evidence");
        assert(HasEdge(edges, "com.acme.orders.OrderService", "Workflow", "IMPLEMENTS", 0.9, "syntax")
                && HasEdge(edges, "com.acme.orders.OrderService", "Auditable", "IMPLEMENTS", 0.9, "syntax"),
            "class super_interfaces should map to IMPLEMENTS with syntax evidence");
        assert(HasEdge(edges, "com.acme.orders.OrderDto", "Payload", "IMPLEMENTS", 0.9, "syntax"),
            "record super_interfaces should map to IMPLEMENTS");
        assert(HasEdge(edges, "OrderWorkflow.java", "java.time.Instant", "IMPORTS", 0.9, "syntax")
                && HasEdge(edges, "OrderWorkflow.java", "java.util.List", "IMPORTS", 0.9, "syntax")
                && HasEdge(edges, "OrderWorkflow.java", "java.util.Objects.requireNonNull", "IMPORTS", 0.9, "syntax"),
            "Java imports should produce file-level IMPORTS evidence");
        assert(edges.All(edge => edge.Kind is "CONTAINS" or "HAS_METHOD" or "HAS_PROPERTY" or "EXTENDS" or "IMPLEMENTS" or "IMPORTS"),
            "Java extractor should only emit v2 structural edge kinds");

        var sites = parsed.CallSites;
        assert(HasCallSite(sites, "com.acme.orders.OrderService.OrderService", "requireNonNull", null, 1, "call"),
            "constructor call should attribute to the constructor");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.OrderService", "repository", "this", 0, "access", "write"),
            "constructor assignment to this.repository should be a write access");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "Order", null, 2, "new"),
            "object creation should be a new call site with arity");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "save", "repository", 1, "call"),
            "member invocation should carry receiver and caller");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "count", "this", 0, "access", "write")
                && HasCallSite(sites, "com.acme.orders.OrderService.create", "count", "this", 0, "access", "read"),
            "field assignment should produce write and read access sites");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "audit", null, 1, "call"),
            "direct method invocation should use null receiver");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "id", "order", 0, "call"),
            "nested argument invocation should attribute to the enclosing method");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "OrderDto", null, 2, "new"),
            "record construction should be a new call site");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.create", "now", "Instant", 0, "call"),
            "static-style call should carry the type receiver");
        assert(HasCallSite(sites, "com.acme.orders.OrderService.audit", "info", "Logger", 1, "call"),
            "private method call should attribute to its declaring method");
        assert(sites.All(site => site.Line >= 1), "Java call sites should carry 1-based lines");
    }

    private static IEnumerable<ParsedSymbol> FlattenSymbols(IEnumerable<ParsedSymbol> symbols) =>
        symbols.SelectMany(symbol => new[] { symbol }.Concat(FlattenSymbols(symbol.Children)));

    private static bool HasEdge(
        IReadOnlyList<ParsedEdge> edges,
        string from,
        string to,
        string kind,
        double confidence,
        string evidence) =>
        edges.Any(edge => edge.FromQualified == from
            && edge.ToName == to
            && edge.Kind == kind
            && Math.Abs(edge.Confidence - confidence) < 1e-9
            && edge.Evidence == evidence);

    private static bool HasCallSite(
        IReadOnlyList<ParsedCallSite> sites,
        string caller,
        string target,
        string? receiver,
        int arity,
        string kind,
        string? accessMode = null) =>
        sites.Any(site => site.CallerQualified == caller
            && site.TargetName == target
            && site.ReceiverText == receiver
            && site.Arity == arity
            && site.Kind == kind
            && site.AccessMode == accessMode);
}
