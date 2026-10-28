import { isInstallationAdmin } from "../../types/auth";
import { useSearchParams } from "react-router-dom";
import { useAuthStore } from "../../stores/authStore";
import { ApiExplorer } from "./ApiExplorer";
import { ApiKeysPanel } from "./ApiKeysPanel";
import { McpPanel } from "./McpPanel";
import "./Connections.css";

type Tab = "api" | "keys" | "ai";

/** Connections: the API of this installation, the keys scripts use for it, and AI apps (Claude, ChatGPT, Codex) over MCP. */
export function ConnectionsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const user = useAuthStore((state) => state.user);
  // API keys belong to the installation (Step 5.5): its admins make them, for chosen workspaces.
  const isAdmin = isInstallationAdmin(user);
  const requested = searchParams.get("tab");
  // "claude" was the AI apps tab's first name; old links still open it.
  const tab: Tab = requested === "keys" ? "keys" : requested === "ai" || requested === "claude" ? "ai" : "api";

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
        <p className="connections-page__lead">Use your data from scripts, spreadsheets, other tools and AI apps such as Claude and ChatGPT.</p>
      </header>

      <div className="connections-tabs" role="tablist" aria-label="Connections">
        <button type="button" role="tab" aria-selected={tab === "api"} className="connections-tabs__tab" onClick={() => select("api")}>
          API
        </button>
        <button type="button" role="tab" aria-selected={tab === "keys"} className="connections-tabs__tab" onClick={() => select("keys")}>
          API keys
        </button>
        <button type="button" role="tab" aria-selected={tab === "ai"} className="connections-tabs__tab" onClick={() => select("ai")}>
          AI apps
        </button>
      </div>

      <div role="tabpanel" className="connections-page__panel">
        {tab === "api" ? (
          <ApiExplorer />
        ) : tab === "ai" ? (
          <McpPanel />
        ) : isAdmin ? (
          <ApiKeysPanel />
        ) : (
          <p className="api-keys__intro">API keys are created by an administrator. Ask one for a key for your script.</p>
        )}
      </div>
    </div>
  );
}
