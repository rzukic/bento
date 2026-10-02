import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { ThemeSwitch } from "../components/common/ThemeSwitch";
import { Button } from "../components/ui/Button";
import { ConfirmDialog } from "../components/ui/Dialog";
import { Panel } from "../components/ui/Panel";
import { useSetupStore } from "../features/setup/setupStore";
import { useAuthStore } from "../stores/authStore";
import { useSchemaStore } from "../stores/schemaStore";
import "./SettingsPage.css";

export function SettingsPage() {
  const user = useAuthStore((state) => state.user);
  const logout = useAuthStore((state) => state.logout);
  const setupInfo = useSetupStore((state) => state.info);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const navigate = useNavigate();
  const [logoutOpen, setLogoutOpen] = useState(false);

  const facts: Array<[string, string]> = [
    ["Database", setupInfo?.database_provider ?? "—"],
    ["Auth mode", setupInfo?.auth_mode ?? "—"],
    ["Backend", setupInfo ? `v${setupInfo.version}` : "—"],
    ["Schema", snapshot ? `version ${snapshot.version}` : setupInfo ? `version ${setupInfo.schema_version}` : "—"]
  ];

  return (
    <div className="settings">
      <Panel title="Appearance" description="Applies to this browser immediately.">
        <div className="settings__row">
          <span className="settings__label">Theme</span>
          <ThemeSwitch />
        </div>
      </Panel>

      <Panel title="Deployment" description="Read from the environment or Docker Compose; change it there and restart the stack.">
        <dl className="settings__facts">
          {facts.map(([label, value]) => (
            <div key={label} className="settings__fact">
              <dt className="section-label">{label}</dt>
              <dd className="mono">{value}</dd>
            </div>
          ))}
        </dl>
      </Panel>

      <Panel title="Account" description="Additional users can be created through the API until user management lands in the UI.">
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
        description="This ends the session in this browser; the refresh cookie is revoked on the server."
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
