import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { NavLink, useMatches, useNavigate } from "react-router-dom";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { useAppStore } from "../../stores/appStore";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { Icon, type IconName } from "../common/Icon";
import type { AppRouteHandle } from "../../router";
import { Button } from "../ui/Button";
import { ConfirmDialog } from "../ui/Dialog";
import { ThemeSwitch } from "../common/ThemeSwitch";
import { Breadcrumbs, type BreadcrumbItem } from "./Breadcrumbs";
import "./AppFrame.css";
import { SushiMascot } from "../brand";

const sidebarShortcutLabel =
  typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform) ? "⌘\\" : "Ctrl+\\";

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
  /** Modules that are not built yet render dimmed. */
  dimmed?: boolean;
};

const navSections: NavSection[] = [
  {
    title: "Workspace",
    items: [
      { to: "/", label: "Home", icon: "home" },
      { to: "/data", label: "Data", icon: "database" },
      { to: "/model", label: "Schema", icon: "schema" },
      { to: "/dashboards", label: "Dashboards", icon: "dashboard" },
      { to: "/connections", label: "Connections", icon: "link" },
    ],
  },
  {
    title: "Coming soon",
    dimmed: true,
    items: [
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

  // Collapsed, the sidebar opens over the page while the pointer (or the
  // keyboard focus) is on it — a short delay keeps passing by from opening it.
  const [peek, setPeek] = useState(false);
  const peekTimer = useRef<number | undefined>(undefined);
  const canPeek = useCallback(
    () => !sidebarOpen && (typeof window.matchMedia !== "function" || window.matchMedia("(min-width: 901px)").matches),
    [sidebarOpen],
  );
  const schedulePeek = useCallback(
    (next: boolean, delay: number) => {
      window.clearTimeout(peekTimer.current);
      if (next && !canPeek()) return;
      peekTimer.current = window.setTimeout(() => setPeek(next), delay);
    },
    [canPeek],
  );
  // Kept open while hovered: it is open already, so the page beside it moves
  // over at once instead of sliding (before the browser paints the change).
  const [instant, setInstant] = useState(false);
  const instantTimer = useRef<number | undefined>(undefined);
  useLayoutEffect(() => {
    if (!sidebarOpen || !peek) return;
    window.clearTimeout(peekTimer.current);
    window.clearTimeout(instantTimer.current);
    setInstant(true);
    setPeek(false);
    instantTimer.current = window.setTimeout(() => setInstant(false), 400);
  }, [sidebarOpen, peek]);
  useEffect(
    () => () => {
      window.clearTimeout(peekTimer.current);
      window.clearTimeout(instantTimer.current);
    },
    [],
  );

  // ⌘\ (Ctrl+\ elsewhere) toggles the sidebar from anywhere.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "\\" && (event.metaKey || event.ctrlKey) && !event.altKey) {
        event.preventDefault();
        toggleSidebar();
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [toggleSidebar]);
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
        return breadcrumbItems[breadcrumbItems.length - 1]?.label ?? "Bento";
      }

      const table = schemaSnapshot.tables.find((candidate) => candidate.name === tableId);
      return table?.displayName ?? breadcrumbItems[breadcrumbItems.length - 1]?.label ?? "Bento";
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
  const customPageHeader = currentHandle?.customPageHeader === true;

  useDocumentTitle(`${currentTitle} · Bento`);

  return (
    <div className={`shell ${sidebarOpen ? "" : "shell--collapsed"} ${peek ? "shell--peek" : ""} ${instant ? "shell--instant" : ""}`}>
      <div className="shell__sky" aria-hidden="true">
        <div className="shell__pool shell__pool--1" />
        <div className="shell__pool shell__pool--2" />
        <svg className="shell__grain" xmlns="http://www.w3.org/2000/svg">
          <filter id="shell-grain">
            <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" stitchTiles="stitch" />
            <feColorMatrix type="saturate" values="0" />
          </filter>
          <rect width="100%" height="100%" filter="url(#shell-grain)" />
        </svg>
      </div>
      <aside
        className="shell__sidebar"
        aria-label="Primary sidebar"
        onMouseEnter={() => schedulePeek(true, 150)}
        onMouseLeave={() => schedulePeek(false, 250)}
        onFocus={() => schedulePeek(true, 0)}
        onBlur={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) schedulePeek(false, 0);
        }}
        onKeyDown={(event) => {
          if (event.key === "Escape" && peek) schedulePeek(false, 0);
        }}
      >
        <div className="shell__brand">
          {/* Collapsed, the mark doubles as the expand control: it turns
              into the sidebar icon on hover. */}
          <button
            type="button"
            className="shell__brand-mark"
            onClick={sidebarOpen || peek ? undefined : toggleSidebar}
            tabIndex={sidebarOpen || peek ? -1 : 0}
            aria-hidden={sidebarOpen || peek ? true : undefined}
            aria-label={sidebarOpen || peek ? undefined : `Expand sidebar (${sidebarShortcutLabel})`}
            title={sidebarOpen || peek ? undefined : `Expand sidebar (${sidebarShortcutLabel})`}
          >
            <span className="shell__brand-logo">
              <SushiMascot width={32} />
            </span>
            <span className="shell__brand-expand">
              <Icon name="panel-left" size={16} />
            </span>
          </button>
          <div className="shell__brand-text">
            <span className="shell__brand-name">Bento</span>
          </div>
          {/* Open: collapses it. Opened by hover: keeps it open, in the same spot. */}
          <button
            type="button"
            className="shell__sidebar-toggle"
            onClick={toggleSidebar}
            aria-label={sidebarOpen ? `Collapse sidebar (${sidebarShortcutLabel})` : `Keep sidebar open (${sidebarShortcutLabel})`}
            title={sidebarOpen ? `Collapse sidebar (${sidebarShortcutLabel})` : `Keep sidebar open (${sidebarShortcutLabel})`}
          >
            <Icon name="panel-left" size={16} />
          </button>
        </div>

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
                        [
                          "shell__nav-link",
                          isActive ? "shell__nav-link--active" : "",
                          section.dimmed ? "shell__nav-link--dimmed" : ""
                        ]
                          .filter(Boolean)
                          .join(" ")
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
            <div className="shell__user-avatar display" aria-hidden="true">
              {(user?.username ?? "?").slice(0, 1).toUpperCase()}
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
          <Breadcrumbs items={breadcrumbItems} />

          <div className="shell__topbar-spacer" />

          <ThemeSwitch className="shell__theme" />
        </header>

        <div
          className={
            hidePageHeader ? "shell__page shell__page--immersive" : "shell__page"
          }
        >
          {!hidePageHeader && !customPageHeader ? (
            <header className="shell__page-header">
              <div className="shell__page-heading">
                <h1 className="shell__page-title display">{currentTitle}</h1>
                {currentSubtitle ? (
                  <p className="shell__page-subtitle">{currentSubtitle}</p>
                ) : null}
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
        description="You will need to sign in again to get back to your workspace."
        confirmLabel="Sign out"
        confirmVariant="danger"
        onConfirm={handleLogout}
        onCancel={() => setLogoutOpen(false)}
      />
    </div>
  );
}
