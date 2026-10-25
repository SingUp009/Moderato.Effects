using Microsoft.CodeAnalysis;

namespace Moderato.Effects.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "Moderato.Effects";

    public static readonly DiagnosticDescriptor NeedsPartialUnion = new(
        "MEFF001", "Needs a partial union", "'{1}' must be a partial union to use [{0}]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericNotSupported = new(
        "MEFF002", "Generic types are not supported", "[{0}] does not support generic type '{1}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AmbiguousCase = new(
        "MEFF003", "Case reachable in more than one way",
        "'{1}' is reachable from '{0}' through {2}; no conversion is generated for it",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedNotSupported = new(
        "MEFF004", "Nested types are not supported", "[{0}] does not support nested type '{1}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NeedsPartialType = new(
        "MEFF101", "Needs a partial type", "'{0}' must be partial to use [Environment]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NeedsPrimaryConstructor = new(
        "MEFF102", "Needs a primary constructor", "'{0}' must have a primary constructor to use [Environment]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateService = new(
        "MEFF103", "Service provided twice", "'{0}' is provided by both '{1}' and '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExposeNotConvertible = new(
        "MEFF104", "Parameter cannot be provided as that type", "Parameter '{0}' of type '{1}' cannot be exposed as '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RestOfNotUnion = new(
        "MEFF201", "Not a union", "'{0}' is not a union",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RestOfWrongCases = new(
        "MEFF202", "Must leave out exactly one case",
        "'{0}' must hold all cases of '{1}' but one; it leaves out {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CatchAllInGen = new(
        "MEFF301", "Catch clause catches ScopeFailure", "Catch clause swallows ScopeFailure; filter it out with 'when (ex is not ScopeFailure)'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StepInCleanup = new(
        "MEFF302", "Scope step in cleanup code", "'{0}' does not run after an Err; use 'scope.Defer' for cleanup",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScopeInNestedFunction = new(
        "MEFF303", "Scope used in a nested function", "Scope '{0}' is used in a nested function; use it only in its own Gen block",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScopeEscapes = new(
        "MEFF304", "Scope escapes its Gen block", "Scope '{0}' escapes its Gen block",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ResourceInLoop = new(
        "MEFF305", "Scope resource in a loop", "'{0}' in a loop holds each resource until the Gen block ends; use a nested Gen block or 'Use'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BlockingWait = new(
        "MEFF306", "Blocking wait in a Gen block", "'{0}' blocks the thread; use 'await'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SystemClock = new(
        "MEFF307", "System clock used in an effect", "'{0}' reads the system clock; use {1}",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotRestOf = new(
        "MEFF308", "Not the rest of the error union", "'{0}' is not declared [RestOf<{1}>]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RestLeavesOutAnother = new(
        "MEFF309", "Rest leaves out another case", "'{0}' leaves out '{1}', not '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
