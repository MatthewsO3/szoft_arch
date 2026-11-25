// Thin fetch wrappers over the REST API (plan §6.2).
export class ApiError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function request(path, { method = 'GET', body, token } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = `Bearer ${token}`;

  let res;
  try {
    res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError(0, 'network', 'Cannot reach the server.');
  }

  if (res.ok) return res.status === 204 ? null : res.json();

  let problem = {};
  try { problem = await res.json(); } catch { /* non-JSON error */ }
  const message = res.status === 429 ? 'Too many attempts, wait a moment.' : (problem.detail || `Request failed (${res.status}).`);
  throw new ApiError(res.status, problem.code || `http_${res.status}`, message);
}

export const roomExists = (code) => request(`/api/rooms/${encodeURIComponent(code)}/exists`);

export const joinRoom = (code, name) =>
  request(`/api/rooms/${encodeURIComponent(code)}/join`, { method: 'POST', body: { name } });

export const rejoinRoom = (code, token) =>
  request(`/api/rooms/${encodeURIComponent(code)}/rejoin`, { method: 'POST', token });
