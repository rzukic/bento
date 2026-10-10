import "@testing-library/jest-dom/vitest";

// React Router's data routers (including createMemoryRouter) construct a
// `new Request(url, { signal })` on every navigation. Under vitest's jsdom
// environment the `AbortSignal` Node hands to the router is from jsdom's
// realm, while the `Request` constructor we get is undici's, and undici
// strictly refuses signals from other realms. We do not care about abort
// propagation in tests, so wrap the global Request to silently strip the
// signal before delegating to the real constructor.
{
  const OriginalRequest = globalThis.Request;
  if (OriginalRequest) {
    class PatchedRequest extends OriginalRequest {
      constructor(input: RequestInfo | URL, init?: RequestInit) {
        if (init && "signal" in init) {
          const { signal: _ignored, ...rest } = init;
          super(input, rest);
        } else {
          super(input, init);
        }
      }
    }
    globalThis.Request = PatchedRequest as unknown as typeof Request;
  }
}

// Query results are kept in the browser between renders (src/api/queryCache.ts);
// each test starts without them. Not imported here: loading app modules in the
// setup would come before the tests' vi.mock calls.
import { afterEach } from "vitest";

afterEach(() => (globalThis as { __bentoClearQueryCache?: () => void }).__bentoClearQueryCache?.());
