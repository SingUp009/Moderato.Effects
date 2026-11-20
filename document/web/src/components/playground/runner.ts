export interface Diagnostic {
    id: string;
    severity: 'Error' | 'Warning';
    message: string;
    path: string;
    line: number;
    column: number;
}

export interface CompileResult {
    success: boolean;
    diagnostics: Diagnostic[];
}

export interface RunResult {
    success: boolean;
    hit: boolean;
    compile: number;
    execute: number;
}

export type LoadPhase = 'download' | 'start' | 'warmup';

export interface RunnerEvents {
    loading(phase: LoadPhase, loaded?: number, total?: number): void;
    ready(): void;
    compiled(result: CompileResult): void;
    output(text: string, isError: boolean): void;
    failed(message: string): void;
}

const IDLE_COMPILE_MS = 300;

/** Owns the Web Worker that compiles and runs C#, restarting it when a run has to be stopped. */
export class Runner {
    private worker!: Worker;
    private nextId = 1;
    private idle: ReturnType<typeof setTimeout> | undefined;
    private pendingRun: { id: number; resolve(result: RunResult): void } | undefined;
    private isReady = false;

    constructor(
        private readonly url: string,
        private readonly events: RunnerEvents,
    ) {
        this.start();
    }

    get ready(): boolean {
        return this.isReady;
    }

    /** Compiles in the background once typing pauses, so a later run only has to execute. */
    compileSoon(code: string, delay = IDLE_COMPILE_MS): void {
        clearTimeout(this.idle);
        this.idle = setTimeout(() => this.post('compile', code), delay);
    }

    run(code: string): Promise<RunResult> {
        clearTimeout(this.idle);
        return new Promise((resolve) => {
            this.pendingRun = { id: this.post('run', code), resolve };
        });
    }

    /** Terminates the worker, which is the only way to stop user code that never yields. */
    restart(): void {
        this.worker.terminate();
        this.pendingRun = undefined;
        this.start();
    }

    private post(type: 'compile' | 'run', code: string): number {
        const id = this.nextId++;
        this.worker.postMessage({ type, id, code });
        return id;
    }

    private start(): void {
        this.isReady = false;
        this.worker = new Worker(this.url, { type: 'module' });
        this.worker.onerror = (event) => this.events.failed(event.message || 'The C# runtime failed to load.');
        this.worker.onmessage = ({ data }) => {
            switch (data.type) {
                case 'progress':
                    this.events.loading(data.phase, data.loaded, data.total);
                    break;
                case 'ready':
                    this.isReady = true;
                    if (data.timings.warmupSuccess) this.events.ready();
                    else this.events.failed('The C# compiler failed its warm-up compilation.');
                    break;
                case 'compiled':
                    this.events.compiled(data.result);
                    break;
                case 'output':
                    this.events.output(data.text, data.isError);
                    break;
                case 'done':
                    if (this.pendingRun?.id === data.id) {
                        this.pendingRun.resolve(data);
                        this.pendingRun = undefined;
                    }
                    break;
                case 'error':
                    this.events.failed(data.message);
                    break;
            }
        };
    }
}
