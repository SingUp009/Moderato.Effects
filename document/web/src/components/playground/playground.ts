import { CodeEditor } from './editor';
import { Runner, type Diagnostic, type LoadPhase } from './runner';
import { findSnippet } from './snippets';

const LAYOUT_KEY = 'moderato.tutorial-layout';
const EDITS_KEY_PREFIX = 'moderato.playground.';

/** Wires the editor, output and runner beside one tutorial chapter. */
export function mountPlayground(root: HTMLElement, text: HTMLElement): void {
    const chapter = root.dataset.chapter!;
    const original: string = JSON.parse(root.querySelector('[data-playground-code]')!.textContent!);
    const runButton = root.querySelector<HTMLButtonElement>('[data-playground-run]')!;
    const resetButton = root.querySelector<HTMLButtonElement>('[data-playground-reset]')!;
    const status = root.querySelector<HTMLElement>('[data-playground-status]')!;
    const output = root.querySelector<HTMLElement>('[data-playground-output]')!;

    let running = false;
    let diagnostics: Diagnostic[] = [];
    let runner: Runner | undefined;

    const setStatus = (message: string, isError = false) => {
        status.textContent = message;
        status.classList.toggle('playground-status-error', isError);
    };

    const progress = createProgress(root.querySelector<HTMLElement>('[data-playground-progress]')!, setStatus);

    // The compiler is about 10 MB, so it starts only once someone opens the editor; closing keeps it warm.
    const ensureRunner = () => {
        runner ??= new Runner(root.dataset.runner!, {
            loading: progress.update,
            ready() {
                progress.finish();
                runButton.disabled = false;
                setStatus('Ready');
                runner!.compileSoon(editor.code, 0);
            },
            compiled(result) {
                diagnostics = result.diagnostics;
                editor.showDiagnostics(diagnostics);
                if (!running) setStatus(result.success ? 'Ready' : describe(diagnostics), !result.success);
            },
            output(chunk, isError) {
                const span = document.createElement('span');
                span.textContent = chunk;
                if (isError) span.className = 'playground-output-error';
                output.append(span);
            },
            failed(message) {
                progress.finish();
                running = false;
                runButton.textContent = 'Run';
                runButton.disabled = false;
                setStatus(message, true);
            },
        });
    };

    // Set after the editor exists; the editor reports its first content before that.
    let snippets: ReturnType<typeof followSnippets> | undefined;
    const editor = new CodeEditor({
        parent: root.querySelector<HTMLElement>('[data-playground-editor]')!,
        onChange(code) {
            // Any edit already cleared the editor's highlight; only the marker in the text is left.
            snippets?.forget();
            store(EDITS_KEY_PREFIX + chapter, code === original ? null : code);
            if (runner?.ready) runner.compileSoon(code);
        },
        onRun: () => void run(),
    });
    // Edits survive moving between chapters for the rest of the browser session.
    editor.code = load('session', EDITS_KEY_PREFIX + chapter) ?? original;
    snippets = followSnippets(text, editor, isOpen);

    async function run() {
        if (!runner) return;
        if (running) {
            // A second press stops the program: terminating the worker is the only reliable way.
            progress.reset();
            runner.restart();
            running = false;
            runButton.textContent = 'Run';
            runButton.disabled = true;
            return;
        }
        if (!runner.ready) return;

        running = true;
        runButton.textContent = 'Stop';
        output.replaceChildren();
        output.classList.remove('playground-output-error');
        setStatus('Running…');
        const started = performance.now();
        const result = await runner.run(editor.code);
        if (!running) return;
        running = false;
        runButton.textContent = 'Run';
        if (!result.success) {
            output.textContent = diagnostics
                .filter((d) => d.severity === 'Error')
                .map((d) => `${d.path ? `${d.path}(${d.line},${d.column}): ` : ''}${d.id}: ${d.message}`)
                .join('\n');
            output.classList.add('playground-output-error');
        }
        const elapsed = Math.round(performance.now() - started);
        setStatus(result.success ? `Finished in ${elapsed} ms` : describe(diagnostics), !result.success);
    }

    function isOpen() {
        return document.documentElement.dataset.playground === 'open';
    }

    function setOpen(open: boolean) {
        // Only one side panel shows at a time: the editor, or the site navigation.
        const root = document.documentElement;
        root.dataset.playground = open ? 'open' : 'closed';
        root.dataset.tutorialNav = open ? 'closed' : 'open';
        store(LAYOUT_KEY, open ? 'playground' : 'navigation', 'local');
        if (open) {
            ensureRunner();
            snippets?.refresh();
        } else {
            editor.focus([]);
            snippets?.forget();
        }
    }

    for (const button of document.querySelectorAll('[data-playground-open]')) button.addEventListener('click', () => setOpen(true));
    for (const button of document.querySelectorAll('[data-playground-close]')) button.addEventListener('click', () => setOpen(false));
    for (const button of document.querySelectorAll('[data-playground-toggle]')) {
        // Rendered inside the right sidebar, whose stacking context sits below the site navigation it must cover.
        document.body.append(button);
        button.addEventListener('click', () => setOpen(!isOpen()));
    }
    runButton.addEventListener('click', () => void run());
    resetButton.addEventListener('click', () => {
        editor.code = original;
        if (runner?.ready) runner.compileSoon(editor.code, 0);
    });

    if (isOpen()) ensureRunner();
}

type StorageKind = 'local' | 'session';

// Storage can be unavailable (private windows, blocked site data), and even reading the
// localStorage property can throw; the playground works without it.
function storageOf(kind: StorageKind): Storage {
    return kind === 'local' ? window.localStorage : window.sessionStorage;
}

function load(kind: StorageKind, key: string): string | null {
    try {
        return storageOf(kind).getItem(key);
    } catch {
        return null;
    }
}

function store(key: string, value: string | null, kind: StorageKind = 'session'): void {
    try {
        if (value === null) storageOf(kind).removeItem(key);
        else storageOf(kind).setItem(key, value);
    } catch {
        // Not remembering is acceptable.
    }
}

// The band of the viewport where a snippet counts as the one being read: from 30% to 50% down.
const READING_BAND = '-30% 0px -50% 0px';

/**
 * Highlights in the editor the lines that the C# snippet being read refers to, as the reader
 * scrolls the chapter text or points at a snippet.
 */
function followSnippets(text: HTMLElement, editor: CodeEditor, isOpen: () => boolean) {
    // "Try it" asks the reader to change the code, so from there on nothing is highlighted.
    const blocks = [...text.querySelectorAll<HTMLElement>('.expressive-code, h2#try-it')]
        .filter((block) => block.matches('h2') || block.querySelector('pre[data-language="csharp"]'));
    const inBand = new Set<Element>();
    let active: HTMLElement | undefined;

    const forget = () => {
        active?.classList.remove('playground-snippet-active');
        active = undefined;
    };

    const activate = (block: HTMLElement) => {
        if (block === active || !isOpen()) return;
        forget();
        if (block.matches('h2')) {
            editor.focus([]);
            active = block;
            return;
        }
        const snippet = [...block.querySelectorAll('.ec-line')].map((line) => line.textContent ?? '').join('\n');
        const ranges = findSnippet(editor.code, snippet);
        editor.focus(ranges);
        if (ranges.length === 0) return;
        active = block;
        block.classList.add('playground-snippet-active');
    };

    const refresh = () => {
        const reading = blocks.find((block) => inBand.has(block));
        if (reading) activate(reading);
    };

    const observer = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            if (entry.isIntersecting) inBand.add(entry.target);
            else inBand.delete(entry.target);
        }
        refresh();
    }, { rootMargin: READING_BAND });

    for (const block of blocks) {
        observer.observe(block);
        if (!block.matches('h2')) block.addEventListener('mouseenter', () => activate(block));
    }

    return { forget, refresh };
}

const DOWNLOAD_END = 75;
const START_AT = 80;
const WARMUP_AT = 85;
const WARMUP_END = 97;
const WARMUP_EXPECTED_MS = 2000;

/** Shows how far the compiler is from ready; values only ever move forward. */
function createProgress(container: HTMLElement, setStatus: (text: string) => void) {
    const bar = container.querySelector('progress')!;
    let value = 0;
    let frame = 0;

    const set = (next: number) => {
        value = Math.max(value, next);
        bar.value = value;
    };

    return {
        update(phase: LoadPhase, loaded?: number, total?: number) {
            container.dataset.done = 'false';
            switch (phase) {
                case 'download':
                    // The runtime adds resources to the total as it discovers them, so the ratio can dip.
                    set(total ? (loaded! / total) * DOWNLOAD_END : 0);
                    setStatus(`Downloading the C# compiler… ${Math.round(value)}%`);
                    break;
                case 'start':
                    set(START_AT);
                    setStatus('Starting .NET…');
                    break;
                case 'warmup': {
                    set(WARMUP_AT);
                    setStatus('Warming up the compiler…');
                    // The worker is busy compiling and cannot report, so ease toward the end meanwhile.
                    const started = performance.now();
                    const creep = () => {
                        const elapsed = performance.now() - started;
                        set(WARMUP_AT + (WARMUP_END - WARMUP_AT) * (1 - Math.exp(-elapsed / WARMUP_EXPECTED_MS)));
                        frame = requestAnimationFrame(creep);
                    };
                    frame = requestAnimationFrame(creep);
                    break;
                }
            }
        },
        finish() {
            cancelAnimationFrame(frame);
            set(100);
            container.dataset.done = 'true';
        },
        reset() {
            cancelAnimationFrame(frame);
            value = 0;
            bar.value = 0;
            container.dataset.done = 'false';
            setStatus('Stopped. Restarting the C# compiler…');
        },
    };
}

function describe(diagnostics: Diagnostic[]): string {
    const errors = diagnostics.filter((d) => d.severity === 'Error').length;
    return errors === 1 ? '1 compile error' : `${errors} compile errors`;
}
