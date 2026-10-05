import { useEffect, useLayoutEffect, useRef } from "react";
import { closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { javascript } from "@codemirror/lang-javascript";
import { bracketMatching, HighlightStyle, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { Compartment, EditorState } from "@codemirror/state";
import { EditorView, keymap, lineNumbers } from "@codemirror/view";
import { tags } from "@lezer/highlight";

const codeHighlightStyle = HighlightStyle.define([
  { tag: [tags.keyword, tags.controlKeyword, tags.definitionKeyword, tags.moduleKeyword], class: "code-tok-keyword" },
  { tag: [tags.string, tags.special(tags.string), tags.regexp], class: "code-tok-string" },
  { tag: [tags.number, tags.bool, tags.null], class: "code-tok-number" },
  { tag: [tags.lineComment, tags.blockComment], class: "code-tok-comment" },
  { tag: [tags.function(tags.variableName), tags.function(tags.propertyName)], class: "code-tok-function" },
  { tag: [tags.propertyName], class: "code-tok-property" },
]);

type CodeEditorProps = {
  value: string;
  onChange: (value: string) => void;
  readOnly?: boolean;
  ariaLabel?: string;
};

/** A JavaScript editor for a custom function's body: highlighting, brackets, line numbers. */
export function CodeEditor({ value, onChange, readOnly = false, ariaLabel = "Function code" }: CodeEditorProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange);
  const suppressRef = useRef(false);
  const readOnlyCompartment = useRef(new Compartment()).current;
  const initialRef = useRef({ value, readOnly, ariaLabel });

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
          javascript(),
          lineNumbers(),
          bracketMatching(),
          closeBrackets(),
          EditorState.tabSize.of(2),
          indentUnit.of("  "),
          syntaxHighlighting(codeHighlightStyle),
          keymap.of([indentWithTab, ...closeBracketsKeymap, ...defaultKeymap, ...historyKeymap]),
          readOnlyCompartment.of([EditorState.readOnly.of(initial.readOnly), EditorView.editable.of(!initial.readOnly)]),
          EditorView.contentAttributes.of({
            "aria-label": initial.ariaLabel,
            "aria-multiline": "true",
            role: "textbox",
            spellcheck: "false",
          }),
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !suppressRef.current) onChangeRef.current(update.state.doc.toString());
          }),
        ],
      }),
    });
    viewRef.current = view;
    return () => {
      view.destroy();
      viewRef.current = null;
    };
  }, [readOnlyCompartment]);

  useEffect(() => {
    viewRef.current?.dispatch({
      effects: readOnlyCompartment.reconfigure([EditorState.readOnly.of(readOnly), EditorView.editable.of(!readOnly)]),
    });
  }, [readOnly, readOnlyCompartment]);

  useLayoutEffect(() => {
    const view = viewRef.current;
    if (!view || view.state.doc.toString() === value) return;
    suppressRef.current = true;
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value } });
    suppressRef.current = false;
  }, [value]);

  return <div ref={hostRef} className="code-editor mono" data-testid="code-editor" />;
}
