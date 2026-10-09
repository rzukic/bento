import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { decideAuthorization, getAuthorizeInfo, permissionsFromScope, type AuthorizeInfo, type McpPermission } from "../../api/mcp";
import { Glyph, Sky } from "../../components/common/Atmosphere";
import { Icon } from "../../components/common/Icon";
import { ThemeSwitch } from "../../components/common/ThemeSwitch";
import { SushiMascot } from "../../components/brand";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { useAuthStore } from "../../stores/authStore";
import "../../routes/LoginPage.css";
import "./OAuthConsent.css";
import { PermissionChecklist } from "./PermissionChecklist";

/**
 * /oauth/authorize (Step 4.6): an MCP client such as Claude asks to use Bento
 * as the signed-in user. The user ticks what it may do, area by area (reading
 * is always in; the role limits the rest), and is sent back to the client with
 * a code (or with access_denied).
 */
export function OAuthConsentPage() {
  useDocumentTitle("Connect an app · Bento");
  const [params] = useSearchParams();
  const user = useAuthStore((state) => state.user);

  const clientId = params.get("client_id") ?? "";
  const redirectUri = params.get("redirect_uri") ?? "";
  const codeChallenge = params.get("code_challenge") ?? "";
  const codeChallengeMethod = params.get("code_challenge_method") ?? "S256";
  const state = params.get("state");
  const scopeParam = params.get("scope");
  const responseType = params.get("response_type");

  const [info, setInfo] = useState<AuthorizeInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [granted, setGranted] = useState<McpPermission[]>(["data.read"]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (responseType !== "code" || !clientId || !redirectUri || !codeChallenge) {
      setError("This sign-in link is incomplete. Start connecting again from your app.");
      return;
    }
    let cancelled = false;
    getAuthorizeInfo(clientId, redirectUri)
      .then((next) => {
        if (cancelled) return;
        setInfo(next);
        // What the app asked for, as far as the user's role allows it.
        setGranted(permissionsFromScope(scopeParam).filter((id) => next.allowed.includes(id)));
      })
      .catch((cause) => {
        if (!cancelled) setError(cause instanceof ApiClientError ? cause.message : "The app could not be checked. Try again.");
      });
    return () => {
      cancelled = true;
    };
  }, [clientId, redirectUri, codeChallenge, responseType, scopeParam]);

  const decide = async (approve: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const redirectTo = await decideAuthorization({ clientId, redirectUri, codeChallenge, codeChallengeMethod, permissions: granted, state, approve });
      window.location.assign(redirectTo);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "That did not work. Try again.");
      setBusy(false);
    }
  };

  const name = info?.clientName ?? "An app";

  return (
    <main className="login oauth-consent" aria-labelledby="consent-title">
      <Sky />
      <Glyph value="MCP" />
      <div className="login__frame">
        <header className="login__header">
          <div className="login__brand">
            <span className="login__brand-mark" aria-hidden="true">
              <SushiMascot width={32} />
            </span>
            <span className="login__brand-name">Bento</span>
          </div>
          <div className="login__header-right">
            <ThemeSwitch />
          </div>
        </header>

        <div className="login__body">
          <section className="login__content">
            <span className="login__eyebrow">Connect an app</span>
            <h1 id="consent-title" className="login__title display">
              <span className="login__line">
                <span>Let {name}</span>
              </span>
              <span className="login__line">
                <span style={{ animationDelay: "0.1s" }}>use Bento?</span>
              </span>
            </h1>
            <p className="login__lede">
              {info ? (
                <>
                  <strong>{info.clientName}</strong> will work with your data as <strong>{user?.username ?? "you"}</strong> and then
                  return to <code className="oauth-consent__host">{info.redirectHost}</code>.
                </>
              ) : error ? null : (
                "Checking the app…"
              )}
            </p>
          </section>

          <div className="login__form oauth-consent__form">
            {error ? (
              <p className="login__error" role="alert">
                {error}
              </p>
            ) : null}

            {info ? (
              <>
                <PermissionChecklist allowed={info.allowed} granted={granted} onChange={setGranted} disabled={busy} />
                <p className="oauth-consent__note">
                  It acts as you and never manages users or keys. Change what it may do or disconnect it any time under Connections → Claude.
                </p>
                <div className="login__row oauth-consent__actions">
                  <button type="button" className="btn btn--ghost btn--lg" disabled={busy} onClick={() => void decide(false)}>
                    Deny
                  </button>
                  <button type="button" className="btn btn--primary btn--lg" disabled={busy} onClick={() => void decide(true)}>
                    {busy ? "Connecting…" : "Allow"} <Icon name="arrow-right" size={14} />
                  </button>
                </div>
              </>
            ) : null}
          </div>
        </div>

        <footer className="login__footer">
          <div className="login__rule" aria-hidden="true" />
          <div className="login__footer-row">
            <span className="login__hint">© Bento</span>
          </div>
        </footer>
      </div>
    </main>
  );
}
