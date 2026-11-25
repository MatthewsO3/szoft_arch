// Player session persisted in localStorage so a reload or reconnect can rejoin (plan §7.5).
// Storage can throw (private mode, blocked cookies), so every access is guarded.
const KEY = 'quizgame.session';

export function loadSession() {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

export function saveSession(session) {
  try { localStorage.setItem(KEY, JSON.stringify(session)); } catch { /* ignore */ }
}

export function clearSession() {
  try { localStorage.removeItem(KEY); } catch { /* ignore */ }
}
