// End-to-end smoke test: host creates a room, players join with the code, presence, kick, rejoin, close.
// Usage: start the server (dotnet run --project src/QuizGame.Server), then
//   node smoke.mjs [baseUrl]      (defaults: http://localhost:5084, admin/admin)
import * as signalR from '@microsoft/signalr';

const BASE = process.argv[2] ?? process.env.BASE_URL ?? 'http://localhost:5084';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin';

let failures = 0;
const check = (cond, label) => {
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${label}`);
  if (!cond) failures++;
};

async function api(path, { method = 'GET', body, token } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: {
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let json = null;
  try { json = await res.json(); } catch { /* no body */ }
  return { status: res.status, body: json };
}

/** Hub connection that records every event so tests can await them. */
function connectHub(token) {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/game`, { accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.None)
    .build();
  const events = [];
  const waiters = [];
  for (const name of ['RoomState', 'PlayerJoined', 'PlayerLeft', 'PlayerConnectionChanged', 'Kicked', 'RoomClosed', 'Error']) {
    conn.on(name, (payload) => {
      const evt = { name, payload };
      events.push(evt);
      for (const w of [...waiters]) if (w.match(evt)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(evt); }
    });
  }
  // Resolves when an event matching the predicate has occurred (already or in the future).
  const waitFor = (name, pred = () => true, ms = 4000) => {
    const match = (e) => e.name === name && pred(e.payload);
    const past = events.find(match);
    if (past) return Promise.resolve(past);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        const i = waiters.indexOf(w);
        if (i >= 0) waiters.splice(i, 1);
        reject(new Error(`timeout waiting for ${name}`));
      }, ms);
      const w = { match, resolve: (evt) => { clearTimeout(timer); resolve(evt); } };
      waiters.push(w);
    });
  };
  return { conn, events, waitFor };
}

try {
  // ---- Health, login ----
  check((await api('/health')).status === 200, 'GET /health');
  check((await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: 'wrong' } })).status === 401, 'login with wrong password -> 401');
  const login = await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: ADMIN_PASS } });
  check(login.status === 200 && !!login.body.token, 'login -> GM token');
  const gmToken = login.body.token;

  // ---- Room creation ----
  check((await api('/api/rooms', { method: 'POST' })).status === 401, 'create room without token -> 401');
  const created = await api('/api/rooms', { method: 'POST', token: gmToken });
  const code = created.body?.roomCode;
  check(created.status === 201 && /^[1-9]\d{5}$/.test(code), `create room -> 6-digit code (${code})`);
  check(created.body.joinUrl.endsWith(`/?code=${code}`), `joinUrl carries the code (${created.body.joinUrl})`);
  const withSettings = await api('/api/rooms', { method: 'POST', token: gmToken, body: { baseValue: 500, territoryValue: 50 } });
  check(withSettings.status === 201, 'create room with RoomSettings body -> 201');
  check((await api('/api/rooms', { method: 'POST', token: gmToken, body: { baseValue: -1 } })).body?.code === 'InvalidSettings', 'invalid settings -> InvalidSettings');
  const other = await api('/api/rooms', { method: 'POST', token: gmToken });
  check(other.body.roomCode !== code, 'second room gets a different code');

  // ---- Host attaches over SignalR ----
  const host = connectHub(gmToken);
  await host.conn.start();
  await host.conn.invoke('HostAttach', code);
  const hostState = await host.waitFor('RoomState');
  check(hostState.payload.settings.baseValue === 300, 'RoomState carries default RoomSettings');
  check(hostState.payload.roomCode === code && hostState.payload.players.length === 0 && hostState.payload.phase === 0, 'HostAttach -> RoomState (Lobby, 0 players)');

  let foreignRejected = false;
  try { await host.conn.invoke('HostAttach', '000000'); } catch { foreignRejected = true; }
  check(foreignRejected, 'HostAttach to unknown room is rejected');

  // ---- Joining ----
  check((await api(`/api/rooms/${code}/exists`)).body.exists === true, 'exists(code) -> true');
  check((await api('/api/rooms/999999/exists')).body.exists === false, 'exists(unknown) -> false');
  check((await api('/api/rooms/999999/join', { method: 'POST', body: { name: 'X' } })).status === 404, 'join wrong code -> 404');

  const joinA = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: '  Anna ' } });
  check(joinA.status === 200 && joinA.body.player.name === 'Anna' && !!joinA.body.token, 'player joins with code + name (name trimmed)');
  await host.waitFor('PlayerJoined', (p) => p.name === 'Anna');
  check(true, 'host receives PlayerJoined(Anna)');

  const dup = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'ANNA' } });
  check(dup.status === 409 && dup.body.code === 'NameTaken', 'duplicate name (case-insensitive) -> 409 name_taken');
  const bad = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: '   ' } });
  check(bad.status === 400 && bad.body.code === 'InvalidName', 'blank name -> 400 invalid_name');
  const long = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'x'.repeat(21) } });
  check(long.status === 400, '21-char name -> 400');

  const joinB = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'Béla' } });
  check(joinB.status === 200, 'second player joins');
  check(joinA.body.player.colorHex !== joinB.body.player.colorHex, 'players get different colours');

  // ---- Presence ----
  const a = connectHub(joinA.body.token);
  await a.conn.start();
  const aState = await a.waitFor('RoomState');
  check(aState.payload.players.length === 2, 'player A connects -> RoomState lists both players');
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Anna' && p.connected === true);
  check(true, 'host sees Anna connected');

  const b = connectHub(joinB.body.token);
  await b.conn.start();
  await b.waitFor('RoomState');
  await a.waitFor('PlayerConnectionChanged', (p) => p.name === 'Béla' && p.connected);
  check(true, 'player A sees Béla connect (room broadcast)');

  // Player token cannot act as host.
  let playerAsHost = false;
  try { await a.conn.invoke('HostAttach', code); } catch { playerAsHost = true; }
  check(playerAsHost, 'player token cannot call HostAttach');
  let playerKick = false;
  try { await a.conn.invoke('KickPlayer', joinB.body.player.id); } catch { playerKick = true; }
  check(playerKick, 'player token cannot call KickPlayer');
  check((await api(`/api/rooms/${code}`, { token: joinA.body.token })).status === 403, 'player token cannot read host room endpoint -> 403');

  // ---- Disconnect + rejoin ----
  await a.conn.stop();
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Anna' && p.connected === false);
  check(true, 'host sees Anna go offline (not removed)');

  const snapshot = await api(`/api/rooms/${code}`, { token: gmToken });
  check(snapshot.body.players.length === 2 && snapshot.body.players.find((p) => p.name === 'Anna').connected === false, 'GET room (host) still lists offline Anna');

  const rejoin = await api(`/api/rooms/${code}/rejoin`, { method: 'POST', token: joinA.body.token });
  check(rejoin.status === 200 && rejoin.body.player.id === joinA.body.player.id, 'rejoin with stored token -> same player id');
  const wrongRoomRejoin = await api(`/api/rooms/${other.body.roomCode}/rejoin`, { method: 'POST', token: joinA.body.token });
  check(wrongRoomRejoin.status === 404, 'token is bound to its room (rejoin other room -> 404)');

  const a2 = connectHub(rejoin.body.token);
  await a2.conn.start();
  await a2.waitFor('RoomState');
  a2.events.length = 0;
  await a2.conn.invoke('Rejoin');
  await a2.waitFor('RoomState');
  check(true, 'Rejoin hub method replies with RoomState');
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Anna' && p.connected === true && host.events.filter((e) => e.name === 'PlayerConnectionChanged').length >= 3);
  check(true, 'Anna reconnects and host sees her online again');

  // ---- Kick ----
  await host.conn.invoke('KickPlayer', joinB.body.player.id);
  await b.waitFor('Kicked');
  check(true, 'kicked player receives Kicked');
  await host.waitFor('PlayerLeft', (p) => p.id === joinB.body.player.id);
  await a2.waitFor('PlayerLeft', (p) => p.id === joinB.body.player.id);
  check(true, 'room is told PlayerLeft');
  const kickedRejoin = await api(`/api/rooms/${code}/rejoin`, { method: 'POST', token: joinB.body.token });
  check(kickedRejoin.status === 404 && kickedRejoin.body.code === 'PlayerNotFound', 'kicked player cannot rejoin');
  await b.conn.stop();

  // ---- Voluntary leave ----
  const joinC = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'Cili' } });
  const c = connectHub(joinC.body.token);
  await c.conn.start();
  await c.waitFor('RoomState');
  await c.conn.invoke('LeaveRoom');
  await host.waitFor('PlayerLeft', (p) => p.id === joinC.body.player.id);
  check(true, 'LeaveRoom -> host sees PlayerLeft');
  await c.conn.stop();

  // ---- Auth on the hub ----
  const forged = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/game`, { accessTokenFactory: () => joinA.body.token.slice(0, -2) + 'xx' })
    .configureLogging(signalR.LogLevel.None).build();
  let forgedRejected = false;
  try { await forged.start(); } catch { forgedRejected = true; }
  check(forgedRejected, 'hub rejects a token with a bad signature');

  // ---- Close room ----
  check((await api(`/api/rooms/${code}`, { method: 'DELETE', token: gmToken })).status === 204, 'host closes room -> 204');
  await a2.waitFor('RoomClosed');
  check(true, 'players receive RoomClosed');
  check((await api(`/api/rooms/${code}/exists`)).body.exists === false, 'closed room no longer exists');
  check((await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'Late' } })).status === 404, 'join closed room -> 404');

  await Promise.allSettled([host.conn.stop(), a2.conn.stop()]);
} catch (err) {
  console.error('ERROR', err);
  failures++;
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
