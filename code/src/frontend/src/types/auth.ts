export type AuthRole = "admin" | "editor" | "viewer" | "api";

export type AuthUser = {
  id: number;
  username: string;
  /** The role in the workspace the app is open in (Step 5.5). */
  role: AuthRole | string;
  /** The user's own role: `admin` runs the installation (users, workspaces, vault, channels, AI). */
  installation_role?: AuthRole | string;
  /** The role in the open workspace; null when the user is not a member of it. */
  workspace_role?: AuthRole | string | null;
};

/** Runs the installation, in every workspace. A workspace's admin is not one. */
export function isInstallationAdmin(user: AuthUser | null | undefined): boolean {
  return (user?.installation_role ?? user?.role) === "admin";
}

export type LoginRequest = {
  username: string;
  password: string;
};

export type RegisterRequest = {
  username: string;
  password: string;
};

export type RegisterResponse = {
  id: number;
  username: string;
  role: AuthRole | string;
  /** `true` when this registration created the first (admin) account. */
  bootstrap_admin: boolean;
};

export type LoginResponse = {
  access_token: string;
  token_type: string;
  /** ISO 8601 timestamp */
  expires_at: string;
  user: AuthUser;
};

export type ApiErrorBody = {
  error?: {
    code?: string;
    message?: string;
  };
};
