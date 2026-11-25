// Anchor server time to a monotonic clock so changing the device clock cannot move a deadline.
export function createClock(now = () => performance.now()) {
  let serverTime = Date.now();
  let anchor = now();
  return {
    sync(utc, roundTripMs = 0) {
      const parsed = Date.parse(utc);
      if (!Number.isFinite(parsed)) return;
      serverTime = parsed + Math.max(0, roundTripMs) / 2;
      anchor = now();
    },
    remaining(deadline) {
      return Math.max(0, Date.parse(deadline) - (serverTime + now() - anchor));
    },
  };
}
