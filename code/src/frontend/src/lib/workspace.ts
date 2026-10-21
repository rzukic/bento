// ── Workspaces (Step 5.5): the area of the installation this page works in ──
//
// The address names it: /w/<slug>/… for a workspace, no prefix for the
// default one (so links from before workspaces keep working). It is read once
// when the page loads; switching workspaces loads the page anew, so every
// store and cached result starts clean.

const SLUG = /^\/w\/([a-z][a-z0-9-]{1,39})(?=\/|$)/;

function slugFromLocation(): string | null {
  if (typeof window === "undefined") return null;
  return SLUG.exec(window.location.pathname)?.[1] ?? null;
}

let current: string | null = slugFromLocation();

/** The workspace's short name, or null for the default workspace. */
export function currentWorkspaceSlug(): string | null {
  return current;
}

/** The router's basename: /w/<slug>, or undefined for the default workspace. */
export function routerBasename(): string | undefined {
  return current ? `/w/${current}` : undefined;
}

/** The address of a workspace's home page. */
export function workspaceHref(workspace: { slug: string; isDefault: boolean }): string {
  return workspace.isDefault ? "/" : `/w/${workspace.slug}/`;
}

/** An API address of the current workspace, for links the browser opens itself (downloads). */
export function workspaceApiPath(path: string): string {
  return current ? `/api/v1/w/${current}${path}` : `/api/v1${path}`;
}

/** Tests: pretend the page was loaded in another workspace. */
export function setCurrentWorkspaceForTests(slug: string | null): void {
  current = slug;
}
