// End-to-end smoke test for the full game (plan §4.2): BaseSelection → Conquest → Expansion → Duel → Results.
// Needs @microsoft/signalr (npm install in tests/smoke). Usage: start the server, then: node conquest.mjs [baseUrl]
import * as signalR from '@microsoft/signalr';

const BASE = process.argv[2] ?? process.env.BASE_URL ?? 'http://localhost:5084';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin';
const Phase = { Lobby: 0, BaseSelection: 1, Conquest: 2, Expansion: 3, Duel: 4, Results: 5 };
const Purpose = { Base: 0, ConquestClaim: 1, Expansion: 2, DuelTarget: 3 };

let failures = 0;
const check = (cond, label) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${label}`); if (!cond) failures++; };

async function api(path, { method = 'GET', body, token } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: { ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let json = null; try { json = await res.json(); } catch { /* no body */ }
  return { status: res.status, body: json };
}

const EVENTS = ['RoomState', 'PlayerConnectionChanged', 'QuestionStarted', 'DuelAnnounced', 'DuelNominated',
  'AnswerCountChanged', 'RoundClosed', 'PhaseChanged', 'MapUpdated', 'PickRequested', 'PickWaiting', 'GameFinished', 'Error'];

function connectHub(token) {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/game`, { accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.None).build();
  const events = [];
  const waiters = [];
  for (const name of EVENTS) {
    conn.on(name, (payload) => {
      const evt = { name, payload };
      events.push(evt);
      for (const w of [...waiters]) if (w.match(evt)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(evt); }
    });
  }
  const waitFor = (name, pred = () => true, ms = 8000) => {
    const match = (e) => e.name === name && pred(e.payload);
    const past = events.find(match);
    if (past) return Promise.resolve(past);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { const i = waiters.indexOf(w); if (i >= 0) waiters.splice(i, 1); reject(new Error(`timeout waiting for ${name}`)); }, ms);
      const w = { match, resolve: (evt) => { clearTimeout(timer); resolve(evt); } };
      waiters.push(w);
    });
  };
  return { conn, events, waitFor };
}

const neighbours = (rows, cols, i) => {
  const r = Math.floor(i / cols), c = i % cols, out = [];
  if (r > 0) out.push(i - cols); if (c > 0) out.push(i - 1);
  if (c < cols - 1) out.push(i + 1); if (r < rows - 1) out.push(i + cols);
  return out;
};

try {
  const gmToken = (await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: ADMIN_PASS } })).body.token;
  check(!!gmToken, 'host logs in');

  const catId = (await api('/api/categories', { method: 'POST', token: gmToken, body: { name: `full-smoke-${Date.now()}` } })).body.id;
  const mc = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 0, text: 'MC', difficulty: 1, timeLimitSeconds: 30,
    options: [{ label: 'A', text: 'a', isCorrect: false }, { label: 'B', text: 'b', isCorrect: true }] } });
  const est = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 1, text: 'EST', difficulty: 1, timeLimitSeconds: 30, options: [], correctNumber: 100, unit: 'm' } });
  check(mc.status === 201 && est.status === 201, 'create questions');
  const MC = mc.body.id, EST = est.body.id;

  // Small map + few rounds keeps the smoke quick.
  const code = (await api('/api/rooms', { method: 'POST', token: gmToken, body: {
    mapPreset: 0, pickTimeoutSeconds: 3, conquestRounds: 1, duelRounds: 1, baseHealth: 2 } })).body.roomCode;

  const host = connectHub(gmToken);
  await host.conn.start();
  await host.conn.invoke('HostAttach', code);
  await host.waitFor('RoomState');

  async function addPlayer(name) {
    const join = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name } });
    const hub = connectHub(join.body.token);
    await hub.conn.start();
    await hub.waitFor('RoomState');
    return { name, id: join.body.player.id, hub };
  }
  const players = [await addPlayer('Anna'), await addPlayer('Bela'), await addPlayer('Cili')];
  const byId = Object.fromEntries(players.map((p) => [p.id, p]));
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Cili' && p.connected);

  // ---- BaseSelection: each player picks a base in turn order ----
  await host.conn.invoke('StartGame');
  await host.waitFor('PhaseChanged', (p) => p === Phase.BaseSelection);

  async function resolvePicks(expectedPurpose) {
    // Keep answering PickRequested until nobody is asked.
    for (;;) {
      // Poll each player's latest unhandled PickRequested.
      let acted = false;
      for (const p of players) {
        const evt = p.hub.events.find((e) => e.name === 'PickRequested' && !e.done);
        if (!evt) continue;
        evt.done = true;
        if (expectedPurpose !== undefined) check(evt.payload.purpose === expectedPurpose, `pick purpose ${expectedPurpose}`);
        await p.hub.conn.invoke('PickTerritory', evt.payload.validTerritoryIndices[0]);
        acted = true;
      }
      if (!acted) break;
      await new Promise((r) => setTimeout(r, 120));
    }
  }

  // Wait for the first base PickRequested then resolve all base picks.
  await Promise.race(players.map((p) => p.hub.waitFor('PickRequested')));
  await resolvePicks(Purpose.Base);
  check(true, 'all bases picked');

  await host.waitFor('PhaseChanged', (p) => p === Phase.Conquest);

  // ---- Conquest: claim stage + one multiple-choice question ----
  for (const p of players) p.hub.events.length = 0;
  await host.conn.invoke('SendQuestion', { questionId: MC });
  await Promise.race(players.map((p) => p.hub.waitFor('PickRequested', (r) => r.purpose === Purpose.ConquestClaim)));
  await resolvePicks(Purpose.ConquestClaim);

  const cq = (await players[0].hub.waitFor('QuestionStarted')).payload;
  for (const p of players) await p.hub.conn.invoke('SubmitAnswer', { roundId: cq.roundId, option: 'B' }); // all right
  const cqClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === cq.roundId)).payload;
  check(cqClosed.kind === 0, 'conquest round closed');
  check(cqClosed.winnerPlayerIds.length === 3, 'every correct claimer kept their tile');

  await host.waitFor('PhaseChanged', (p) => p === Phase.Expansion);

  // ---- Expansion: estimation rounds until the map is full ----
  for (let guard = 0; guard < 60; guard++) {
    const snap = (await api(`/api/rooms/${code}`, { token: gmToken })).body;
    if (!snap.map.territories.some((t) => t.ownerPlayerId === null)) break;
    for (const p of players) p.hub.events.length = 0;
    host.events.length = 0;
    await host.conn.invoke('SendQuestion', { questionId: EST });
    const eq = (await players[0].hub.waitFor('QuestionStarted', (q) => q.type === 1)).payload;
    let guess = 100;
    for (const p of players) await p.hub.conn.invoke('SubmitAnswer', { roundId: eq.roundId, number: guess++ });
    await host.waitFor('RoundClosed', (r) => r.roundId === eq.roundId);
    // The closest guess is asked to pick.
    await Promise.race(players.map((p) => p.hub.waitFor('PickRequested', (r) => r.purpose === Purpose.Expansion)));
    const picker = players.find((p) => p.hub.events.some((e) => e.name === 'PickRequested' && e.payload.purpose === Purpose.Expansion));
    if (picker) {
      const req = picker.hub.events.find((e) => e.name === 'PickRequested' && e.payload.purpose === Purpose.Expansion).payload;
      await picker.hub.conn.invoke('PickTerritory', req.validTerritoryIndices[0]);
      await host.waitFor('MapUpdated');
    }
  }
  const full = (await api(`/api/rooms/${code}`, { token: gmToken })).body.map;
  check(!full.territories.some((t) => t.ownerPlayerId === null), 'Expansion filled the whole map');

  await host.waitFor('PhaseChanged', (p) => p === Phase.Duel);

  // ---- Duel: active player nominates, host confirms, attacker wins ----
  for (const p of players) p.hub.events.length = 0;
  host.events.length = 0;
  const snap = (await api(`/api/rooms/${code}`, { token: gmToken })).body;
  const attackerId = snap.turnOrder[0];
  const attacker = byId[attackerId];
  const owner = new Map(snap.map.territories.map((t) => [t.index, t.ownerPlayerId]));
  const target = snap.map.territories.find((t) => !t.isBase && t.ownerPlayerId && t.ownerPlayerId !== attackerId
    && neighbours(snap.map.rows, snap.map.cols, t.index).some((n) => owner.get(n) === attackerId))
    ?? snap.map.territories.find((t) => t.ownerPlayerId && t.ownerPlayerId !== attackerId
    && neighbours(snap.map.rows, snap.map.cols, t.index).some((n) => owner.get(n) === attackerId));
  check(!!target, 'found an adjacent enemy tile to attack');
  const defender = byId[target.ownerPlayerId];

  await attacker.hub.conn.invoke('NominateTarget', target.index);
  const nominated = (await host.waitFor('DuelNominated')).payload;
  check(nominated.attackerId === attackerId && nominated.targetTerritoryIndex === target.index, 'host notified of the nomination');

  await host.conn.invoke('ConfirmDuel', { questionId: MC });
  const dq = (await attacker.hub.waitFor('QuestionStarted')).payload;
  const spectator = players.find((p) => p.id !== attackerId && p.id !== defender.id);
  await spectator.hub.waitFor('DuelAnnounced', (d) => d.targetTerritoryIndex === target.index);
  check(true, 'spectator got DuelAnnounced');

  await attacker.hub.conn.invoke('SubmitAnswer', { roundId: dq.roundId, option: 'B' });   // attacker right
  await defender.hub.conn.invoke('SubmitAnswer', { roundId: dq.roundId, option: 'A' });    // defender wrong
  const dClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === dq.roundId)).payload;
  check(dClosed.kind === 2, 'duel closed');
  const after = (await api(`/api/rooms/${code}`, { token: gmToken })).body.map.territories.find((t) => t.index === target.index);
  check(after.ownerPlayerId === attackerId, 'attacker captured the (normal) tile');

  // ---- Results ----
  await host.waitFor('PhaseChanged', (p) => p === Phase.Results);
  const finished = (await host.waitFor('GameFinished')).payload;
  check(finished.standings.length === 3 && finished.standings[0].rank === 1, 'GameFinished has ranked standings');

  check((await api(`/api/rooms/${code}`, { method: 'DELETE', token: gmToken })).status === 204, 'room closes -> 204');
  await Promise.allSettled([host.conn.stop(), ...players.map((p) => p.hub.conn.stop())]);
} catch (err) {
  console.error('ERROR', err);
  failures++;
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
