export type HealthStatus = "healthy" | "degraded" | "unhealthy";

export type HealthResponse = {
  status: HealthStatus | string;
  database: {
    system_pool: string;
    data_pool: string;
  };
  version?: string;
  error?: string | null;
};

export type HealthDetailsResponse = HealthResponse & {
  runtime?: {
    database_provider?: string;
    auth_mode?: string;
    log_level?: string;
    data_dir?: string;
    config_path?: string;
    jwt_configured?: boolean;
  };
  uptime?: string;
};

/**
 * The snapshot types mirror the backend Model JSON contract. They are kept
 * intentionally loose (unknown for the rarer fields) so the schema designer
 * slice can tighten them later without blocking the shell on exact parity.
 */
export type ModelColumnType =
  | "int64"
  | "string"
  | "boolean"
  | "double"
  | "date"
  | "datetime"
  | "json"
  | "enum"
  | "relation"
  | string;

export type ModelColumn = {
  name: string;
  displayName?: string | null;
  description?: string | null;
  type: ModelColumnType;
  nullable?: boolean;
  unique?: boolean;
  primaryKey?: boolean;
  default?: unknown;
  values?: string[];
  references?: unknown;
  formula?: unknown;
  relationName?: string | null;
  renamedFrom?: string | null;
};

export type ModelIndex = {
  name?: string;
  columns: string[];
  unique?: boolean;
};

export type ModelTable = {
  name: string;
  displayName?: string | null;
  description?: string | null;
  renamedFrom?: string | null;
  columns: ModelColumn[];
  indexes?: ModelIndex[];
};

export type ModelRelation = {
  name: string;
  type?: string;
  fromTable?: string;
  toTable?: string;
  [key: string]: unknown;
};

export type ModelSnapshot = {
  version: number;
  tables: ModelTable[];
  relations?: ModelRelation[];
};
