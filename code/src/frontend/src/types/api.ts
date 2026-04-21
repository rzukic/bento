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
  from?: string;
  to?: string;
  fromAccessor?: string | null;
  toAccessor?: string | null;
  column?: string | null;
  fromColumn?: string | null;
  toColumn?: string | null;
  resolvedFromAccessor?: string | null;
  resolvedToAccessor?: string | null;
  resolvedJunctionTableName?: string | null;
  fromTable?: string;
  toTable?: string;
  [key: string]: unknown;
};

export type ModelSnapshot = {
  version: number;
  tables: ModelTable[];
  relations?: ModelRelation[];
};

/**
 * Step 2.5 workbench types stay close to the backend Model JSON and schema
 * preview/apply contracts. The looser `ModelSnapshot` types above remain in
 * place for existing callers that do not need exact parity yet.
 */
export type SchemaColumnType =
  | "text"
  | "integer"
  | "float"
  | "boolean"
  | "date"
  | "datetime"
  | "json"
  | "reference"
  | "computed"
  | "currency"
  | "enum"
  | "file";

export type SchemaRelationType = "1:N" | "N:M";

export type SchemaChangeType =
  | "AddTable"
  | "DropTable"
  | "RenameTable"
  | "AddColumn"
  | "DropColumn"
  | "RenameColumn"
  | "AlterColumnType"
  | "AlterColumnNullability"
  | "AlterColumnDefault"
  | "AlterColumnUnique"
  | "AddRelation"
  | "DropRelation"
  | "AlterRelation"
  | "AddIndex"
  | "DropIndex"
  | "RenameIndex"
  | "AlterIndex";

export type SchemaCoercionKind = "Identity" | "Safe" | "Lossy" | "Impossible";

export type SchemaIssue = {
  code: string;
  message: string;
  path: string;
};

export type SchemaColumn = {
  name: string;
  displayName?: string | null;
  description?: string | null;
  type: SchemaColumnType;
  nullable?: boolean;
  unique?: boolean;
  primaryKey?: boolean;
  default?: unknown;
  values?: string[];
  references?: string | null;
  formula?: string | null;
  relationName?: string | null;
  renamedFrom?: string | null;
  [key: string]: unknown;
};

export type SchemaIndex = {
  name: string;
  columns: string[];
  unique?: boolean;
  renamedFrom?: string | null;
  [key: string]: unknown;
};

export type SchemaTable = {
  name: string;
  displayName?: string | null;
  description?: string | null;
  renamedFrom?: string | null;
  columns: SchemaColumn[];
  indexes?: SchemaIndex[];
  [key: string]: unknown;
};

export type SchemaRelation = {
  name: string;
  type: SchemaRelationType;
  from: string;
  to: string;
  fromAccessor?: string | null;
  toAccessor?: string | null;
  column?: string | null;
  fromColumn?: string | null;
  toColumn?: string | null;
  resolvedFromAccessor?: string | null;
  resolvedToAccessor?: string | null;
  resolvedJunctionTableName?: string | null;
  [key: string]: unknown;
};

export type SchemaModelDocument = {
  version: number;
  tables: SchemaTable[];
  relations: SchemaRelation[];
  [key: string]: unknown;
};

export type SchemaChange = {
  changeType: SchemaChangeType;
  table: string;
  previousTable?: string | null;
  column?: string | null;
  previousColumn?: string | null;
  relation?: string | null;
  index?: string | null;
  oldValue?: unknown;
  newValue?: unknown;
  coercionKind?: SchemaCoercionKind | null;
  castExamples?: SchemaCastExample[] | null;
  isDestructive: boolean;
};

export type SchemaCastExample = {
  sourceValue?: unknown;
  castValue?: unknown;
  isValid: boolean;
  error?: string | null;
};

export type SchemaPlan = {
  sqlUp: string[];
  sqlDown: string[];
  isDestructive: boolean;
  requiresConfirmation: boolean;
};

export type SchemaPreviewResponse = {
  isValid: boolean;
  hasChanges: boolean;
  isDestructive: boolean;
  requiresConfirmation: boolean;
  currentVersion: number;
  errors: SchemaIssue[];
  warnings: SchemaIssue[];
  changes: SchemaChange[];
  normalizedModel: SchemaModelDocument;
  plan?: SchemaPlan;
};

export type SchemaApplyResponse = {
  applied: boolean;
  version: number;
  changes: SchemaChange[];
  warnings: SchemaIssue[];
};

export type SchemaValidationFailedDetails = {
  errors?: SchemaIssue[];
};

export type SchemaConfirmationRequiredDetails = {
  changes?: SchemaChange[];
};
