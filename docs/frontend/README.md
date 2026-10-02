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
- the workspace is checked in CI on every pull request: `pnpm run typecheck`, `pnpm run lint`, `pnpm run test`

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

Current non-goals for this slice:
- inline editing
- row creation/deletion
- bulk operations
- export
- virtual scrolling
- advanced column management beyond hide/show

Those move into Step 2.3 and later Part 8 work.

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

---

## Shared UI Primitives

Step 2.1 now has a first reusable primitive layer under `src/components/ui/`.

Current primitives:
- `Button`
- `TextField`
- `SelectField`
- `Panel`
- `Alert`
- `EmptyState`
- `Spinner`
- `Dialog` / `ConfirmDialog`

These are intentionally lightweight and token-driven. They are the baseline layer for the Data Explorer and Schema Designer, not a finished long-term design system.

---

## Build Pipeline

Vite builds to `dist/`. The `repere/frontend` Docker container serves this with nginx.

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
