import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { ThemeSwitch } from "../components/common/ThemeSwitch";
import { Button } from "../components/ui/Button";
import { ConfirmDialog } from "../components/ui/Dialog";
import { Panel } from "../components/ui/Panel";
import { NotificationsPanel } from "../features/notifications/NotificationsPanel";
import { RulesPanel } from "../features/notifications/RulesPanel";
import { ProblemsPanel } from "../features/problems/ProblemsPanel";
import { BrowserFormatRow, WorkspaceFormatPanel } from "../features/settings/FormatSettings";
import { useSetupStore } from "../features/setup/setupStore";
import { useAuthStore } from "../stores/authStore";
import "./SettingsPage.css";

export function SettingsPage() {
  const user = useAuthStore((state) => state.user);
  const logout = useAuthStore((state) => state.logout);
  const setupInfo = useSetupStore((state) => state.info);
  const navigate = useNavigate();
  const [logoutOpen, setLogoutOpen] = useState(false);

  const facts: Array<[string, string]> = [
    ["Version", setupInfo?.version ?? "—"],
    ["Database", databaseLabel(setupInfo?.database_provider)]
  ];

  return (
    <div className="settings">
      <Panel title="Appearance" description="Applies to this browser immediately.">
        <div className="settings__row">
          <span className="settings__label">Theme</span>
          <ThemeSwitch />
        </div>
        <BrowserFormatRow />
      </Panel>

      <Panel title="About">
        <dl className="settings__facts">
          {facts.map(([label, value]) => (
            <div key={label} className="settings__fact">
              <dt className="section-label">{label}</dt>
              <dd className="mono">{value}</dd>
            </div>
          ))}
        </dl>
      </Panel>

      {user?.role === "admin" ? <WorkspaceFormatPanel /> : null}

      {user?.role === "admin" ? <NotificationsPanel /> : null}

      {user?.role === "admin" ? <RulesPanel /> : null}

      {user?.role === "admin" ? <ProblemsPanel /> : null}

      <Panel title="Account">
        <div className="settings__row">
          <div className="settings__account">
            <span className="settings__avatar display" aria-hidden="true">
              {(user?.username ?? "?").slice(0, 1).toUpperCase()}
            </span>
            <div>
              <div className="settings__name">{user?.username ?? "—"}</div>
              <div className="section-label">{user?.role ?? "—"}</div>
            </div>
          </div>
          <Button variant="danger" onClick={() => setLogoutOpen(true)}>
            Sign out
          </Button>
        </div>
      </Panel>

      <ConfirmDialog
        open={logoutOpen}
        title="Sign out of this workspace?"
        description="You will need to sign in again to get back to your workspace."
        confirmLabel="Sign out"
        confirmVariant="danger"
        onConfirm={() => {
          setLogoutOpen(false);
          logout();
          navigate("/login", { replace: true });
        }}
        onCancel={() => setLogoutOpen(false)}
      />
    </div>
  );
}

function databaseLabel(provider: string | undefined): string {
  switch (provider) {
    case "sqlite":
      return "SQLite";
    case "postgres":
    case "postgresql":
      return "PostgreSQL";
    default:
      return provider ?? "—";
  }
}
