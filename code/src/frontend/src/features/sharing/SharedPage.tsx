import { useEffect, useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import { setShareToken } from "../../api/client";
import { getShareInfo, type ShareInfo } from "../../api/shares";
import { Spinner } from "../../components/ui/Spinner";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { setWorkspaceCalendar } from "../../lib/calendar";
import { setWorkspaceFormat } from "../../lib/preferences";
import { DashboardPage } from "../dashboards/DashboardPage";
import "../dashboards/Share.css";

/** Inside an iframe? (A browser that refuses to tell counts as yes.) */
function framed(): boolean {
  try {
    return window.self !== window.top;
  } catch {
    return true;
  }
}

/**
 * Step 5.6: a dashboard through a share link, for anyone with it. Every
 * request goes as the link's visitor; the server answers only what the
 * dashboard's widgets ask for.
 */
export function SharedPage() {
  const { token = "" } = useParams();
  const [params] = useSearchParams();
  const embed = params.get("embed") === "1" || framed();
  const [info, setInfo] = useState<ShareInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  useDocumentTitle(info ? `${info.name} · Bento` : "Bento");

  useEffect(() => {
    setShareToken(token);
    let cancelled = false;
    getShareInfo()
      .then((next) => {
        if (cancelled) return;
        // The link's workspace decides how numbers and dates read.
        setWorkspaceFormat(next.settings.locale, next.settings.currency);
        setWorkspaceCalendar({ timeZone: next.settings.timeZone, weekStart: next.settings.weekStart as never, yearStart: next.settings.yearStart });
        setInfo(next);
      })
      .catch(() => !cancelled && setError("This link does not exist, has expired or was revoked."));
    return () => {
      cancelled = true;
      setShareToken(null);
    };
  }, [token]);

  if (error) return <div className="shared-page__message">{error}</div>;
  if (!info) return <Spinner label="Opening the dashboard" />;
  if (framed() && !info.embed) return <div className="shared-page__message">This dashboard may not be shown inside another site. Open the link directly.</div>;

  const filters = Object.fromEntries(Object.entries(info.filters ?? {}).map(([column, values]) => [column, values.map(String)]));
  return (
    <main className={embed ? "shared-page shared-page--embed" : "shared-page"}>
      <DashboardPage shared={{ dashboardId: info.dashboardId, filters, embed }} />
      {embed ? null : <p className="shared-page__footer">Shared from Bento{info.expiresAt ? ` · this link works until ${new Date(info.expiresAt).toLocaleDateString()}` : ""}</p>}
    </main>
  );
}
