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
          { path: "data", element: <DataPage /> },
          { path: "data/:tableId", element: <TablePage /> },
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
- `/dashboards`, `/forms`, and `/ai` already exist as placeholders so later modules can plug into a stable IA without route churn
- Step 2.2+ will replace the placeholder content with the real Data Explorer and Schema Designer experiences

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

---

## State Management

Zustand is the selected global-state layer. Step 2.1 now includes three concrete shared stores:
- `appStore` for theme mode and shell/sidebar state
- `authStore` for browser session lifecycle and refresh handling
- `schemaStore` for the current schema snapshot cache used by the shell and upcoming modules

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

```bash
# Development (with Vite dev server + HMR)
npm run dev

# Type-check the frontend workspace
npm run typecheck

# Production build
npm run build       # outputs to dist/
npm run preview     # preview production build locally

# Run the frontend test suite
npm run test
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
