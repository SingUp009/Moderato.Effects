import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, HighlightStyle, indentOnInput, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { setDiagnostics, type Diagnostic as LintDiagnostic } from '@codemirror/lint';
import { EditorState } from '@codemirror/state';
import { drawSelection, EditorView, highlightActiveLine, keymap, lineNumbers } from '@codemirror/view';
import { tags } from '@lezer/highlight';
import type { Diagnostic } from './runner';

const USER_FILE_PATH = 'Program.cs';

// Colors come from tour.css so the editor follows Starlight's light and dark themes.
const highlight = HighlightStyle.define([
    { tag: [tags.keyword, tags.modifier, tags.operatorKeyword], color: 'var(--tour-code-keyword)' },
    { tag: [tags.string, tags.character], color: 'var(--tour-code-string)' },
    { tag: [tags.number, tags.bool, tags.null], color: 'var(--tour-code-number)' },
    { tag: [tags.comment, tags.meta], color: 'var(--tour-code-comment)', fontStyle: 'italic' },
    { tag: [tags.typeName, tags.className], color: 'var(--tour-code-type)' },
]);

const theme = EditorView.theme({
    '&': { height: '100%', color: 'var(--sl-color-white)', backgroundColor: 'var(--tour-code-bg)' },
    '.cm-content': { fontFamily: 'var(--__sl-font-mono)', caretColor: 'var(--sl-color-white)' },
    '.cm-gutters': { backgroundColor: 'var(--tour-code-bg)', color: 'var(--sl-color-gray-3)', border: 'none' },
    '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'var(--tour-code-active-line)' },
    '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': { backgroundColor: 'var(--tour-code-selection)' },
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
