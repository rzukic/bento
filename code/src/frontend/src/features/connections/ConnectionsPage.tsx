import { useSearchParams } from "react-router-dom";
import { useAuthStore } from "../../stores/authStore";
import { ApiExplorer } from "./ApiExplorer";
import { ApiKeysPanel } from "./ApiKeysPanel";
import "./Connections.css";

type Tab = "api" | "keys";

/** Connections: the API of this installation and the keys scripts use for it. */
export function ConnectionsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const user = useAuthStore((state) => state.user);
  const isAdmin = user?.role === "admin";
  const tab: Tab = searchParams.get("tab") === "keys" ? "keys" : "api";

  const select = (next: Tab) => {
    const params = new URLSearchParams(searchParams);
    if (next === "api") params.delete("tab");
    else params.set("tab", next);
    setSearchParams(params, { replace: true });
  };

  return (
    <div className="connections-page">
      <header className="connections-page__header">
        <h1 className="connections-page__title">Connections</h1>
        <p className="connections-page__lead">Use your data from scripts, spreadsheets and other tools.</p>
      </header>

      <div className="connections-tabs" role="tablist" aria-label="Connections">
        <button type="button" role="tab" aria-selected={tab === "api"} className="connections-tabs__tab" onClick={() => select("api")}>
          API
        </button>
        <button type="button" role="tab" aria-selected={tab === "keys"} className="connections-tabs__tab" onClick={() => select("keys")}>
          API keys
        </button>
      </div>

      <div role="tabpanel" className="connections-page__panel">
        {tab === "api" ? (
          <ApiExplorer />
        ) : isAdmin ? (
          <ApiKeysPanel />
        ) : (
          <p className="api-keys__intro">API keys are created by an administrator. Ask one for a key for your script.</p>
        )}
      </div>
    </div>
  );
}
