# Frontend

> Vite + React + TypeScript SPA — structure, routing, state, and build pipeline.
> Tech decision rationale: [`wiki/plan/tech-decisions.md`](../../wiki/plan/tech-decisions.md).

Current implementation status:
- Phase 2 Step 2.1 workspace scaffold exists under `code/src/frontend/`
- the shared SPA shell, grouped navigation, and breadcrumb system are now in place
- browser auth/session handling is wired to the real backend auth contract
- the shared API client and schema snapshot store are live
- the first reusable UI primitive layer now exists for buttons, fields, panels, alerts, empty states, spinners, and confirm dialogs
- light/dark/system theme switching is available through the shared app store and shell controls
- Phase 2 Step 2.2 now ships a real Data Explorer read path at `/data` and `/data/:tableId`
- Phase 2 Steps 2.3 and 2.4 add saved views (table and kanban, shareable per table), inline cell editing with optimistic rollback, row add/duplicate/delete with undo, bulk selection, a full-screen record editor, and CSV/JSON export
- Phase 2 Step 2.6 now ships the schema workbench at `/model`, including JSON and Visual modes backed by the same Model JSON draft
- every authenticated role can browse the Data Explorer (the schema snapshot is readable by all roles); writes need `editor`/`admin`/`api`, and `/model` editing stays admin-only
- Phase 2 Step 2.7 adds the `/setup` wizard and the packaged frontend image; Step 2.8 rolls the visual language out to the whole UI (see "Shared UI Primitives and Visual Language")
- the workspace is checked in CI on every pull request: `pnpm run typecheck`, `pnpm run lint`, `pnpm run test`, `pnpm run build`

---

## Project Structure

```
src/frontend/
├── src/
│   ├── main.tsx          ← Entry point, React root
│   ├── App.tsx           ← Root route host
│   ├── router.tsx        ← Shared browser route tree
│   ├── routes/           ← Page-level route placeholders
│   ├── components/       ← Shared UI components and layout pieces
│   ├── features/         ← Feature modules like Data Explorer and Schema Workbench
│   ├── stores/           ← Zustand state stores
│   ├── api/              ← API client functions
│   ├── hooks/            ← Custom React hooks
│   ├── test/             ← Frontend test files
│   └── types/            ← Shared TypeScript types
├── index.html
├── package.json
├── vite.config.ts
├── tsconfig.json
├── tsconfig.app.json
└── tsconfig.node.json
```

Module layout inside the two feature areas:

- `features/schema-workbench/` — `SchemaWorkbenchPage.tsx` is the page component only (state, effects, toolbar, layout). The visual designer lives in `visual/` (`erdRouting.ts` for node/edge geometry and orthogonal routing, `VisualSchemaMode.tsx` for the canvas, `VisualSchemaInspector.tsx`, `VisualRelationInspector.tsx`, `VisualRelationCreator.tsx`, `VisualPreviewDiagnostics.tsx`). JSON-mode pieces sit beside the page: `MiniErd.tsx`, `previewPresentation.tsx`, `workbenchDiagnostics.ts`, `draftJson.ts`, `JsonCodeEditor.tsx`. Pure draft mutations stay in `visualSchemaDraft.ts`.
- `stores/explorerStore.ts` — a thin composition of Zustand slices from `stores/explorer/` (`querySlice`, `editingSlice`, `rowOpsSlice`, `viewSlice`, `recordEditorSlice`, plus `types.ts`, `helpers.ts`, `autoSave.ts`). Import `useExplorerStore` and its types from `stores/explorerStore` as before; the slice files are an implementation detail.

---

## Routing

React Router (`createBrowserRouter`) is used. The current scaffold defines the shared route tree in `router.tsx`.

```tsx
const appRoutes = [
  { path: "/login", element: <LoginPage /> },
  {
    path: "/",
    element: <App />,
    children: [
      {
        element: <ShellLayout />,
        children: [
          { index: true, element: <HomePage /> },
          { path: "data", element: <DataExplorerPage /> },
          { path: "data/:tableId", element: <TableDetailPage /> },
          { path: "data/new/csv", element: <NewTableFromCsvPage /> },
          { path: "data/:tableId/import", element: <ImportPage /> },
          { path: "model", element: <ModelPage /> },
          { path: "dashboards", element: <DashboardsPage /> },
          { path: "forms", element: <FormsPage /> },
          { path: "ai", element: <AiPage /> },
          { path: "settings", element: <SettingsPage /> }
        ]
      }
    ]
  }
];
```

Current route behavior:
- `/login` is outside the main shell so public auth can evolve cleanly in `TASK-064`
- the main app routes render inside a shared shell with sidebar navigation and breadcrumbs
- `/data` now renders the schema-driven Data Explorer landing with live table discovery and summary chips
- `/data/:tableId` now renders the live explorer detail route with the read-only grid, Airtable-style foldout controls, pagination, row selection, column visibility controls, and relation browsing
- `/dashboards`, `/forms`, and `/ai` already exist as placeholders so later modules can plug into a stable IA without route churn
- `/model` renders the admin schema workbench with JSON and Visual modes sharing one draft, preview/apply safety, import/export, validation diagnostics, and live schema refresh
- `/model` Visual mode uses a feature-local SVG ERD canvas and inspector; no third-party canvas library was introduced for Step 2.6

---

## API Calls

All frontend API calls should use a relative base URL (`/api/v1/`). The current scaffold's `api/client.ts` already follows that rule.

```ts
// api/client.ts
export const apiClient = {
  get<T>(path: string, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "GET" });
  },
  post<T>(path: string, json?: unknown, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "POST", json });
  }
};
```

Current behavior:
- access tokens are injected automatically from `authStore`
- auth routes use `anonymous` + `skipAuthRetry` to avoid recursive refresh attempts
- normal protected calls can do a one-shot `401 -> /auth/refresh -> retry`
- backend `{ error: { code, message, details } }` envelopes are surfaced as `ApiClientError`
- schema workbench calls live in `api/schema.ts` and keep preview (`POST /admin/schema/preview`) separate from apply (`PUT /admin/schema`)

---

## State Management

Zustand is the selected global-state layer. Step 2.1 now includes three concrete shared stores:
- `appStore` for theme mode and shell/sidebar state
- `authStore` for browser session lifecycle and refresh handling
- `schemaStore` for the current schema snapshot cache used by the shell and upcoming modules
- `explorerStore` for per-table Data Explorer view state, including query state, row loading, selection state, column visibility, reference/related context, and relation drilldowns

```ts
// stores/appStore.ts
export const useAppStore = create<AppStoreState>((set) => ({
  themeMode: "system",
  sidebarOpen: true,
  setThemeMode: (themeMode) => set({ themeMode }),
  setSidebarOpen: (sidebarOpen) => set({ sidebarOpen })
}));
```

Local component state (`useState`, `useReducer`) is used for UI-only state that doesn't need to be shared.

---

## Data Explorer

Phase 2 Step 2.2 is now live as the first real end-user browser surface on top of the Phase 1 API.

Current shipped behavior:
- schema-driven table discovery at `/data`
- live row browsing at `/data/:tableId`
- Airtable-style foldout controls for search, multi-sort, typed filters, column visibility, and selection operations
- server-backed search, multi-sort, typed filters, and pagination
- schema-aware read renderers for booleans, temporal values, JSON, enums, and references
- row selection with click, drag, shift-range, select-all, and context-menu actions
- per-table column visibility with a dedicated `Columns` foldout and header-menu `Hide column`
- reference navigation into linked records
- selection-driven related-record drilldowns that open the backend relation endpoint for exactly one selected parent row using resolved relation metadata from the admin schema snapshot

Since Steps 2.3–2.8 (v0.2.0):
- inline editing with one typography contract for grid cells and record fields (`components/editors/`, popovers portalled via `FloatingPanel`), row create/duplicate/delete, bulk selection, CSV/JSON export, saved views and kanban boards
- `/data` lists tables as one card with a row per table (canvas colour, name, row and column counts); N:M junction tables follow under "Link tables"
- the Columns menu shows and hides columns and rearranges them (drag the grip, or arrow keys on it); the order is the view's `columnOrder`
- one toolbar popover at a time (Search, Sort, Filter, Columns, Export and the kanban configuration close each other and on outside clicks); an active search is a dot on its button plus the "Search: …" chip
- the record editor shows 1:N relations as lists and N:M relations as link lists built for its side column: name (record label) with the link's own fields, open and remove actions, "Link" with a search picker, rows that open to edit the link's fields
- record labels: `getDisplayColumnForTable` / `getSecondaryColumnForTable` (`components/editors/referenceMetadata.ts`) read a table's `recordLabel` and fall back to the first text column; `useReferenceLabels` resolves labels per FK column (one failed lookup does not drop the others) and carries secondary values (`lookupReferenceSecondary`)

Not in scope yet: virtual scrolling.

---

## Schema Workbench

Phase 2 Step 2.6 ships the schema designer under `src/features/schema-workbench/`.

Current structure:
- `SchemaWorkbenchPage.tsx` owns the `/model` route, mode switch, shared draft state, preview/apply flow, Visual inspector, and ERD canvas rendering
- `SchemaWorkbenchPage.css` contains the workbench, editor, inspector, canvas, relation-line, diagnostics, and review-modal styles
- `visualSchemaDraft.ts` contains immutable helpers for table, column, relation, layout, and object-path draft mutations
- JSON helpers such as `draftJson.ts`, `jsonPathLocation.ts`, and `jsonTextPosition.ts` remain the raw-draft support layer from Step 2.5

Design and dependency notes:
- The ERD canvas is rendered with React and inline SVG paths, not a new diagramming or canvas dependency.
- Visual and JSON modes must continue to edit one canonical Model JSON draft. Do not add a visual-only save path.
- Backend preview/apply remains the safety boundary. Frontend previews should not guess migration impact independently.
- Table `layout` metadata is allowed in Model JSON and is inert for migrations. Layout-only apply should not imply a schema version bump.
- Visual relation editing currently exposes guided `1:N`, self-referential `1:N`, visible `N:M`, relation selection, and relation renaming. Explicit N:M junction tables may use custom names as long as the relation's `junctionTable` points at the visible table and its reference columns match the relation endpoints. Advanced relation properties are still deferred.
- Layout since Step 2.8: a header (title, Visual/JSON switch, draft status, Import/Export/Refresh, Preview, Apply), the canvas as the stage (`visual/VisualSchemaMode.tsx`: fit on open, wheel pan, pinch/⌘ zoom around the pointer, dot grid that moves with the canvas, drops onto another table move to the nearest free spot), and a side panel that opens only for a selection (inspector) or on request (Preview). The mode lives in `appStore.schemaMode`; the canvas view survives remounts.
- Safety: `useBlocker` + `beforeunload` guard an unapplied draft; a destructive preview turns Apply red; table/column deletes confirm in the app dialog.
- `recordLabel` is table metadata like `layout`; `visualSchemaDraft.updateColumn` / `removeColumn` keep it pointing at existing columns.

---

## Shared UI Primitives and Visual Language

Step 2.8 rolled one visual language across the app (designed on a canvas during Step 2.7, first shipped in the setup wizard). The rules, all expressed as tokens in `src/styles.css`:

- **Ground and one accent.** Dark ground `--bg #0b0b0c` (light `#f5f6f2`); `--accent` blue is reserved for the current position (sidebar dot, active tab underline) and the one main action of a screen (`Button variant="accent"`). Everything else is tone-on-tone.
- **Hairlines, not boxes.** `--line` (8 % of the ink), `--line-2` (14 %), `--hover` (6 %) derive from `--text` and follow the theme; surfaces sit at most one step above the ground; shadows only on overlays. Radii: `--r-xl` 14 px for cards, `--r-pill` for actions.
- **Type as structure.** `--font-display` (Space Grotesk, bundled, loaded once in `main.tsx`) for page titles, counts and numerals; Public Sans for prose; JetBrains Mono for labels, badges and data. `.display` is the utility for display type.
- **Light as a material.** `components/common/Atmosphere` provides `Sky` (drifting pools + grain, `--pool-*`/`--grain-*` tokens) and `Glyph` (an outlined gradient glyph) for the public pages; `OutlinedNumeral` draws the stat-card numerals; the shell carries a confined pool behind the top-right. All of it is `aria-hidden`, pointer-events off, and still under `prefers-reduced-motion`.
- **Contrast.** `--text-3`/`--text-4` are tuned to at least 4.5:1 on their ground in both themes so mono labels stay readable.

Primitives under `src/components/ui/` and `src/components/common/`:

- `Button` — pills. `default` outlined, `primary` ink fill, `accent` blue fill, `ghost`, `danger` (outlined); sizes `sm`, `lg`, `icon`
- `TextField` / `SelectField` — hairline boxed fields, or `underline` for editorial forms (login, record editor, inspectors)
- `Panel`, `.card` — hairline surfaces with display-face titles
- `.badge` (with `.badge__dot`), `.chip` (enum value with colour dot), `.chip--ref` (reference)
- `Alert`, `EmptyState`, `Spinner`, `Dialog` / `ConfirmDialog` (16 px radius, display title, hairline footer)
- `ThemeSwitch` — segmented Dark / Light / System control bound to the app store; used by the shell top bar, setup and login

Feature stylesheets (`DataExplorer.css`, `SchemaWorkbenchPage.css`) end with a "Step 2.8" override section that restyles their chrome on top of these tokens without touching behaviour-bearing structure (fold-out seams, grid internals, the id column).

---

## Build Pipeline

Vite builds to `dist/`. The `bento/frontend` Docker container serves this with nginx.

nginx is configured with `try_files $uri /index.html` so React Router handles all routes client-side.

The workspace uses **pnpm** (pinned via `packageManager` in `package.json`). CI runs typecheck, lint, and tests for every pull request that touches `code/**`.

```bash
# Install dependencies
pnpm install

# Development (with Vite dev server + HMR)
pnpm run dev

# Type-check the frontend workspace
pnpm run typecheck

# Lint (ESLint flat config in eslint.config.js)
pnpm run lint

# Production build
pnpm run build       # outputs to dist/
pnpm run preview     # preview production build locally

# Run the frontend test suite
pnpm run test
```

During development, `vite.config.ts` proxies `/api/v1/*` to `http://localhost:5000` so you can run the backend separately.

```ts
// vite.config.ts
export default defineConfig({
  server: {
    proxy: {
      "/api/v1": "http://127.0.0.1:5000",
      "/ws": { target: "ws://127.0.0.1:5000", ws: true },
    },
  },
});
```

---

## Conventions

- **One component per file.** File name matches the component name.
- **Co-locate styles.** Use CSS Modules (`Component.module.css`) in the same folder as the component.
- **No default exports for stores or utilities** — named exports only. Default exports are fine for components and pages.
- **Types in `types/`** for shared types. Local types stay in the file that uses them.
- **Relative API base URLs only.** The frontend should not hardcode a production backend host.
- **Step 2.1 is a real scaffold, not throwaway code.** Placeholder pages are acceptable right now, but the workspace structure should survive into later Phase 2 work.
- **Public and shell routes stay distinct.** `/login` is intentionally outside the shell so auth UX can evolve independently from authenticated navigation.
