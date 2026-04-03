# Frontend

> Vite + React + TypeScript SPA — structure, routing, state, and build pipeline.
> Tech decision rationale: [`wiki/plan/tech-decisions.md`](../../wiki/plan/tech-decisions.md).

---

## Project Structure

```
src/frontend/
├── public/               ← Static assets (favicon, etc.)
├── src/
│   ├── main.tsx          ← Entry point, React root
│   ├── App.tsx           ← Root component, router setup
│   ├── routes/           ← Page-level components (one per route)
│   │   ├── Dashboard.tsx
│   │   ├── Table.tsx
│   │   └── ...
│   ├── components/       ← Shared UI components
│   │   ├── DataGrid/
│   │   ├── Chart/
│   │   └── ...
│   ├── stores/           ← Zustand state stores
│   │   ├── authStore.ts
│   │   ├── tableStore.ts
│   │   └── ...
│   ├── api/              ← API client functions
│   │   ├── client.ts     ← Base fetch wrapper
│   │   ├── tables.ts
│   │   └── ...
│   ├── hooks/            ← Custom React hooks
│   └── types/            ← Shared TypeScript types
├── index.html
├── vite.config.ts
└── tsconfig.json
```

---

## Routing

React Router v6 (`createBrowserRouter`). Routes are defined in `App.tsx`.

```tsx
const router = createBrowserRouter([
  { path: "/", element: <Home /> },
  { path: "/tables/:tableId", element: <Table /> },
  { path: "/dashboards/:dashboardId", element: <Dashboard /> },
  { path: "/embed/dashboards/:dashboardId", element: <EmbedDashboard /> },
  { path: "/login", element: <Login /> },
]);
```

### Embed mode

Routes under `/embed/*` render in embed mode — no sidebar, no navigation chrome. Detected by the route path; no separate build or config needed.

---

## API Calls

All API calls use a relative base URL (`/api/v1/`). Never hardcode `localhost` or a port. Caddy handles routing to the backend.

```ts
// api/client.ts
const BASE = "/api/v1";

export async function apiFetch<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${getToken()}`,
      ...options?.headers,
    },
  });
  if (!res.ok) throw new ApiError(res.status, await res.json());
  return res.json();
}
```

---

## State Management

Zustand is used for global state. Each domain has its own store.

```ts
// stores/authStore.ts
export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  token: null,
  login: (user, token) => set({ user, token }),
  logout: () => set({ user: null, token: null }),
}));
```

Local component state (`useState`, `useReducer`) is used for UI-only state that doesn't need to be shared.

---

## Build Pipeline

Vite builds to `dist/`. The `repere/frontend` Docker container serves this with nginx.

nginx is configured with `try_files $uri /index.html` so React Router handles all routes client-side.

```bash
# Development (with Vite dev server + HMR)
npm run dev

# Production build
npm run build       # outputs to dist/
npm run preview     # preview production build locally
```

During development, `vite.config.ts` proxies `/api/v1/*` to `http://localhost:5000` so you can run the backend separately.

```ts
// vite.config.ts
export default defineConfig({
  server: {
    proxy: {
      "/api/v1": "http://localhost:5000",
      "/ws": { target: "ws://localhost:5000", ws: true },
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
