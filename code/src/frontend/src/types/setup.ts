/**
 * Mirrors `SetupStatusResponse` from the backend: a public, secret-free
 * description of the installation used by the first-run wizard.
 */
export type SetupStatus = {
  /** `true` once setup is no longer needed (open auth mode, or at least one user exists). */
  initialized: boolean;
  /** `"standard"` or `"open"`, lower-cased by the backend. */
  auth_mode: string;
  auth_required: boolean;
  /** Whether `POST /auth/register` still accepts the anonymous bootstrap admin. */
  registration_bootstrap_open: boolean;
  /** `"sqlite"` or `"postgres"`, lower-cased by the backend. */
  database_provider: string;
  schema_version: number;
  /** Whether the one-time demo workspace can still be loaded. */
  sample_data_available: boolean;
  version: string;
  /** How the workspace writes numbers and dates (null: each browser's language) and the currency of amounts. */
  locale?: string | null;
  currency?: string;
  /** The workspace's calendar: the time zone of "today" and schedules (the server's when not chosen), week and year start. */
  time_zone?: string;
  time_zone_set?: boolean;
  week_start?: "monday" | "sunday" | "saturday";
  year_start?: number;
};

/** Mirrors `SampleDataResponse`: summary returned after the demo workspace was loaded. */
export type SampleDataResponse = {
  schema_version: number;
  tables_created: string[];
  rows_inserted: Record<string, number>;
};
