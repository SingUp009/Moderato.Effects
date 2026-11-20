/** Messages between the playground page and the runner worker. */

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
    timings: { parse: number; generate: number; emit: number; total: number };
}

/** How running a compiled program ended, as WasmRunner.ExecuteAsync reports it. */
export type RunOutcome =
    | { success: true; elapsed: number }
    | { success: false; error: 'programFailed' | 'noEntryPoint' | 'nothingCompiled' };

export type LoadPhase = 'download' | 'start' | 'warmup';

export type WorkerRequest =
    | { type: 'compile'; id: number; code: string }
    | { type: 'run'; id: number; code: string }
    | { type: 'diagnostics'; id: number };

export interface RunDone {
    type: 'done';
    id: number;
    hit: boolean;
    compiled: boolean;
    compile: number;
    outcome?: RunOutcome;
}

export interface MemoryDiagnostics {
    type: 'diagnostics';
    id: number;
    assemblies: number;
    managedBytes: number;
    wasmBytes: number;
}

export type WorkerReply =
    | { type: 'progress'; phase: LoadPhase; loaded?: number; total?: number }
    | { type: 'ready'; timings: { boot: number; initialize: number; warmup: number } }
    | { type: 'compiled'; id: number; result: CompileResult }
    | { type: 'output'; text: string; isError: boolean }
    | RunDone
    | MemoryDiagnostics
    | { type: 'error'; message: string };
