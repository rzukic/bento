import { useMemo, useState } from "react";
import { NavLink, useMatches, useNavigate } from "react-router-dom";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { useAppStore, type ThemeMode } from "../../stores/appStore";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { Icon, type IconName } from "../common/Icon";
import type { AppRouteHandle } from "../../router";
import { Button } from "../ui/Button";
import { ConfirmDialog } from "../ui/Dialog";
import { SelectField } from "../ui/SelectField";
import { Breadcrumbs, type BreadcrumbItem } from "./Breadcrumbs";
import "./AppFrame.css";

type AppFrameProps = {
  children: React.ReactNode;
};

type NavItem = {
  to: string;
  label: string;
  icon: IconName;
};

type NavSection = {
  title: string;
  items: NavItem[];
};

const navSections: NavSection[] = [
  {
    title: "Workspace",
    items: [
      { to: "/", label: "Overview", icon: "home" },
      { to: "/data", label: "Data", icon: "database" },
      { to: "/model", label: "Schema", icon: "schema" },
    ],
  },
  {
    title: "Later Modules",
    items: [
      { to: "/dashboards", label: "Dashboards", icon: "dashboard" },
      { to: "/forms", label: "Forms", icon: "forms" },
      { to: "/ai", label: "AI", icon: "sparkles" },
    ],
  },
  {
    title: "Admin",
    items: [{ to: "/settings", label: "Settings", icon: "settings" }],
  },
];

export function AppFrame({ children }: AppFrameProps) {
  const matches = useMatches();
  const navigate = useNavigate();
  const sidebarOpen = useAppStore((state) => state.sidebarOpen);
  const toggleSidebar = useAppStore((state) => state.toggleSidebar);
  const themeMode = useAppStore((state) => state.themeMode);
  const setThemeMode = useAppStore((state) => state.setThemeMode);
  const user = useAuthStore((state) => state.user);
  const logout = useAuthStore((state) => state.logout);
  const schemaSnapshot = useSchemaStore((state) => state.snapshot);
  const [logoutOpen, setLogoutOpen] = useState(false);

  function handleLogout() {
    setLogoutOpen(false);
    logout();
    navigate("/login", { replace: true });
  }

  const breadcrumbItems = useMemo<BreadcrumbItem[]>(() => {
    const items: BreadcrumbItem[] = [];
    for (const match of matches) {
      const handle = match.handle as AppRouteHandle | undefined;
      if (!handle?.title) continue;
      // Insert a parent breadcrumb for flat sibling routes that logically nest
      if (handle.breadcrumbParent) {
        items.push(handle.breadcrumbParent);
      }
      const label =
        typeof handle.title === "function"
          ? handle.title(match.params)
          : handle.title;
      items.push({ label, to: match.pathname });
    }
    return items;
  }, [matches]);

  const currentTitle =
    useMemo(() => {
      const tableId = matches[matches.length - 1]?.params?.tableId;
      if (!tableId || !schemaSnapshot) {
        return breadcrumbItems[breadcrumbItems.length - 1]?.label ?? "Repere";
      }

      const table = schemaSnapshot.tables.find((candidate) => candidate.name === tableId);
      return table?.displayName ?? breadcrumbItems[breadcrumbItems.length - 1]?.label ?? "Repere";
    }, [breadcrumbItems, matches, schemaSnapshot]);

  const currentSubtitle = useMemo(() => {
    const currentMatch = matches[matches.length - 1];
    const handle = currentMatch?.handle as AppRouteHandle | undefined;
    const tableId = currentMatch?.params?.tableId;

    if (tableId && schemaSnapshot) {
      const table = schemaSnapshot.tables.find((candidate) => candidate.name === tableId);
      if (table?.description) return table.description;
    }

    if (!handle?.subtitle) return null;
    return typeof handle.subtitle === "function"
      ? handle.subtitle(currentMatch?.params ?? {})
      : handle.subtitle;
  }, [matches, schemaSnapshot]);
  const currentHandle = matches[matches.length - 1]?.handle as
    | AppRouteHandle
    | undefined;
  const hidePageHeader = currentHandle?.hidePageHeader === true;

  useDocumentTitle(`${currentTitle} · Repere`);

  return (
    <div className={`shell ${sidebarOpen ? "" : "shell--collapsed"}`}>
      <aside className="shell__sidebar" aria-label="Primary sidebar">
        <div className="shell__brand">
          <div className="shell__brand-mark" aria-hidden="true">
            <Icon name="layers" size={16} strokeWidth={2} />
          </div>
          <div className="shell__brand-text">
            <span className="shell__brand-name">Repere</span>
            <span className="shell__brand-env">repere · local</span>
          </div>
        </div>

        <button
          type="button"
          className="shell__search"
          aria-label="Open command palette"
        >
          <Icon name="search" size={14} />
          <span className="shell__search-text">Search or jump…</span>
          <span className="kbd">⌘</span>
          <span className="kbd">K</span>
        </button>

        <nav className="shell__nav" aria-label="Main navigation">
          {navSections.map((section) => (
            <div key={section.title} className="shell__nav-section">
              <div className="shell__nav-section-title section-label">
                {section.title}
              </div>
              <ul className="shell__nav-list">
                {section.items.map((item) => (
                  <li key={item.to}>
                    <NavLink
                      to={item.to}
                      end={item.to === "/"}
                      className={({ isActive }) =>
                        isActive
                          ? "shell__nav-link shell__nav-link--active"
                          : "shell__nav-link"
                      }
                    >
                      <Icon name={item.icon} size={15} />
                      <span className="shell__nav-label">{item.label}</span>
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>

        <div className="shell__sidebar-footer">
          <div className="shell__user">
            <div className="shell__user-avatar" aria-hidden="true">
              <Icon name="user" size={14} />
            </div>
            <div className="shell__user-text">
              <span className="shell__user-name">
                {user?.username ?? "signed out"}
              </span>
              <span className="shell__user-role">{user?.role ?? "—"}</span>
            </div>
            <Button
              variant="ghost"
              size="icon"
              className="shell__user-menu"
              aria-label="Sign out"
              onClick={() => setLogoutOpen(true)}
            >
              <Icon name="logout" size={14} />
            </Button>
          </div>
        </div>
      </aside>

      <main className="shell__main">
        <header className="shell__topbar" aria-label="Toolbar">
          <Button
            variant="ghost"
            size="icon"
            className="shell__collapse"
            onClick={toggleSidebar}
            aria-pressed={sidebarOpen}
            aria-label={sidebarOpen ? "Collapse sidebar" : "Expand sidebar"}
          >
            <Icon
              name={sidebarOpen ? "chevron-left" : "chevron-right"}
              size={14}
            />
          </Button>

          <Breadcrumbs items={breadcrumbItems} />

          <div className="shell__topbar-spacer" />

          <div className="shell__theme-picker">
            <SelectField
              label="Theme"
              hideLabel
              compact
              value={themeMode}
              onChange={(event) =>
                setThemeMode(event.target.value as ThemeMode)
              }
              options={[
                { label: "System", value: "system" },
                { label: "Dark", value: "dark" },
                { label: "Light", value: "light" },
              ]}
            />
          </div>
          <span className="badge">sqlite · dev</span>
          <span className="badge">v0.1.0</span>
        </header>

        <div
          className={
            hidePageHeader ? "shell__page shell__page--immersive" : "shell__page"
          }
        >
          {!hidePageHeader ? (
            <header className="shell__page-header">
              <div className="shell__page-heading">
                <h1 className="shell__page-title">{currentTitle}</h1>
                {currentSubtitle ? (
                  <p className="shell__page-subtitle">{currentSubtitle}</p>
                ) : null}
              </div>
              <div className="shell__page-actions">
                <Button leadingIcon={<Icon name="activity" size={14} />}>
                  View logs
                </Button>
                <Button
                  variant="primary"
                  leadingIcon={<Icon name="plus" size={14} strokeWidth={2.25} />}
                >
                  New
                </Button>
              </div>
            </header>
          ) : null}

          <div
            className={
              hidePageHeader
                ? "shell__page-body shell__page-body--immersive"
                : "shell__page-body"
            }
          >
            {children}
          </div>
        </div>
      </main>

      <ConfirmDialog
        open={logoutOpen}
        title="Sign out of this workspace?"
        description="This clears the in-memory access token and sends you back to the public login route. The refresh cookie remains managed by the backend auth flow."
        confirmLabel="Sign out"
        confirmVariant="danger"
        onConfirm={handleLogout}
        onCancel={() => setLogoutOpen(false)}
      />
    </div>
  );
}
