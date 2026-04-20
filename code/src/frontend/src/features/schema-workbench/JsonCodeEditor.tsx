import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useLayoutEffect,
  useMemo,
  useRef,
} from "react";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { json } from "@codemirror/lang-json";
import { defaultHighlightStyle, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { EditorState } from "@codemirror/state";
import { EditorView, keymap } from "@codemirror/view";
import { indexFromLocation, type JsonTextLocation } from "./jsonTextPosition";

type JsonCodeEditorProps = {
  activeLine?: number | null;
  diagnostics?: JsonEditorDiagnostic[];
  focusLocation?: JsonTextLocation | null;
  value: string;
  onChange: (value: string) => void;
};

export type JsonEditorDiagnostic = {
  line: number;
  severity: "error" | "warning";
};

export type JsonCodeEditorHandle = {
  focusLocation: (location: JsonTextLocation) => void;
};

const editorExtensions = [
  history(),
  json(),
  EditorState.tabSize.of(2),
  indentUnit.of("  "),
  syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
  keymap.of([
    indentWithTab,
    ...defaultKeymap,
    ...historyKeymap,
  ]),
  EditorView.contentAttributes.of({
    "aria-label": "Model JSON draft",
    "aria-multiline": "true",
    role: "textbox",
  }),
];

export const JsonCodeEditor = forwardRef<JsonCodeEditorHandle, JsonCodeEditorProps>(function JsonCodeEditor(
  {
    activeLine = null,
    diagnostics = [],
    focusLocation = null,
    value,
    onChange,
  },
  ref,
) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const gutterContentRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const suppressChangeRef = useRef(false);
  const onChangeRef = useRef(onChange);

  const lineCount = useMemo(() => value.split("\n").length, [value]);
  const diagnosticsByLine = useMemo(() => {
    const map = new Map<number, { count: number; severity: "error" | "warning" }>();

    for (const diagnostic of diagnostics) {
      if (!Number.isFinite(diagnostic.line) || diagnostic.line < 1) {
        continue;
      }

      const previous = map.get(diagnostic.line);
      map.set(diagnostic.line, {
        count: (previous?.count ?? 0) + 1,
        severity:
          previous?.severity === "error" || diagnostic.severity === "error"
            ? "error"
            : "warning",
      });
    }

    return map;
  }, [diagnostics]);

  function syncScroll() {
    if (!viewRef.current || !gutterContentRef.current) {
      return;
    }

    gutterContentRef.current.style.transform = `translateY(${-viewRef.current.scrollDOM.scrollTop}px)`;
  }

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  useEffect(() => {
    if (!hostRef.current) {
      return;
    }

    const view = new EditorView({
      parent: hostRef.current,
      state: EditorState.create({
        doc: value,
        extensions: [
          ...editorExtensions,
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !suppressChangeRef.current) {
              onChangeRef.current(update.state.doc.toString());
            }

            if (update.docChanged || update.viewportChanged || update.selectionSet) {
              syncScroll();
            }
          }),
        ],
      }),
    });

    viewRef.current = view;
    const scrollTarget = view.scrollDOM;
    const handleScroll = () => syncScroll();
    scrollTarget.addEventListener("scroll", handleScroll, { passive: true });
    syncScroll();

    return () => {
      scrollTarget.removeEventListener("scroll", handleScroll);
      view.destroy();
      viewRef.current = null;
    };
  }, []);

  useLayoutEffect(() => {
    const view = viewRef.current;
    if (!view) {
      return;
    }

    const currentValue = view.state.doc.toString();
    if (currentValue === value) {
      syncScroll();
      return;
    }

    suppressChangeRef.current = true;
    view.dispatch({
      changes: {
        from: 0,
        to: view.state.doc.length,
        insert: value,
      },
    });
    suppressChangeRef.current = false;
    syncScroll();
  }, [value]);

  useLayoutEffect(() => {
    if (!focusLocation || !viewRef.current) {
      return;
    }

    focusViewLocation(viewRef.current, focusLocation);
    syncScroll();
  }, [focusLocation, value]);

  useImperativeHandle(ref, () => ({
    focusLocation(location) {
      if (!viewRef.current) {
        return;
      }

      focusViewLocation(viewRef.current, location);
      syncScroll();
    },
  }), [value]);

  return (
    <div className="schema-json-editor" data-testid="model-json-editor">
      <div
        className="schema-json-editor__gutter mono"
        aria-hidden="true"
      >
        <div ref={gutterContentRef} className="schema-json-editor__gutter-content">
          {Array.from({ length: lineCount }, (_, index) => (
            <div
              key={index + 1}
              className={[
                "schema-json-editor__gutter-line",
                activeLine === index + 1 ? "schema-json-editor__gutter-line--active" : "",
                diagnosticsByLine.get(index + 1)?.severity === "error"
                  ? "schema-json-editor__gutter-line--error"
                  : "",
                diagnosticsByLine.get(index + 1)?.severity === "warning"
                  ? "schema-json-editor__gutter-line--warning"
                  : "",
              ].filter(Boolean).join(" ")}
              data-line={index + 1}
            >
              <span className="schema-json-editor__gutter-number">{index + 1}</span>
              {diagnosticsByLine.get(index + 1) ? (
                <span className="schema-json-editor__gutter-marker">
                  {diagnosticsByLine.get(index + 1)?.count}
                </span>
              ) : null}
            </div>
          ))}
        </div>
      </div>

      <div ref={hostRef} className="schema-json-editor__layers mono" />
    </div>
  );
});

function focusViewLocation(view: EditorView, location: JsonTextLocation) {
  const index = indexFromLocation(view.state.doc.toString(), location);
  view.dispatch({
    selection: { anchor: index, head: index },
    scrollIntoView: true,
  });
  view.focus();
}
