import { useEffect } from "react";
import { useWorkspaceName } from "../stores/workspacesStore";

/** The tab's title; with more than one workspace it names the current one: "transactions · Finances · Bento". */
export function useDocumentTitle(title: string) {
  const workspace = useWorkspaceName();
  const shown = workspace && title.endsWith(" · Bento") ? `${title.slice(0, -" · Bento".length)} · ${workspace} · Bento` : title;
  useEffect(() => {
    document.title = shown;
  }, [shown]);
}
