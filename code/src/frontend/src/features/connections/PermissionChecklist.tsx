import { MCP_PERMISSIONS, type McpPermission } from "../../api/mcp";
import "./OAuthConsent.css";

/**
 * What a connected app may do, area by area (Step 4.6): reading is always in,
 * the rest as far as the user's role allows. Used on the consent page and in
 * a connected app's details.
 */
export function PermissionChecklist({
  allowed,
  granted,
  onChange,
  disabled = false,
}: {
  allowed: McpPermission[];
  granted: McpPermission[];
  onChange: (next: McpPermission[]) => void;
  disabled?: boolean;
}) {
  return (
    <fieldset className="oauth-consent__choices" disabled={disabled}>
      <legend>
        What it may do
        <span className="oauth-consent__quick">
          <button type="button" onClick={() => onChange(MCP_PERMISSIONS.map((p) => p.id).filter((id) => allowed.includes(id)))}>
            All
          </button>
          <button type="button" onClick={() => onChange(["data.read"])}>
            Read only
          </button>
        </span>
      </legend>
      {MCP_PERMISSIONS.map((permission) => {
        const isAllowed = allowed.includes(permission.id);
        const always = permission.id === "data.read";
        const checked = granted.includes(permission.id);
        return (
          <label
            key={permission.id}
            className={`oauth-consent__choice${isAllowed ? "" : " oauth-consent__choice--off"}${permission.danger ? " oauth-consent__choice--danger" : ""}`}
          >
            <input
              type="checkbox"
              checked={checked}
              disabled={always || !isAllowed}
              onChange={() =>
                onChange(
                  checked
                    ? granted.filter((id) => id !== permission.id)
                    : MCP_PERMISSIONS.map((p) => p.id).filter((id) => id === permission.id || granted.includes(id)),
                )
              }
            />
            <span>
              <strong>{permission.label}</strong>
              <small>{isAllowed ? permission.detail : `Needs the ${permission.needs} role.`}</small>
            </span>
          </label>
        );
      })}
    </fieldset>
  );
}
