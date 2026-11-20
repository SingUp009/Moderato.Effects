import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, HighlightStyle, indentOnInput, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { setDiagnostics, type Diagnostic as LintDiagnostic } from '@codemirror/lint';
import { EditorState, RangeSetBuilder, StateEffect, StateField, type Text } from '@codemirror/state';
import { Decoration, drawSelection, EditorView, highlightActiveLine, keymap, lineNumbers, type DecorationSet } from '@codemirror/view';
import { tags } from '@lezer/highlight';
import type { Diagnostic } from './runner';
import type { LineRange } from './snippets';

const setFocus = StateEffect.define<LineRange[]>();
const focusedLine = Decoration.line({ class: 'cm-playground-focus' });
const dimmedLine = Decoration.line({ class: 'cm-playground-dim' });

function focusDecorations(doc: Text, ranges: LineRange[]): DecorationSet {
    if (ranges.length === 0) return Decoration.none;
    const builder = new RangeSetBuilder<Decoration>();
    for (let n = 1; n <= doc.lines; n++) {
        const focused = ranges.some((range) => n >= range.from && n <= range.to);
        builder.add(doc.line(n).from, doc.line(n).from, focused ? focusedLine : dimmedLine);
    }
    return builder.finish();
}

// Lines the chapter text is talking about; any edit clears them, since the text no longer matches.
const focusField = StateField.define<DecorationSet>({
    create: () => Decoration.none,
    update(decorations, transaction) {
        for (const effect of transaction.effects) {
            if (effect.is(setFocus)) return focusDecorations(transaction.state.doc, effect.value);
        }
        return transaction.docChanged ? Decoration.none : decorations;
    },
    provide: (field) => EditorView.decorations.from(field),
});

const USER_FILE_PATH = 'Program.cs';

// Colors come from playground.css so the editor follows Starlight's light and dark themes.
const highlight = HighlightStyle.define([
    { tag: [tags.keyword, tags.modifier, tags.operatorKeyword], color: 'var(--playground-code-keyword)' },
    { tag: [tags.string, tags.character], color: 'var(--playground-code-string)' },
    { tag: [tags.number, tags.bool, tags.null], color: 'var(--playground-code-number)' },
    { tag: [tags.comment, tags.meta], color: 'var(--playground-code-comment)', fontStyle: 'italic' },
    { tag: [tags.typeName, tags.className], color: 'var(--playground-code-type)' },
]);

const theme = EditorView.theme({
    '&': { height: '100%', color: 'var(--sl-color-white)', backgroundColor: 'var(--playground-code-bg)' },
    '.cm-content': { fontFamily: 'var(--__sl-font-mono)', caretColor: 'var(--sl-color-white)' },
    '.cm-gutters': { backgroundColor: 'var(--playground-code-bg)', color: 'var(--sl-color-gray-3)', border: 'none' },
    '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'var(--playground-code-active-line)' },
    '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': { backgroundColor: 'var(--playground-code-selection)' },
    '&.cm-focused': { outline: 'none' },
});

export interface EditorOptions {
    parent: HTMLElement;
    onChange(code: string): void;
    onRun(): void;
}

/** A C# editor that reports edits and runs on Ctrl/Cmd+Enter. */
export class CodeEditor {
    private readonly view: EditorView;

    constructor({ parent, onChange, onRun }: EditorOptions) {
        this.view = new EditorView({
            parent,
            state: EditorState.create({
                extensions: [
                    lineNumbers(),
                    history(),
                    drawSelection(),
                    indentOnInput(),
                    bracketMatching(),
                    closeBrackets(),
                    highlightActiveLine(),
                    StreamLanguage.define(csharp),
                    syntaxHighlighting(highlight),
                    focusField,
                    theme,
                    EditorState.tabSize.of(4),
                    keymap.of([
                        { key: 'Mod-Enter', run: () => (onRun(), true) },
                        ...closeBracketsKeymap,
                        ...defaultKeymap,
                        ...historyKeymap,
                        indentWithTab,
                    ]),
                    EditorView.updateListener.of((update) => {
                        if (update.docChanged) onChange(update.state.doc.toString());
                    }),
                ],
            }),
        });
    }

    get code(): string {
        return this.view.state.doc.toString();
    }

    set code(code: string) {
        this.view.dispatch({ changes: { from: 0, to: this.view.state.doc.length, insert: code } });
        this.view.dispatch(setDiagnostics(this.view.state, []));
    }

    /** Highlights the given lines, dims the rest and scrolls the first into the middle; no ranges clears it. */
    focus(ranges: LineRange[]): void {
        const doc = this.view.state.doc;
        const valid = ranges.filter((range) => range.from >= 1 && range.to <= doc.lines);
        this.view.dispatch({ effects: setFocus.of(valid) });
        if (valid.length === 0) return;

        // Scroll only the editor; scrollIntoView would also move the page the reader is scrolling.
        const scroller = this.view.scrollDOM;
        const top = this.view.lineBlockAt(doc.line(valid[0].from).from).top;
        const bottom = this.view.lineBlockAt(doc.line(valid[valid.length - 1].to).from).bottom;
        const target = (top + bottom) / 2 - scroller.clientHeight / 2;
        scroller.scrollTo({ top: Math.max(0, target), behavior: 'smooth' });
    }

    showDiagnostics(diagnostics: Diagnostic[]): void {
        const doc = this.view.state.doc;
        const marks = diagnostics
            .filter((d) => d.path === USER_FILE_PATH && d.line <= doc.lines)
            .map((d): LintDiagnostic => {
                const line = doc.line(d.line);
                const from = Math.min(line.from + d.column - 1, line.to);
                const word = this.view.state.wordAt(from);
                return {
                    from,
                    to: word?.to ?? Math.min(from + 1, doc.length),
                    severity: d.severity === 'Error' ? 'error' : 'warning',
                    message: `${d.id}: ${d.message}`,
                };
            });
        this.view.dispatch(setDiagnostics(this.view.state, marks));
    }
}
