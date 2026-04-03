# Components

> Shared UI components for the Repère frontend.
> Component source lives in `src/frontend/src/components/`.

---

## Conventions

- Each component lives in its own folder: `components/DataGrid/`
- Folder contains: `index.tsx`, `DataGrid.module.css`, optionally `DataGrid.test.tsx`
- Export the component as a named export from `index.tsx`
- Props interface is always named `<ComponentName>Props` and exported

```tsx
// components/DataGrid/index.tsx
export interface DataGridProps {
  tableId: string;
  editable?: boolean;
  onRowClick?: (row: Row) => void;
}

export function DataGrid({ tableId, editable = false, onRowClick }: DataGridProps) {
  // ...
}
```

---

## Component Index

> Components will be documented here as they are implemented.

| Component | Description | Status |
|-----------|-------------|--------|
| `DataGrid` | Tabular data view with sorting, filtering, inline edit | Planned — Phase 2 |
| `Chart` | ECharts wrapper with Repère chart config format | Planned — Phase 3 |
| `FormulaEditor` | Formula input with syntax highlighting and autocomplete | Planned — Phase 2 |
| `SchemaDesigner` | Drag-and-drop column type editor | Planned — Phase 2 |
| `DashboardGrid` | Drag-and-drop widget layout | Planned — Phase 3 |
| `EmbedFrame` | Embeddable dashboard shell (no nav) | Planned — Phase 3 |

---

## Third-Party Libraries in Use

| Library | Used for | Docs |
|---------|---------|------|
| TanStack Table | Headless data grid logic | [tanstack.com/table](https://tanstack.com/table) |
| ECharts | Charts and visualisations | [echarts.apache.org](https://echarts.apache.org) |
| Zustand | State management | [zustand-demo.pmnd.rs](https://zustand-demo.pmnd.rs) |
| React Router | Client-side routing | [reactrouter.com](https://reactrouter.com) |

When wrapping a third-party library, document the wrapper's API here — not the underlying library's full API. Link to the library docs for the rest.
