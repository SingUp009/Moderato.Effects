using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moderato.Effects;

/// <summary>Lists active scope, fiber and resource lifetimes for debugging.</summary>
public static class EffectTracker
{
    private static ConditionalWeakTable<object, EffectTrackingState>? tracking;
    private static bool enableStackTrace;
    private static long nextId;

    /// <summary>Tracks new lifetimes; disabling clears the current registry.</summary>
    public static bool EnableTracking
    {
        get => Volatile.Read(ref tracking) is not null;
        set
        {
            if (!value)
            {
                Interlocked.Exchange(ref tracking, null);
            }
            else if (Volatile.Read(ref tracking) is null)
            {
                Interlocked.CompareExchange(ref tracking, new(), null);
            }
        }
    }

    /// <summary>Captures stack traces for new tracked lifetimes; disabled by default.</summary>
    public static bool EnableStackTrace
    {
        get => Volatile.Read(ref enableStackTrace);
        set => Volatile.Write(ref enableStackTrace, value);
    }

    /// <summary>Returns active metadata ordered by ID, without retaining the tracked objects.</summary>
    /// <returns>A snapshot; concurrent starts and completions may change the registry.</returns>
    public static EffectTrackingState[] GetSnapshot()
    {
        if (Volatile.Read(ref tracking) is not { } current) return [];
        var states = new List<EffectTrackingState>();
        foreach (var item in current) states.Add(item.Value);
        var snapshot = states.ToArray();
        Array.Sort(snapshot, static (left, right) => left.Id.CompareTo(right.Id));
        return snapshot;
    }

    internal static void Track(object owner, EffectTrackingKind kind)
    {
        if (Volatile.Read(ref tracking) is { } current) TrackCore(current, owner, kind);
    }

    private static void TrackCore(ConditionalWeakTable<object, EffectTrackingState> current, object owner, EffectTrackingKind kind)
    {
        try
        {
            var stackTrace = EnableStackTrace ? new StackTrace(1, true).ToString() : null;
            var state = new EffectTrackingState(Interlocked.Increment(ref nextId), kind,
                owner.GetType().ToString(), DateTimeOffset.UtcNow, stackTrace);
            current.Add(owner, state);
        }
        catch (Exception)
        {
            // Debug tracking must not change execution or prevent cleanup.
        }
    }

    internal static void Untrack(object owner)
    {
        if (Volatile.Read(ref tracking) is { } current) current.Remove(owner);
    }
}

/// <summary>The lifetime observed by the tracker.</summary>
public enum EffectTrackingKind
{
    /// <summary>A Gen block or Resource.Use, including its cleanup.</summary>
    Scope,
    /// <summary>A forked effect that has not completed.</summary>
    Fiber,
    /// <summary>A successful Resource.Make acquisition whose release has not completed.</summary>
    Resource,
}

/// <summary>Immutable debug metadata that holds no tracked object, environment or result.</summary>
public sealed class EffectTrackingState
{
    internal EffectTrackingState(long id, EffectTrackingKind kind, string typeName, DateTimeOffset startedAt, string? stackTrace)
    {
        Id = id;
        Kind = kind;
        TypeName = typeName;
        StartedAt = startedAt;
        StackTrace = stackTrace;
    }

    /// <summary>The process-local tracking identifier.</summary>
    public long Id { get; }
    /// <summary>The lifetime being tracked.</summary>
    public EffectTrackingKind Kind { get; }
    /// <summary>The internal lifetime owner's type name.</summary>
    public string TypeName { get; }
    /// <summary>The UTC time when tracking began.</summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>The captured start stack, or null when stack capture was disabled.</summary>
    public string? StackTrace { get; }
}
