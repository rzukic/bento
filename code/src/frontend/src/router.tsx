import { lazy, Suspense, type ReactNode } from "react";
import { createBrowserRouter, createMemoryRouter, type RouteObject } from "react-router-dom";
import { App } from "./App";
import { RequireAuth } from "./components/auth/RequireAuth";
import { ShellLayout } from "./components/layout/ShellLayout";
import { DataExplorerPage, ImportPage, NewTableFromCsvPage, TableDetailPage } from "./features/data-explorer";
import { SetupGate, SetupPage } from "./features/setup";
import { Spinner } from "./components/ui/Spinner";

// Dashboards carry the chart library; it loads when a dashboard opens.
const DashboardsListPage = lazy(() => import("./features/dashboards/DashboardsListPage").then((m) => ({ default: m.DashboardsListPage })));
const ConnectionsPage = lazy(() => import("./features/connections/ConnectionsPage").then((m) => ({ default: m.ConnectionsPage })));
const FunctionsPage = lazy(() => import("./features/functions/FunctionsPage").then((m) => ({ default: m.FunctionsPage })));
const DashboardPage = lazy(() => import("./features/dashboards/DashboardPage").then((m) => ({ default: m.DashboardPage })));
const FormsPage = lazy(() => import("./features/forms/FormsPage").then((m) => ({ default: m.FormsPage })));
const FormPage = lazy(() => import("./features/forms/FormsPage").then((m) => ({ default: m.FormPage })));
const FormBuilder = lazy(() => import("./features/forms/FormBuilder").then((m) => ({ default: m.FormBuilder })));
const AiPage = lazy(() => import("./features/ai/AiPage").then((m) => ({ default: m.AiPage })));
const OAuthConsentPage = lazy(() => import("./features/connections/OAuthConsentPage").then((m) => ({ default: m.OAuthConsentPage })));

function Lazy({ children }: { children: ReactNode }) {
  return <Suspense fallback={<Spinner label="Loading" />}>{children}</Suspense>;
}
import { HomePage } from "./routes/HomePage";
import { LoginPage } from "./routes/LoginPage";
import { ModelPage } from "./routes/ModelPage";
import { SettingsPage } from "./routes/SettingsPage";

export type AppRouteHandle = {
  title: string | ((params: Record<string, string | undefined>) => string);
  subtitle?: string | ((params: Record<string, string | undefined>) => string);
  hidePageHeader?: boolean;
  /** The page renders its own heading; the shell keeps its padding but draws no header. */
  customPageHeader?: boolean;
  navLabel?: string;
  navGroup?: string;
  /** Optional parent breadcrumb for routes that are flat siblings but should appear nested. */
  breadcrumbParent?: { label: string; to: string };
};

const shellRoutes: RouteObject[] = [
  {
    index: true,
    element: <HomePage />,
    handle: {
      title: "Home",
      customPageHeader: true,
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
    path: "data/new/csv",
    element: <NewTableFromCsvPage />,
    handle: {
      title: "New table from CSV",
      hidePageHeader: true,
      breadcrumbParent: { label: "Data Explorer", to: "/data" }
    } satisfies AppRouteHandle
  },
  {
    path: "data/:tableId/import",
    element: <ImportPage />,
    handle: {
      title: ({ tableId }) => `Import into ${tableId ?? "table"}`,
      hidePageHeader: true,
      breadcrumbParent: { label: "Data Explorer", to: "/data" }
    } satisfies AppRouteHandle
  },
  {
    path: "model",
    element: <ModelPage />,
    handle: {
      title: "Schema",
      subtitle: "Design tables, columns and relations.",
      hidePageHeader: true,
      navLabel: "Schema",
      navGroup: "Workspace"
    } satisfies AppRouteHandle
  },
  {
    path: "dashboards",
    element: (
      <Lazy>
        <DashboardsListPage />
      </Lazy>
    ),
    handle: {
      title: "Dashboards",
      hidePageHeader: true,
      navLabel: "Dashboards",
      navGroup: "Workspace"
    } satisfies AppRouteHandle
  },
  {
    path: "dashboards/:dashboardId",
    element: (
      <Lazy>
        <DashboardPage />
      </Lazy>
    ),
    handle: {
      title: "Dashboard",
      hidePageHeader: true,
      breadcrumbParent: { label: "Dashboards", to: "/dashboards" }
    } satisfies AppRouteHandle
  },
  {
    path: "functions",
    element: (
      <Lazy>
        <FunctionsPage />
      </Lazy>
    ),
    handle: {
      title: "Functions",
      hidePageHeader: true,
      navLabel: "Functions",
      navGroup: "Workspace"
    } satisfies AppRouteHandle
  },
  {
    path: "connections",
    element: (
      <Lazy>
        <ConnectionsPage />
      </Lazy>
    ),
    handle: {
      title: "Connections",
      hidePageHeader: true,
      navLabel: "Connections",
      navGroup: "Workspace"
    } satisfies AppRouteHandle
  },
  {
    path: "forms",
    element: (
      <Lazy>
        <FormsPage />
      </Lazy>
    ),
    handle: {
      title: "Forms",
      hidePageHeader: true,
      navLabel: "Forms",
      navGroup: "Workspace"
    } satisfies AppRouteHandle
  },
  {
    path: "forms/new",
    element: (
      <Lazy>
        <FormBuilder />
      </Lazy>
    ),
    handle: { title: "New form", hidePageHeader: true, breadcrumbParent: { label: "Forms", to: "/forms" } } satisfies AppRouteHandle
  },
  {
    path: "forms/:formId",
    element: (
      <Lazy>
        <FormPage />
      </Lazy>
    ),
    handle: { title: "Form", hidePageHeader: true, breadcrumbParent: { label: "Forms", to: "/forms" } } satisfies AppRouteHandle
  },
  {
    path: "forms/:formId/edit",
    element: (
      <Lazy>
        <FormBuilder />
      </Lazy>
    ),
    handle: { title: "Edit form", hidePageHeader: true, breadcrumbParent: { label: "Forms", to: "/forms" } } satisfies AppRouteHandle
  },
  {
    path: "ai",
    element: (
      <Lazy>
        <AiPage />
      </Lazy>
    ),
    handle: {
      title: "AI",
      hidePageHeader: true,
      navLabel: "AI",
      navGroup: "Workspace"
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
];

export const appRoutes: RouteObject[] = [
  {
    // The wizard decides on its own whether it should be shown; everything
    // else sits behind the SetupGate so an uninitialised installation lands
    // here from any path, including /login.
    path: "/setup",
    element: <SetupPage />,
    handle: {
      title: "Setup"
    } satisfies AppRouteHandle
  },
  {
    element: <SetupGate />,
    children: [
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
                // An MCP client (Claude) asks to connect; full-screen like the login.
                path: "oauth/authorize",
                element: (
                  <Lazy>
                    <OAuthConsentPage />
                  </Lazy>
                ),
                handle: { title: "Connect an app" } satisfies AppRouteHandle
              },
              {
                element: <ShellLayout />,
                children: shellRoutes
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
