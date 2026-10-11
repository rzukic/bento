import { Fragment, useCallback, useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { clearProblems, listProblems, type Problem } from "../../api/problems";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "./Problems.css";

const kindLabels: Record<Problem["kind"], string> = { slow: "slow", timeout: "timeout", error: "error", import: "import" };

function formatWhen(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "medium" }).format(date);
}

function formatDuration(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${ms} ms`;
}

/** Pretty JSON where the detail starts with it (a query), else as it is. */
function pretty(detail: string): string {
  const [first, ...rest] = detail.split("\n\n");
  try {
    return [JSON.stringify(JSON.parse(first), null, 2), ...rest].join("\n\n");
  } catch {
    return detail;
  }
}

/**
 * Slow queries, timeouts and server errors this server noted (Settings, admins):
 * where they came from (page, app), how long they took, and the query or error.
 */
export function ProblemsPanel() {
  const [problems, setProblems] = useState<Problem[] | null>(null);
  const [slowMs, setSlowMs] = useState(3000);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);

  const load = useCallback(() => {
    setProblems(null);
    setError(null);
    listProblems()
      .then((result) => {
        setProblems(result.problems);
        setSlowMs(result.slowMs);
      })
      .catch((cause) => {
        setProblems([]);
        setError(cause instanceof ApiClientError ? cause.message : "The problems could not be loaded.");
      });
  }, []);

  useEffect(load, [load]);

  const clear = async () => {
    try {
      await clearProblems();
      setProblems([]);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The problems could not be cleared.");
    }
  };

  return (
    <Panel
      title="Problems"
      description={`Queries slower than ${formatDuration(slowMs)}, timeouts and server errors of the last 90 days. They stay on this server.`}
      actions={
        <>
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="refresh" size={13} />} onClick={load}>
            Refresh
          </Button>
          <Button type="button" variant="ghost" size="sm" disabled={!problems?.length} onClick={() => void clear()}>
            Clear
          </Button>
        </>
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {problems === null ? (
        <Spinner label="Loading problems" />
      ) : problems.length === 0 ? (
        <p className="problems__empty">No problems noted.</p>
      ) : (
        <div className="problems__wrap">
          <table className="problems__table">
            <thead>
              <tr>
                <th>When</th>
                <th>Kind</th>
                <th>Where</th>
                <th>Time</th>
                <th>What</th>
              </tr>
            </thead>
            <tbody>
              {problems.map((problem) => {
                const open = expanded === problem.id;
                const where = problem.source ?? problem.path ?? "—";
                return (
                  <Fragment key={problem.id}>
                    <tr className="problems__row" onClick={() => setExpanded(open ? null : problem.id)}>
                      <td>{formatWhen(problem.loggedAt)}</td>
                      <td>
                        <span className={`problems__kind problems__kind--${problem.kind}`}>{kindLabels[problem.kind]}</span>
                      </td>
                      <td className="problems__where" title={where}>
                        <button
                          type="button"
                          className="problems__toggle"
                          aria-expanded={open}
                          aria-label={`${open ? "Hide" : "Show"} details`}
                          onClick={(event) => {
                            event.stopPropagation();
                            setExpanded(open ? null : problem.id);
                          }}
                        >
                          <Icon name={open ? "chevron-down" : "chevron-right"} size={12} />
                        </button>
                        {where}
                        {problem.via ? <span className="problems__via"> · {problem.via}</span> : null}
                      </td>
                      <td>{formatDuration(problem.durationMs)}</td>
                      <td className="problems__message">{problem.message}</td>
                    </tr>
                    {open ? (
                      <tr className="problems__details">
                        <td colSpan={5}>
                          <dl>
                            <dt>Request</dt>
                            <dd>
                              {problem.method ?? ""} {problem.path ?? ""}
                              {problem.status ? ` → ${problem.status}` : ""}
                              {problem.username ? ` · ${problem.username}` : ""}
                            </dd>
                            <dt>Message</dt>
                            <dd>{problem.message}</dd>
                          </dl>
                          {problem.detail ? <pre>{pretty(problem.detail)}</pre> : null}
                        </td>
                      </tr>
                    ) : null}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}
