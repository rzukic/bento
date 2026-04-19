import type { ReactElement } from "react";
import type { ModelColumn, ModelTable } from "../../../types/api";
import type {
  ExplorerViewConfig,
  ExplorerViewType,
} from "../../../types/explorer";
import { KanbanView } from "./KanbanView";
import { DataGrid } from "./DataGrid";

/*
 * ViewRenderer — view-type plugin routing layer.
 *
 * This component is a thin router that picks which view component to render
 * based on the active saved view's `type`. It contains NO view-specific logic;
 * all visual behaviour lives in the view components themselves.
 *
 * How to add a new view type:
 *   1. Add the new literal to `ExplorerViewType` (and a matching config shape)
 *      in `types/explorer.ts`.
 *   2. Build the new component in `features/data-explorer/components/`.
 *   3. Register it in `VIEW_TYPE_REGISTRY` below, keyed by the type string.
 *      The renderer entry receives the shared `CommonViewProps` plus the
 *      active `viewConfig` (discriminated by `config.type`) so it can read
 *      type-specific settings without the caller needing to know the shape.
 *   4. If the view needs callers to supply extra props (e.g. handlers that
 *      don't belong in `CommonViewProps`), thread them through explicitly.
 *
 * Unknown types fall back to the table grid and log a warning so saved views
 * created by a future version don't crash older deployments.
 */

export type CommonViewProps = {
  table: ModelTable;
  rows: Record<string, unknown>[];
  page: number;
  perPage: number;
  loading?: boolean;
  pendingNewRowIds?: string[];
  rowErrors?: Record<string, string>;
  selectedRowIds?: string[];
  onAddRow?: () => void;
  onDeleteRowsShortcut?: (focusedRowId: string | null) => void;
  onOpenRecord?: (row: Record<string, unknown>) => void;
  onRequestFilterColumn?: (column: ModelColumn) => void;
  onRequestSortColumn?: (column: ModelColumn) => void;
  onRequestHideColumn?: (column: ModelColumn) => void;
  onNavigateReference?: (
    column: ModelColumn,
    value: unknown,
    row: Record<string, unknown>,
  ) => void;
  onToggleRowSelection?: (row: Record<string, unknown>) => void;
  onToggleSelectAllRows?: () => void;
  onSetSelectedRowIds?: (rowIds: string[]) => void;
  onClearSelection?: () => void;
  selectionMenuItems?: Array<{
    id: string;
    label: string;
    disabled?: boolean;
    onSelect: () => void;
  }>;
  onConfigureView?: () => void;
  // ── Inline edit mode (TASK-092) ──
  editingCell?: { rowId: string; columnName: string } | null;
  cellErrors?: Record<string, Record<string, string>>;
  onStartEdit?: (
    row: Record<string, unknown>,
    column: ModelColumn,
  ) => void;
  onCancelEdit?: () => void;
  onCommitEdit?: (
    row: Record<string, unknown>,
    column: ModelColumn,
    nextValue: unknown,
  ) => void;
};

type ViewRendererProps = CommonViewProps & {
  type: ExplorerViewType | string;
  viewConfig?: ExplorerViewConfig | null;
};

type RendererEntry = (
  props: CommonViewProps & { viewConfig: ExplorerViewConfig | null },
) => ReactElement;

// View-type registry. Each entry is a small wrapper that forwards the shared
// props and pulls any type-specific bits off `viewConfig`. Add new view types
// here (see file comment).
export const VIEW_TYPE_REGISTRY: Partial<Record<ExplorerViewType, RendererEntry>> = {
  table: ({ viewConfig: _viewConfig, ...rest }) => <DataGrid {...rest} />, // onOpenRecord flows via ...rest
  kanban: ({
    viewConfig,
    table,
    rows,
    loading,
    onConfigureView,
    onNavigateReference,
    onOpenRecord,
  }) => {
    const kanbanColumn =
      viewConfig && viewConfig.type === "kanban" ? viewConfig.kanbanColumn : null;
    const cardFieldNames =
      viewConfig && viewConfig.type === "kanban" ? viewConfig.cardFieldNames : [];
    const hiddenLaneValues =
      viewConfig && viewConfig.type === "kanban" ? (viewConfig.hiddenLaneValues ?? []) : [];
    return (
      <KanbanView
        table={table}
        rows={rows}
        loading={loading}
        kanbanColumn={kanbanColumn}
        cardFieldNames={cardFieldNames}
        hiddenLaneValues={hiddenLaneValues}
        onConfigureView={onConfigureView}
        onNavigateReference={onNavigateReference}
        onOpenRecord={onOpenRecord}
      />
    );
  },
};

const warnedTypes = new Set<string>();

export function ViewRenderer({
  type,
  viewConfig = null,
  ...rest
}: ViewRendererProps) {
  const entry =
    (VIEW_TYPE_REGISTRY as Record<string, RendererEntry>)[type] ?? null;

  if (!entry) {
    if (!warnedTypes.has(type)) {
      warnedTypes.add(type);
      // eslint-disable-next-line no-console
      console.warn(
        `[ViewRenderer] Unknown view type "${type}" — falling back to table grid.`,
      );
    }
    return <DataGrid {...rest} />;
  }

  return entry({ ...rest, viewConfig });
}
