using Microsoft.CodeAnalysis;

namespace Moderato.Effects.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "Moderato.Effects";

    public static readonly DiagnosticDescriptor NeedsPartialUnion = new(
        "MEFF001", "Needs a partial union", "[{0}] needs a partial union; declare '{1}' as 'partial union'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericNotSupported = new(
        "MEFF002", "Generic types are not supported", "[{0}] does not support generic types yet; '{1}' is generic",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AmbiguousCase = new(
        "MEFF003", "Case reachable in more than one way",
        "'{1}' is reachable from '{0}' in more than one way ({2}), so no conversion is generated for it; handle it through one of those cases",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedNotSupported = new(
        "MEFF004", "Nested types are not supported", "[{0}] does not support nested types yet; move '{1}' out of '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NeedsPartialType = new(
        "MEFF101", "Needs a partial type", "[Environment] needs a partial type; declare '{0}' as partial",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NeedsPrimaryConstructor = new(
        "MEFF102", "Needs a primary constructor", "[Environment] provides the parameters of a primary constructor, and '{0}' has none",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateService = new(
        "MEFF103", "Service provided twice", "'{0}' is provided by both '{1}' and '{2}'; wrap one in its own type or use [Expose<T>] to provide another type",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExposeNotConvertible = new(
        "MEFF104", "Parameter cannot be provided as that type", "Parameter '{0}' of type '{1}' cannot be provided as '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RestOfNotUnion = new(
        "MEFF201", "Not a union", "[RestOf<{0}>] needs '{0}' to be a union",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RestOfWrongCases = new(
        "MEFF202", "Must leave out exactly one case",
        "'{0}' must hold every case of '{1}' but one; it leaves out {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
