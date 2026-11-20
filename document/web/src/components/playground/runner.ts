import { Deferred, Effect, Fiber } from 'effect';
import type { CompileResult, LoadPhase, MemoryDiagnostics, RunDone, WorkerReply, WorkerRequest } from './protocol';

export type { CompileResult, Diagnostic, LoadPhase, RunDone, RunOutcome } from './protocol';

export interface RunnerEvents {
    loading(phase: LoadPhase, loaded?: number, total?: number): void;
    ready(): void;
    compiled(result: CompileResult): void;
    output(text: string, isError: boolean): void;
    failed(message: string): void;
}

const IDLE_COMPILE = '300 millis';

type Reply<T extends WorkerReply['type']> = Extract<WorkerReply, { type: T }>;

/** Owns the Web Worker that compiles and runs C#, and restarts it when a run is interrupted. */
export class Runner {
    private worker!: Worker;
    private nextId = 1;
    private isReady = false;
    private idle: Fiber.Fiber<void> | undefined;
    // Replies a caller is waiting for, by request id; a new worker abandons them all.
    private readonly waiting = new Map<number, Deferred.Deferred<WorkerReply>>();

    constructor(private readonly events: RunnerEvents) {
        this.start();
    }

    get ready(): boolean {
        return this.isReady;
    }

    /** Compiles in the background once typing pauses, so a later run only has to execute. */
    compileSoon(code: string, delay: Parameters<typeof Effect.sleep>[0] = IDLE_COMPILE): void {
        this.cancelIdle();
        this.idle = Effect.runFork(
            Effect.sleep(delay).pipe(Effect.andThen(Effect.sync(() => this.send({ type: 'compile', id: this.nextId++, code })))),
        );
    }

    /**
     * Compiles if needed and runs `code`. Interrupting the effect stops the program the only reliable
     * way, by replacing the worker, since user code that never yields cannot be stopped from inside.
     */
    run(code: string): Effect.Effect<RunDone> {
        return Effect.suspend(() => {
            this.cancelIdle();
            return this.request<'done'>({ type: 'run', id: this.nextId++, code });
        }).pipe(Effect.onInterrupt(() => Effect.sync(() => this.restart())));
    }

    /** Memory figures after a full collection, for measuring growth over many runs. */
    diagnostics(): Effect.Effect<MemoryDiagnostics> {
        return Effect.suspend(() => this.request<'diagnostics'>({ type: 'diagnostics', id: this.nextId++ }));
    }

    /** Compiles `code` now and waits for the result. */
    compile(code: string): Effect.Effect<Reply<'compiled'>> {
        return Effect.suspend(() => this.request<'compiled'>({ type: 'compile', id: this.nextId++, code }));
    }

    private request<T extends 'done' | 'diagnostics' | 'compiled'>(request: WorkerRequest): Effect.Effect<Reply<T>> {
        return Effect.gen({ self: this }, function* () {
            const reply = yield* Deferred.make<WorkerReply>();
            this.waiting.set(request.id, reply);
            this.send(request);
            return (yield* Deferred.await(reply)) as Reply<T>;
        }).pipe(Effect.ensuring(Effect.sync(() => this.waiting.delete(request.id))));
    }

    private cancelIdle(): void {
        if (this.idle) Effect.runFork(Fiber.interrupt(this.idle));
        this.idle = undefined;
    }

    private send(request: WorkerRequest): void {
        this.worker.postMessage(request);
    }

    private restart(): void {
        this.worker.terminate();
        // Nothing will answer the old worker's requests, so their callers stop waiting.
        for (const waiting of this.waiting.values()) Effect.runSync(Deferred.interrupt(waiting));
        this.waiting.clear();
        this.start();
    }

    private start(): void {
        this.isReady = false;
        this.worker = new Worker(new URL('./runner.worker.ts', import.meta.url), { type: 'module' });
        this.worker.onerror = (event) => this.events.failed(event.message || 'The C# runtime failed to load.');
        this.worker.onmessage = ({ data }: MessageEvent<WorkerReply>) => {
            switch (data.type) {
                case 'progress':
                    this.events.loading(data.phase, data.loaded, data.total);
                    break;
                case 'ready':
                    this.isReady = true;
                    this.events.ready();
                    break;
                case 'output':
                    this.events.output(data.text, data.isError);
                    break;
                case 'error':
                    this.events.failed(data.message);
                    break;
                case 'compiled':
                    this.events.compiled(data.result);
                    this.reply(data.id, data);
                    break;
                case 'done':
                case 'diagnostics':
                    this.reply(data.id, data);
                    break;
            }
        };
    }

    private reply(id: number, data: WorkerReply): void {
        const waiting = this.waiting.get(id);
        if (waiting) Effect.runSync(Deferred.succeed(waiting, data));
    }
}
