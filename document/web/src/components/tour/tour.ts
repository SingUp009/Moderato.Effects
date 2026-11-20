import { CodeEditor } from './editor';
import { Runner, type Diagnostic, type LoadPhase } from './runner';

interface Chapter {
    id: string;
    title: string;
    code: string;
}

/** Wires the chapter text, editor, output and runner of one tour page together. */
export function mountTour(root: HTMLElement): void {
    const chapters: Chapter[] = JSON.parse(root.querySelector('[data-tour-chapters]')!.textContent!);
    const runButton = root.querySelector<HTMLButtonElement>('[data-tour-run]')!;
    const resetButton = root.querySelector<HTMLButtonElement>('[data-tour-reset]')!;
    const status = root.querySelector<HTMLElement>('[data-tour-status]')!;
    const output = root.querySelector<HTMLElement>('[data-tour-output]')!;
    const position = root.querySelector<HTMLElement>('[data-tour-position]')!;
    const prev = root.querySelector<HTMLAnchorElement>('.tour-prev')!;
    const next = root.querySelector<HTMLAnchorElement>('.tour-next')!;

    // Edits survive moving between chapters, until the page is left or Reset is pressed.
    const edits = new Map<string, string>();
    let current = chapters.findIndex((c) => !root.querySelector(`[data-tour-chapter="${c.id}"]`)!.hidden);
    let running = false;
    let diagnostics: Diagnostic[] = [];

    const setStatus = (text: string, isError = false) => {
        status.textContent = text;
        status.classList.toggle('tour-status-error', isError);
    };

    const progress = createProgress(root.querySelector<HTMLElement>('[data-tour-progress]')!, setStatus);

    const runner = new Runner(root.dataset.runner!, {
        loading: progress.update,
        ready() {
            progress.finish();
            runButton.disabled = false;
            setStatus('Ready');
            runner.compileSoon(editor.code, 0);
        },
        compiled(result) {
            diagnostics = result.diagnostics;
            editor.showDiagnostics(diagnostics);
            if (!running) setStatus(result.success ? 'Ready' : describe(diagnostics), !result.success);
        },
        output(text, isError) {
            const span = document.createElement('span');
            span.textContent = text;
            if (isError) span.className = 'tour-output-error';
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

    const editor = new CodeEditor({
        parent: root.querySelector<HTMLElement>('[data-tour-editor]')!,
        onChange(code) {
            edits.set(chapters[current].id, code);
            if (runner.ready) runner.compileSoon(code);
        },
        onRun: () => void run(),
    });
    editor.code = chapters[current].code;

    async function run() {
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
        output.classList.remove('tour-output-error');
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
            output.classList.add('tour-output-error');
        }
        const elapsed = Math.round(performance.now() - started);
        setStatus(result.success ? `Finished in ${elapsed} ms` : describe(diagnostics), !result.success);
    }

    function show(index: number, push: boolean) {
        if (index < 0 || index >= chapters.length || index === current) return;
        root.querySelector<HTMLElement>(`[data-tour-chapter="${chapters[current].id}"]`)!.hidden = true;
        root.querySelector<HTMLElement>(`[data-tour-chapter="${chapters[index].id}"]`)!.hidden = false;
        current = index;
        position.textContent = String(index + 1);
        link(prev, index - 1);
        link(next, index + 1);
        output.replaceChildren();
        output.classList.remove('tour-output-error');
        editor.code = edits.get(chapters[index].id) ?? chapters[index].code;
        if (runner.ready) runner.compileSoon(editor.code, 0);
        const heading = document.getElementById('_top');
        const previousTitle = heading?.textContent ?? '';
        const title = `Tour: ${chapters[index].title}`;
        if (heading) heading.textContent = title;
        document.title = document.title.replace(previousTitle, title);
        if (push) history.pushState({ chapter: index }, '', `/tour/${chapters[index].id}/`);
    }

    function link(anchor: HTMLAnchorElement, index: number) {
        const exists = index >= 0 && index < chapters.length;
        if (exists) anchor.href = `/tour/${chapters[index].id}/`;
        else anchor.removeAttribute('href');
        anchor.setAttribute('aria-disabled', String(!exists));
    }

    // Moving between chapters in place keeps the warmed-up compiler; a full page load would restart it.
    const navigate = (event: MouseEvent, offset: number) => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        show(current + offset, true);
        root.scrollIntoView({ block: 'start' });
    };
    prev.addEventListener('click', (event) => navigate(event, -1));
    next.addEventListener('click', (event) => navigate(event, 1));
    window.addEventListener('popstate', () => {
        const id = location.pathname.replace(/^\/tour\/?|\/$/g, '');
        show(Math.max(0, chapters.findIndex((c) => c.id === id)), false);
    });

    runButton.addEventListener('click', () => void run());
    resetButton.addEventListener('click', () => {
        edits.delete(chapters[current].id);
        editor.code = chapters[current].code;
        if (runner.ready) runner.compileSoon(editor.code, 0);
    });
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
