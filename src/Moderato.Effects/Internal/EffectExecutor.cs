using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace Moderato.Effects.Internal;

internal static class EffectExecutor
{
    // Ordinary runs stay allocation-free; deeper composition uses explicit frames.
    internal const int DIRECT_DEPTH = 64;

    internal static ValueTask<Outcome<T, E>> Run<T, E, R>(Effect<T, E, R> effect, R environment, RunContext context)
    {
        var root = effect.Enter(environment, context, null);
        EffectFrame? current = root;
        while (current is not null)
        {
            current = current.Step();
            if (current?.Waiting != true) continue;
            CaptureWaiters(current);
            var completion = new Completion<T, E>();
            _ = Await(root, current, completion);
            return completion.Task;
        }

        return root.Result();
    }

    private static async Task Await<T, E>(EffectFrame<T, E> root, EffectFrame current, Completion<T, E> completion)
    {
        List<ExecutionScope>? scopes = null;
        try
        {
            while (true)
            {
                var boundary = new AwaitBoundary(current.WaitAsync(), scopes);
                scopes = null;
                await boundary;
                // Registration unwinds the suspended segment; synchronous completion keeps it active.
                if (!boundary.Registered) scopes = boundary.Scopes;
                EffectFrame? next = current.Parent;
                while (next is not null)
                {
                    next.Resume(ref scopes);
                    next = next.Step();
                    if (next?.Waiting != true) continue;
                    CaptureWaiters(next);
                    break;
                }

                if (next is null)
                {
                    // Completing first lets outer awaiters run before the nested scopes unwind.
                    if (root.Fault is null) completion.Complete(root.Output);
                    else completion.Fail(root.Fault.SourceException);
                    return;
                }

                current = next;
            }
        }
        catch (Exception exception)
        {
            completion.Fail(exception);
        }
        finally
        {
            ExecutionScope.Unwind(scopes);
        }
    }

    private static void CaptureWaiters(EffectFrame current)
    {
        var context = ExecutionContext.Capture();
        for (EffectFrame? frame = current; frame is not null; frame = frame.Parent)
        {
            var captures = frame.CapturesContext;
            // A still-waiting ancestor has already captured the rest of this chain.
            if (frame.IsAsync && (!captures || frame.HasAwaitContext)) break;
            frame.IsAsync = true;
            if (!captures) continue;
            frame.HasAwaitContext = true;
            frame.AwaitContext = context;
        }
    }

    private sealed class Completion<T, E> : IValueTaskSource<Outcome<T, E>>
    {
        private ManualResetValueTaskSourceCore<Outcome<T, E>> core;
        internal ValueTask<Outcome<T, E>> Task => new(this, core.Version);
        internal void Complete(Outcome<T, E> outcome) => core.SetResult(outcome);
        internal void Fail(Exception exception) => core.SetException(exception);
        public Outcome<T, E> GetResult(short token) => core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => core.OnCompleted(continuation, state, token, flags);
    }

    private sealed class AwaitBoundary(ValueTask task, List<ExecutionScope>? scopes)
    {
        internal List<ExecutionScope>? Scopes { get; } = scopes;
        internal bool Registered { get; private set; }
        public Awaiter GetAwaiter() => new(task.ConfigureAwait(false).GetAwaiter(), this);

        internal readonly struct Awaiter(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter, AwaitBoundary boundary)
            : ICriticalNotifyCompletion
        {
            public bool IsCompleted => awaiter.IsCompleted;
            public void GetResult() => awaiter.GetResult();
            public void OnCompleted(Action continuation) => Register(continuation, flowContext: true);
            public void UnsafeOnCompleted(Action continuation) => Register(continuation, flowContext: false);

            private void Register(Action continuation, bool flowContext)
            {
                boundary.Registered = true;
                try
                {
                    if (flowContext) awaiter.OnCompleted(continuation);
                    else awaiter.UnsafeOnCompleted(continuation);
                }
                finally
                {
                    ExecutionScope.Unwind(boundary.Scopes);
                }
            }
        }
    }
}

internal readonly struct ExecutionScope(ExecutionContext context, bool suppressed, SynchronizationContext? synchronizationContext)
{
    internal static ExecutionScope Capture()
    {
        // Unwinding also needs the ambient values of a scope whose flow is suppressed.
        var suppressed = ExecutionContext.IsFlowSuppressed();
        if (suppressed) ExecutionContext.RestoreFlow();
        var context = ExecutionContext.Capture()!;
        if (suppressed) ExecutionContext.SuppressFlow();
        return new(context, suppressed, SynchronizationContext.Current);
    }

    private void Restore()
    {
        ExecutionContext.Restore(context);
        if (suppressed) ExecutionContext.SuppressFlow();
        SynchronizationContext.SetSynchronizationContext(synchronizationContext);
    }

    internal static void Unwind(List<ExecutionScope>? scopes)
    {
        if (scopes is null) return;
        for (var i = scopes.Count - 1; i >= 0; i--) scopes[i].Restore();
        scopes.Clear();
    }
}

internal abstract class EffectFrame(EffectFrame? parent)
{
    internal EffectFrame? Parent { get; } = parent;
    internal bool Waiting { get; set; }
    internal bool IsAsync { get; set; }
    internal bool HasAwaitContext { get; set; }
    internal ExecutionContext? AwaitContext { get; set; }
    internal virtual bool CapturesContext => false;
    internal abstract EffectFrame? Step();
    internal abstract ValueTask WaitAsync();

    internal void Resume(ref List<ExecutionScope>? scopes)
    {
        if (!HasAwaitContext) return;
        // Suppressed flow inherits the completion context instead of restoring an uncaptured scope.
        if (AwaitContext is not null)
        {
            (scopes ??= []).Add(ExecutionScope.Capture());
            ExecutionContext.Restore(AwaitContext);
        }
        HasAwaitContext = false;
        AwaitContext = null;
    }
}

internal abstract class EffectFrame<T, E>(EffectFrame? parent) : EffectFrame(parent)
{
    internal Outcome<T, E> Output;
    internal ExceptionDispatchInfo? Fault;
    private ValueTask<Outcome<T, E>> pending;

    internal ValueTask<Outcome<T, E>> Result()
        => Fault is null ? new(Output) : ValueTask.FromException<Outcome<T, E>>(Fault.SourceException);

    protected EffectFrame? Complete(ValueTask<Outcome<T, E>> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            Output = task.Result;
            return Parent;
        }

        pending = task;
        Waiting = true;
        return this;
    }

    internal override async ValueTask WaitAsync()
    {
        try
        {
            Output = await pending.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fault = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            pending = default;
            Waiting = false;
        }
    }
}

internal sealed class LeafFrame<T, E, R>(Effect<T, E, R> effect, R environment, RunContext context, EffectFrame? parent)
    : EffectFrame<T, E>(parent)
{
    internal override EffectFrame? Step()
    {
        try
        {
            return Complete(effect.Run(environment, context));
        }
        catch (Exception exception)
        {
            Fault = ExceptionDispatchInfo.Capture(exception);
            return Parent;
        }
    }
}

internal abstract class EffectInstruction<T, E, R>
{
    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context)
        => RunDirect(environment, context, EffectExecutor.DIRECT_DEPTH);

    internal abstract ValueTask<Outcome<T, E>> RunDirect(R environment, RunContext context, int depth);
    internal abstract EffectFrame<T, E> Enter(R environment, RunContext context, EffectFrame? parent);
}
