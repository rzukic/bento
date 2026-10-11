import { useEffect, useState } from "react";
import { getAiStatus } from "../../api/ai";

let cached: Promise<boolean> | null = null;

/** Whether Bento's AI is set up and on — asked once per page load, for the ✨ actions. */
export function useAiEnabled(): boolean {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    let cancelled = false;
    cached ??= getAiStatus()
      .then((status) => status.enabled && status.configured)
      .catch(() => false);
    void cached.then((value) => !cancelled && setEnabled(value));
    return () => {
      cancelled = true;
    };
  }, []);
  return enabled;
}

/** Ask again next time — after the AI page changed the key or switched the AI on or off. */
export function forgetAiStatus() {
  cached = null;
}
