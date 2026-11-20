import { Effect } from 'effect';
import type { Runner } from './runner';

/** One memory sample: runtime figures after a full collection. */
interface Sample {
    step: number;
    assemblies: number;
    managedMB: number;
    wasmMB: number;
}

const MB = 1024 * 1024;

const whenReady = (runner: Runner) =>
    Effect.gen(function* () {
        while (!runner.ready) yield* Effect.sleep('100 millis');
    });

const sample = (runner: Runner, step: number) =>
    Effect.map(runner.diagnostics(), (d): Sample => ({
        step,
        assemblies: d.assemblies,
        managedMB: +(d.managedBytes / MB).toFixed(2),
        wasmMB: +(d.wasmBytes / MB).toFixed(2),
    }));

/**
 * Repeats `step` with fresh code and samples memory every `every` steps, to see whether anything
 * accumulates in the worker. Each user-code run keeps its assembly loaded, about 400 KB apiece.
 */
const measure = (runner: Runner, steps: number, every: number, step: (code: string) => Effect.Effect<unknown>, code: () => string) =>
    Effect.gen(function* () {
        yield* whenReady(runner);
        const samples = [yield* sample(runner, 0)];
        for (let i = 1; i <= steps; i++) {
            yield* step(`// step ${i} ${Date.now()}\n${code()}`);
            if (i % every === 0) {
                samples.push(yield* sample(runner, i));
                console.table(samples);
            }
        }
        return samples;
    });

/** Exposes measurement helpers on `window.playgroundBench` during development. */
export function installBench(runner: () => Runner, code: () => string): void {
    (window as unknown as { playgroundBench: unknown }).playgroundBench = {
        /** Compiles and runs fresh code `runs` times. */
        runs: (runs = 1000, every = 100) =>
            Effect.runPromise(measure(runner(), runs, every, (source) => runner().run(source), code)),
        /** Only compiles, to tell compilation's share of the growth from loading's. */
        compiles: (compiles = 300, every = 100) =>
            Effect.runPromise(measure(runner(), compiles, every, (source) => runner().compile(source), code)),
    };
}
