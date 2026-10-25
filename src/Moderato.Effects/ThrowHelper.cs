using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Moderato.Effects;

internal static class ThrowHelper
{
    private const string UninitializedEffect =
        "The Effect is default (uninitialized). Create effects with Effect<R>.Succeed, Effect<R>.Fail, Effect<R>.Sync and the other factories.";

    private const string UninitializedResource =
        "The Resource is default (uninitialized). Create resources with Resource.Make.";

    private const string UninitializedResult =
        "The Result is default (uninitialized). Create results with Result.Ok(...) or Result.Err(...).";

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TReturn ThrowUninitializedEffect<TReturn>() => throw new InvalidOperationException(UninitializedEffect);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TReturn ThrowUninitializedResource<TReturn>() => throw new InvalidOperationException(UninitializedResource);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowUninitializedResult() => throw new InvalidOperationException(UninitializedResult);

    public static InvalidOperationException UninitializedResultException() => new(UninitializedResult);
}
