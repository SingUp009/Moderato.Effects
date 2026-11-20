/// <reference lib="webworker" />
import { Cause, Data, Effect, Queue } from 'effect';
import type { CompileResult, RunOutcome, WorkerReply, WorkerRequest } from './protocol';

declare const self: DedicatedWorkerGlobalScope;

// Served from public/runner/ as it is, outside the bundle, so it is loaded by URL at run time.
const DOTNET_URL = new URL('/runner/_framework/dotnet.js', self.location.origin).href;

const WARMUP_CODE = `using Moderato.Effects;
using Moderato.Results;

Result<string, WarmupError> result = await Effect.Gen(async (Scope<WarmupError, EmptyEnvironment> scope) =>
{
    await Task.Yield();
    return "warm";
}).RunAsync();
Console.WriteLine(result is Ok<string>(var value) ? value : "failed");

public sealed record Missing(string Name);
public sealed record Invalid(string Reason);

[Error]
public partial union WarmupError(Missing, Invalid);
`;

/** The members of WasmRunner that the runner assembly exports to JavaScript. */
interface DotnetRunner {
    Initialize(): number;
    Compile(code: string): string;
    ExecuteAsync(): Promise<string>;
    Diagnostics(): string;
}

interface DotnetRuntime {
    setModuleImports(name: string, imports: Record<string, unknown>): void;
    getAssemblyExports(assemblyName: string): Promise<{ Moderato: { Effects: { WasmRunner: { WasmRunner: DotnetRunner } } } }>;
    getConfig(): { mainAssemblyName?: string };
    localHeapViewU8(): Uint8Array;
}

interface DotnetHostBuilder {
    withModuleConfig(config: {
        onConfigLoaded?: (config: { resources?: Record<string, unknown> }) => void;
        onDownloadResourceProgress?: (loaded: number, total: number) => void;
    }): DotnetHostBuilder;
    create(): Promise<DotnetRuntime>;
}

class RuntimeLoadFailed extends Data.TaggedError('RuntimeLoadFailed')<{ readonly cause: unknown }> {}
class WarmupFailed extends Data.TaggedError('WarmupFailed')<{ readonly result: CompileResult }> {}

interface Loaded {
    readonly runner: DotnetRunner;
    readonly heapBytes: () => number;
}

let muted = false;
let latestCompileId = 0;
let compiled: { code: string; result: CompileResult } | undefined;

const post = (reply: WorkerReply) => Effect.sync(() => self.postMessage(reply));

const load = (step: string, promise: () => Promise<unknown>) =>
    Effect.tryPromise({ try: promise, catch: (cause) => new RuntimeLoadFailed({ cause: `${step}: ${String(cause)}` }) });

/** Downloads and starts .NET, then compiles once so the reader's first run does not pay for it. */
const start = Effect.gen(function* () {
    const started = performance.now();
    yield* post({ type: 'progress', phase: 'download', loaded: 0, total: 1 });

    const { dotnet } = (yield* load('import', () => import(/* @vite-ignore */ DOTNET_URL))) as { dotnet: DotnetHostBuilder };
    // The runtime grows its own total as it discovers resources, so count them up front from the boot config.
    let expected = 0;
    const runtime = (yield* load('create', () =>
        dotnet
            .withModuleConfig({
                onConfigLoaded: (config) => {
                    for (const [kind, entries] of Object.entries(config.resources ?? {})) {
                        if (Array.isArray(entries) && !kind.startsWith('jsModule')) expected += entries.length;
                    }
                },
                onDownloadResourceProgress: (loaded, total) =>
                    self.postMessage({ type: 'progress', phase: 'download', loaded, total: Math.max(total, expected) } satisfies WorkerReply),
            })
            .create(),
    )) as DotnetRuntime;

    yield* post({ type: 'progress', phase: 'start' });
    runtime.setModuleImports('runner', {
        postOutput: (text: string, isError: boolean) => {
            if (!muted) self.postMessage({ type: 'output', text, isError } satisfies WorkerReply);
        },
    });
    const exports = (yield* load('exports', () => runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName!))) as Awaited<
        ReturnType<DotnetRuntime['getAssemblyExports']>
    >;
    const runner = exports.Moderato.Effects.WasmRunner.WasmRunner;
    const booted = performance.now();
    const initialize = runner.Initialize();

    yield* post({ type: 'progress', phase: 'warmup' });
    const warmup = yield* quietly(
        Effect.gen(function* () {
            const result = compile(runner, WARMUP_CODE);
            if (result.success) yield* execute(runner);
            return result;
        }),
    );
    if (!warmup.success) return yield* new WarmupFailed({ result: warmup });

    yield* post({ type: 'ready', timings: { boot: booted - started, initialize, warmup: warmup.timings.total } });
    return { runner, heapBytes: () => runtime.localHeapViewU8().byteLength } satisfies Loaded;
});

/** Runs an effect with the program's console output kept off the page, and always turns it back on. */
const quietly = <A, E>(effect: Effect.Effect<A, E>) =>
    Effect.acquireUseRelease(
        Effect.sync(() => {
            muted = true;
        }),
        () => effect,
        () =>
            Effect.sync(() => {
                muted = false;
            }),
    );

function compile(runner: DotnetRunner, code: string): CompileResult {
    const result = JSON.parse(runner.Compile(code)) as CompileResult;
    compiled = { code, result };
    return result;
}

const execute = (runner: DotnetRunner) => Effect.promise(async () => JSON.parse(await runner.ExecuteAsync()) as RunOutcome);

const handle = ({ runner, heapBytes }: Loaded, request: WorkerRequest) => {
    switch (request.type) {
        case 'compile':
            // Only the newest edit matters; older queued compiles would be thrown away anyway.
            if (request.id !== latestCompileId || compiled?.code === request.code) return Effect.void;
            return post({ type: 'compiled', id: request.id, result: compile(runner, request.code) });
        case 'run':
            return Effect.gen(function* () {
                const hit = compiled?.code === request.code;
                const result = hit ? compiled!.result : compile(runner, request.code);
                if (!hit) yield* post({ type: 'compiled', id: request.id, result });
                const outcome = result.success ? yield* execute(runner) : undefined;
                yield* post({ type: 'done', id: request.id, hit, compiled: result.success, compile: hit ? 0 : result.timings.total, outcome });
            });
        case 'diagnostics':
            return post({ type: 'diagnostics', id: request.id, ...JSON.parse(runner.Diagnostics()), wasmBytes: heapBytes() });
    }
};

const reportFailure = (cause: Cause.Cause<unknown>) => post({ type: 'error', message: Cause.pretty(cause) });

const main = Effect.gen(function* () {
    // Requests that arrive while .NET is still starting wait here, then run one at a time in order.
    const inbox = yield* Queue.unbounded<WorkerRequest>();
    self.onmessage = ({ data }: MessageEvent<WorkerRequest>) => {
        if (data.type === 'compile') latestCompileId = data.id;
        Queue.offerUnsafe(inbox, data);
    };

    const loaded = yield* start;
    yield* Effect.forever(
        Queue.take(inbox).pipe(
            Effect.flatMap((request) => handle(loaded, request)),
            // One failed request must not stop the ones after it.
            Effect.catchCause(reportFailure),
        ),
    );
}).pipe(
    Effect.catchTags({
        RuntimeLoadFailed: (error) => post({ type: 'error', message: `The C# runtime failed to load (${String(error.cause)}).` }),
        WarmupFailed: () => post({ type: 'error', message: 'The C# compiler failed its warm-up compilation.' }),
    }),
    Effect.catchCause(reportFailure),
);

Effect.runFork(main);
