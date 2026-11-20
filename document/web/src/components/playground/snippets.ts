/** Lines of a program, 1-based and inclusive. */
export interface LineRange {
    from: number;
    to: number;
}

// The tutorial shows members of a class; the playground declares the same code as top-level local functions.
const MODIFIERS = /^(?:(?:public|private|protected|internal|static)\s+)+/;

function normalize(line: string): string {
    return line.replace(/\s+/g, ' ').trim().replace(MODIFIERS, '');
}

/**
 * Finds the lines of `code` that a tutorial snippet shows, ignoring indentation, blank lines and
 * access modifiers. Prefers one contiguous block; otherwise matches each snippet line in order to a
 * program line that ends with it, as when the program assigns a call the snippet shows bare.
 */
export function findSnippet(code: string, snippet: string): LineRange[] {
    const lines = code.split('\n').map(normalize);
    const wanted = snippet.split('\n').map(normalize).filter((line) => line !== '');
    if (wanted.length === 0) return [];
    return contiguous(lines, wanted) ?? inOrder(lines, wanted) ?? [];
}

function contiguous(lines: string[], wanted: string[]): LineRange[] | undefined {
    for (let start = 0; start < lines.length; start++) {
        if (lines[start] !== wanted[0]) continue;
        let line = start;
        let matched = 0;
        let last = start;
        while (line < lines.length && matched < wanted.length) {
            if (lines[line] === '') {
                line++;
                continue;
            }
            if (lines[line] !== wanted[matched]) break;
            last = line;
            line++;
            matched++;
        }
        if (matched === wanted.length) return [{ from: start + 1, to: last + 1 }];
    }
    return undefined;
}

function inOrder(lines: string[], wanted: string[]): LineRange[] | undefined {
    const ranges: LineRange[] = [];
    let line = 0;
    for (const target of wanted) {
        while (line < lines.length && !(lines[line] === target || lines[line].endsWith(` ${target}`))) line++;
        if (line === lines.length) return undefined;
        ranges.push({ from: line + 1, to: line + 1 });
        line++;
    }
    return ranges;
}
