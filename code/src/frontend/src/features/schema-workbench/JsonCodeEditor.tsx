import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useLayoutEffect,
  useMemo,
  useRef,
} from "react";
import {
  defaultKeymap,
  history,
  historyKeymap,
  indentWithTab,
} from "@codemirror/commands";
import { json } from "@codemirror/lang-json";
import {
  defaultHighlightStyle,
  indentUnit,
  syntaxHighlighting,
} from "@codemirror/language";
import { Compartment, EditorState, RangeSet } from "@codemirror/state";
import {
  EditorView,
  GutterMarker,
  gutterLineClass,
  keymap,
  lineNumberMarkers,
  lineNumbers,
} from "@codemirror/view";
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

type DiagnosticLineState = {
  count: number;
  severity: "error" | "warning";
};

const lineNumberMarkersCompartment = new Compartment();
const gutterLineClassCompartment = new Compartment();

const editorExtensions = [
  history(),
  json(),
  lineNumbers(),
  EditorState.tabSize.of(2),
  indentUnit.of("  "),
  syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
  keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
  EditorView.contentAttributes.of({
    "aria-label": "Schema JSON editor",
    "aria-multiline": "true",
    role: "textbox",
  }),
];

class DiagnosticLineNumberMarker extends GutterMarker {
  constructor(
    readonly lineNumber: number,
    readonly diagnostic: DiagnosticLineState,
    readonly isActive: boolean,
  ) {
    super();
  }

  eq(other: DiagnosticLineNumberMarker) {
    return (
      this.lineNumber === other.lineNumber &&
      this.diagnostic.count === other.diagnostic.count &&
      this.diagnostic.severity === other.diagnostic.severity &&
      this.isActive === other.isActive
    );
  }

  toDOM() {
    const line = document.createElement("span");
    line.className = [
      "schema-json-editor__gutter-line",
      this.isActive ? "schema-json-editor__gutter-line--active" : "",
      this.diagnostic.severity === "error"
        ? "schema-json-editor__gutter-line--error"
        : "schema-json-editor__gutter-line--warning",
    ]
      .filter(Boolean)
      .join(" ");
    line.dataset.line = String(this.lineNumber);

    const number = document.createElement("span");
    number.className = "schema-json-editor__gutter-number";
    number.textContent = String(this.lineNumber);

    const marker = document.createElement("span");
    marker.className = "schema-json-editor__gutter-marker";
    marker.textContent = "x";

    line.append(number, marker);
    return line;
  }
}

class GutterLineClassMarker extends GutterMarker {
  readonly elementClass: string;

  constructor(className: string) {
    super();
    this.elementClass = className;
  }

  eq(other: GutterLineClassMarker) {
    return this.elementClass === other.elementClass;
  }
}

export const JsonCodeEditor = forwardRef<
  JsonCodeEditorHandle,
  JsonCodeEditorProps
>(function JsonCodeEditor(
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
  const viewRef = useRef<EditorView | null>(null);
  const suppressChangeRef = useRef(false);
  const onChangeRef = useRef(onChange);

  const diagnosticsByLine = useMemo(() => {
    const map = new Map<number, DiagnosticLineState>();

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
          lineNumberMarkersCompartment.of([]),
          gutterLineClassCompartment.of([]),
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !suppressChangeRef.current) {
              onChangeRef.current(update.state.doc.toString());
            }
          }),
        ],
      }),
    });

    viewRef.current = view;
    syncLineNumberDecorations(view, diagnosticsByLine, activeLine);

    return () => {
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
  }, [value]);

  useLayoutEffect(() => {
    if (!focusLocation || !viewRef.current) {
      return;
    }

    focusViewLocation(viewRef.current, focusLocation);
  }, [focusLocation, value]);

  useLayoutEffect(() => {
    if (!viewRef.current) {
      return;
    }

    syncLineNumberDecorations(viewRef.current, diagnosticsByLine, activeLine);
  }, [activeLine, diagnosticsByLine, value]);

  useImperativeHandle(
    ref,
    () => ({
      focusLocation(location) {
        if (!viewRef.current) {
          return;
        }

        focusViewLocation(viewRef.current, location);
      },
    }),
    [],
  );

  return (
    <div className="schema-json-editor" data-testid="model-json-editor">
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

function syncLineNumberDecorations(
  view: EditorView,
  diagnosticsByLine: ReadonlyMap<number, DiagnosticLineState>,
  activeLine: number | null,
) {
  const markerRanges = [];
  const classRanges = [];

  for (const [lineNumber, diagnostic] of diagnosticsByLine.entries()) {
    if (lineNumber < 1 || lineNumber > view.state.doc.lines) {
      continue;
    }

    const line = view.state.doc.line(lineNumber);
    markerRanges.push(
      new DiagnosticLineNumberMarker(
        lineNumber,
        diagnostic,
        activeLine === lineNumber,
      ).range(line.from),
    );
  }

  if (activeLine && activeLine >= 1 && activeLine <= view.state.doc.lines) {
    classRanges.push(
      new GutterLineClassMarker(
        "schema-json-editor__gutter-line--active",
      ).range(view.state.doc.line(activeLine).from),
    );
  }

  view.dispatch({
    effects: [
      lineNumberMarkersCompartment.reconfigure(
        lineNumberMarkers.of(RangeSet.of(markerRanges, true)),
      ),
      gutterLineClassCompartment.reconfigure(
        gutterLineClass.of(RangeSet.of(classRanges, true)),
      ),
    ],
  });
}
