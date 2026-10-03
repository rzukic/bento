import { useEffect, useLayoutEffect, useRef } from "react";
import { autocompletion, completionKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { syntaxHighlighting } from "@codemirror/language";
import {
  Compartment,
  EditorState,
  StateEffect,
  StateField,
  type Range,
} from "@codemirror/state";
import {
  Decoration,
  EditorView,
  keymap,
  placeholder as placeholderExtension,
  type DecorationSet,
} from "@codemirror/view";
import type { FormulaError, FormulaFunction } from "../../api/formulas";
import {
  formulaCompletions,
  formulaHighlightStyle,
  formulaLanguage,
  type FormulaColumn,
  type FormulaLink,
} from "./formulaLanguage";

type FormulaEditorProps = {
  value: string;
  onChange: (value: string) => void;
  columns: FormulaColumn[];
  functions: FormulaFunction[];
  /** Links to other tables, for completions after "link.". */
  links?: FormulaLink[];
  /** Ranges to underline; offsets refer to the current value. */
  errors?: FormulaError[];
  ariaLabel?: string;
  placeholder?: string;
};

const setErrors = StateEffect.define<FormulaError[]>();
const noLinks: FormulaLink[] = [];
const errorMark = Decoration.mark({ class: "formula-editor__error" });

/** Error underlines; they follow edits until the next check replaces them. */
const errorField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, transaction) {
    let next = decorations.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (!effect.is(setErrors)) continue;
      const length = transaction.state.doc.length;
      const ranges: Range<Decoration>[] = [];
      for (const error of effect.value) {
        let from = Math.max(0, Math.min(error.start, length));
        let to = Math.max(from, Math.min(error.end, length));
        // "Ends too early" points past the end: mark the last character.
        if (to === from) {
          if (length === 0) continue;
          from = Math.min(from, length - 1);
          to = from + 1;
        }
        ranges.push(errorMark.range(from, to));
      }
      next = Decoration.set(ranges, true);
    }
    return next;
  },
  provide: (field) => EditorView.decorations.from(field),
});

/**
 * A CodeMirror editor for one formula: highlighting, autocomplete for
 * columns and functions, and error underlines from the validate endpoint.
 */
export function FormulaEditor({
  value,
  onChange,
  columns,
  functions,
  links = noLinks,
  errors = [],
  ariaLabel = "Formula",
  placeholder = "= amount * -1",
}: FormulaEditorProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange);
  const suppressChangeRef = useRef(false);
  const completionCompartment = useRef(new Compartment()).current;
  // The view is created once from the first props; later changes are applied below.
  const initialRef = useRef({ value, columns, functions, links, ariaLabel, placeholder });

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  useEffect(() => {
    if (!hostRef.current) return;

    const initial = initialRef.current;
    const view = new EditorView({
      parent: hostRef.current,
      state: EditorState.create({
        doc: initial.value,
        extensions: [
          history(),
          formulaLanguage,
          syntaxHighlighting(formulaHighlightStyle),
          completionCompartment.of(autocompletion({ override: [formulaCompletions(initial.columns, initial.functions, initial.links)] })),
          keymap.of([...completionKeymap, ...defaultKeymap, ...historyKeymap]),
          errorField,
          EditorView.lineWrapping,
          placeholderExtension(initial.placeholder),
          EditorView.contentAttributes.of({
            "aria-label": initial.ariaLabel,
            "aria-multiline": "true",
            role: "textbox",
            spellcheck: "false",
          }),
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !suppressChangeRef.current) {
              onChangeRef.current(update.state.doc.toString());
            }
          }),
        ],
      }),
    });
    viewRef.current = view;

    return () => {
      view.destroy();
      viewRef.current = null;
    };
  }, [completionCompartment]);

  useLayoutEffect(() => {
    const view = viewRef.current;
    if (!view || view.state.doc.toString() === value) return;
    suppressChangeRef.current = true;
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value } });
    suppressChangeRef.current = false;
  }, [value]);

  useEffect(() => {
    viewRef.current?.dispatch({
      effects: completionCompartment.reconfigure(
        autocompletion({ override: [formulaCompletions(columns, functions, links)] }),
      ),
    });
  }, [columns, completionCompartment, functions, links]);

  useEffect(() => {
    viewRef.current?.dispatch({ effects: setErrors.of(errors) });
  }, [errors]);

  return <div ref={hostRef} className="formula-editor mono" data-testid="formula-editor" />;
}
