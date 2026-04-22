import { createBrowserRouter, createMemoryRouter, type RouteObject } from "react-router-dom";
import { App } from "./App";
import { RequireAuth } from "./components/auth/RequireAuth";
import { ShellLayout } from "./components/layout/ShellLayout";
import { DataExplorerPage, TableDetailPage } from "./features/data-explorer";
import { AiPage } from "./routes/AiPage";
import { DashboardsPage } from "./routes/DashboardsPage";
import { FormsPage } from "./routes/FormsPage";
import { HomePage } from "./routes/HomePage";
import { LoginPage } from "./routes/LoginPage";
import { ModelPage } from "./routes/ModelPage";
import { SettingsPage } from "./routes/SettingsPage";

export type AppRouteHandle = {
  title: string | ((params: Record<string, string | undefined>) => string);
  subtitle?: string | ((params: Record<string, string | undefined>) => string);
  hidePageHeader?: boolean;
  navLabel?: string;
  navGroup?: string;
  /** Optional parent breadcrumb for routes that are flat siblings but should appear nested. */
  breadcrumbParent?: { label: string; to: string };
};

export const appRoutes: RouteObject[] = [
  {
    path: "/login",
    element: <LoginPage />,
    handle: {
      title: "Login"
    } satisfies AppRouteHandle
  },
  {
    path: "/",
    element: <App />,
    children: [
      {
        element: <RequireAuth />,
        children: [
          {
            element: <ShellLayout />,
            children: [
              {
                index: true,
                element: <HomePage />,
                handle: {
                  title: "Home",
                  navLabel: "Home",
                  navGroup: "Workspace"
                } satisfies AppRouteHandle
              },
              {
                path: "data",
                element: <DataExplorerPage />,
                handle: {
                  title: "Data Explorer",
                  subtitle: "Browse your tables and records.",
                  navLabel: "Data",
                  navGroup: "Workspace"
                } satisfies AppRouteHandle
              },
              {
                path: "data/:tableId",
                element: <TableDetailPage />,
                handle: {
                  title: ({ tableId }) => tableId ?? "Table",
                  hidePageHeader: true,
                  breadcrumbParent: { label: "Data Explorer", to: "/data" }
                } satisfies AppRouteHandle
              },
              {
                path: "model",
                element: <ModelPage />,
                handle: {
                  title: "Model",
                  subtitle: "Inspect and stage Model JSON drafts safely.",
                  hidePageHeader: true,
                  navLabel: "Model",
                  navGroup: "Workspace"
                } satisfies AppRouteHandle
              },
              {
                path: "dashboards",
                element: <DashboardsPage />,
                handle: {
                  title: "Dashboards",
                  navLabel: "Dashboards",
                  navGroup: "Later Modules"
                } satisfies AppRouteHandle
              },
              {
                path: "forms",
                element: <FormsPage />,
                handle: {
                  title: "Forms",
                  navLabel: "Forms",
                  navGroup: "Later Modules"
                } satisfies AppRouteHandle
              },
              {
                path: "ai",
                element: <AiPage />,
                handle: {
                  title: "AI Assistant",
                  navLabel: "AI",
                  navGroup: "Later Modules"
                } satisfies AppRouteHandle
              },
              {
                path: "settings",
                element: <SettingsPage />,
                handle: {
                  title: "Settings",
                  navLabel: "Settings",
                  navGroup: "Admin"
                } satisfies AppRouteHandle
              }
            ]
          }
        ]
      }
    ]
  }
];

export const router = createBrowserRouter(appRoutes);

export function createTestRouter(initialEntries: string[]) {
  return createMemoryRouter(appRoutes, { initialEntries });
}
