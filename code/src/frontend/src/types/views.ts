import type { ExplorerViewConfig, ExplorerViewType } from "./explorer";

// ── Persisted view shape (mirrors the backend system_views row) ──────────────

export type SavedView = {
  id: number;
  name: string;
  table_name: string;
  owner_user_id: number;
  type: ExplorerViewType;
  config: ExplorerViewConfig;
  is_shared: boolean;
  created_at: string;
  updated_at: string;
};

// ── API payload shapes ────────────────────────────────────────────────────────

export type CreateViewPayload = {
  name: string;
  table_name: string;
  type: ExplorerViewType;
  config: ExplorerViewConfig;
  is_shared: boolean;
};

/** Only name, config, and is_shared are mutable after creation. */
export type UpdateViewPayload = Partial<Pick<SavedView, "name" | "config" | "is_shared">>;
