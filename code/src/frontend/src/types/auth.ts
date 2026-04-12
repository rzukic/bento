export type AuthRole = "admin" | "editor" | "viewer" | "api";

export type AuthUser = {
  id: number;
  username: string;
  role: AuthRole | string;
};

export type LoginRequest = {
  username: string;
  password: string;
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
