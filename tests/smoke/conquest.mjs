// End-to-end smoke test for Conquest (M4-A): grab + adjacent pick, battle (nominate -> confirm -> duel),
// then Lobby -> ... -> Results with GameFinished.
// Usage: npm install (in tests/smoke), start the server, then: node conquest.mjs [baseUrl]
//   env: BASE_URL, ADMIN_USER, ADMIN_PASS
import * as signalR from '@microsoft/signalr';

const BASE = process.argv[2] ?? process.env.BASE_URL ?? 'http://localhost:5084';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin';
const Kind = { BaseOrder: 0, Grab: 1, Battle: 2 };
const Phase = { Lobby: 0, BaseSelection: 1, Conquest: 2, Results: 3 };

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

const EVENTS = ['RoomState', 'PlayerConnectionChanged', 'QuestionStarted', 'BattleAnnounced', 'BattleNominated',
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
  const waitFor = (name, pred = () => true, ms = 6000) => {
    const match = (e) => e.name === name && pred(e.payload);
    const past = events.find(match);
    if (past) return Promise.resolve(past);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { const i = waiters.indexOf(w); if (i >= 0) waiters.splice(i, 1); reject(new Error(`timeout waiting for ${name}`)); }, ms);
      const w = { match, resolve: (evt) => { clearTimeout(timer); resolve(evt); } };
      waiters.push(w);
    });
  };
  const rejects = async (method, ...args) => { try { await conn.invoke(method, ...args); return null; } catch (e) { return String(e.message ?? e); } };
  return { conn, events, waitFor, rejects };
}
const has = (text, code) => typeof text === 'string' && text.includes(code);

try {
  const gmToken = (await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: ADMIN_PASS } })).body.token;
  check(!!gmToken, 'host logs in');

  const catId = (await api('/api/categories', { method: 'POST', token: gmToken, body: { name: `conquest-smoke-${Date.now()}` } })).body.id;
  const mc = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 0, text: 'Conquest MC', difficulty: 1, timeLimitSeconds: 10,
    options: [{ label: 'A', text: 'a', isCorrect: false }, { label: 'B', text: 'b', isCorrect: true }] } });
  const est = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 1, text: 'Conquest estimation', difficulty: 1, timeLimitSeconds: 10, options: [], correctNumber: 100, unit: 'm' } });
  check(mc.status === 201 && est.status === 201, 'create test questions');
  const DEMO_MC = mc.body.id, DEMO_EST = est.body.id;

  const code = (await api('/api/rooms', { method: 'POST', token: gmToken, body: { settings: { pickTimeoutSeconds: 3 } } })).body.roomCode;
  const host = connectHub(gmToken);
  await host.conn.start();
  await host.conn.invoke('HostAttach', code);
  await host.waitFor('RoomState');

  async function addPlayer(name) {
    const join = await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name } });
    const hub = connectHub(join.body.token);
    await hub.conn.start();
    await hub.waitFor('RoomState');
    return { name, id: join.body.player.id, token: join.body.token, hub };
  }
  const anna = await addPlayer('Anna');
  const bela = await addPlayer('Bela');
  const cili = await addPlayer('Cili');
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Cili' && p.connected);
  const byId = { [anna.id]: anna, [bela.id]: bela, [cili.id]: cili };

  // Lobby -> BaseSelection -> base order -> picks -> Conquest.
  await host.conn.invoke('StartGame');
  await host.waitFor('PhaseChanged', (p) => p === Phase.BaseSelection);
  await host.conn.invoke('SendQuestion', { questionId: DEMO_EST, kind: Kind.BaseOrder });
  const baseQ = (await anna.hub.waitFor('QuestionStarted', (q) => q.type === 1)).payload;
  await anna.hub.conn.invoke('SubmitAnswer', { roundId: baseQ.roundId, number: 100 });
  await bela.hub.conn.invoke('SubmitAnswer', { roundId: baseQ.roundId, number: 101 });
  await cili.hub.conn.invoke('SubmitAnswer', { roundId: baseQ.roundId, number: 102 });
  const baseClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === baseQ.roundId)).payload;
  for (const pid of baseClosed.winnerPlayerIds) {
    const who = byId[pid];
    const req = (await who.hub.waitFor('PickRequested')).payload;
    await who.hub.conn.invoke('PickTerritory', req.validTerritoryIndices[0]);
    await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.ownerPlayerId === pid && c.isBase));
  }
  await host.conn.invoke('AdvancePhase', Phase.Conquest);
  await host.waitFor('PhaseChanged', (p) => p === Phase.Conquest);
  check(true, 'reached Conquest with bases assigned');

  // Grab: Anna answers correctly first -> picks a free adjacent cell.
  for (const x of [host, anna.hub, bela.hub, cili.hub]) x.events.length = 0;
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  const grabQ = (await anna.hub.waitFor('QuestionStarted', (q) => q.type === 0)).payload;
  await anna.hub.conn.invoke('SubmitAnswer', { roundId: grabQ.roundId, option: 'B' });
  const grabClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === grabQ.roundId)).payload;
  check(grabClosed.kind === Kind.Grab && grabClosed.winnerPlayerIds[0] === anna.id, 'grab: fastest correct player wins');
  const grabPick = (await anna.hub.waitFor('PickRequested')).payload;
  await bela.hub.waitFor('PickWaiting', (p) => p.id === anna.id);
  const grabCell = grabPick.validTerritoryIndices[0];
  await anna.hub.conn.invoke('PickTerritory', grabCell);
  const grabbed = (await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.index === grabCell))).payload;
  const grabbedCell = grabbed.changedTerritories.find((c) => c.index === grabCell);
  check(grabbedCell.ownerPlayerId === anna.id && grabbedCell.isBase === false, 'grabbed cell belongs to the winner and is not a base');

  // Battle: find an enemy cell adjacent to a cell Anna owns, nominate it, host confirms.
  const mapSnap = (await api(`/api/rooms/${code}`, { token: gmToken })).body.map;
  const cols = mapSnap.cols;
  const owner = new Map(mapSnap.territories.map((t) => [t.index, t.ownerPlayerId]));
  const neighbours = (i) => {
    const r = Math.floor(i / cols), c = i % cols, out = [];
    if (r > 0) out.push(i - cols); if (c > 0) out.push(i - 1);
    if (c < cols - 1) out.push(i + 1); if (r < mapSnap.rows - 1) out.push(i + cols);
    return out;
  };
  let battleTarget = null;
  for (const t of mapSnap.territories) {
    if (t.ownerPlayerId && t.ownerPlayerId !== anna.id && neighbours(t.index).some((n) => owner.get(n) === anna.id)) { battleTarget = t; break; }
  }
  check(battleTarget !== null, 'an enemy territory adjacent to Anna exists to battle for');
  const defenderId = battleTarget.ownerPlayerId;
  const defender = byId[defenderId];

  check(has(await host.rejects('ConfirmBattle', { questionId: DEMO_EST }), 'NoNomination'), 'confirm without a nomination -> NoNomination');
  await anna.hub.conn.invoke('NominateTarget', battleTarget.index);
  const nominated = (await host.waitFor('BattleNominated')).payload;
  check(nominated.attackerId === anna.id && nominated.defenderId === defenderId && nominated.targetTerritoryIndex === battleTarget.index,
    'host is told who nominated which target');

  host.events.length = 0;
  await host.conn.invoke('ConfirmBattle', { questionId: DEMO_EST });
  const battleHostQ = (await host.waitFor('QuestionStarted')).payload;
  const battleQ = (await anna.hub.waitFor('QuestionStarted', (q) => q.roundId === battleHostQ.roundId)).payload;
  check(battleQ.type === 1, 'confirmed battle opens an estimation duel');
  const spectator = [anna, bela, cili].find((p) => p.id !== anna.id && p.id !== defenderId);
  await spectator.hub.waitFor('BattleAnnounced', (b) => b.targetTerritoryIndex === battleTarget.index);
  check(true, 'non-participants receive BattleAnnounced');
  check(has(await spectator.hub.rejects('SubmitAnswer', { roundId: battleQ.roundId, number: 100 }), 'NotParticipant'), 'a spectator cannot answer a battle');

  await anna.hub.conn.invoke('SubmitAnswer', { roundId: battleQ.roundId, number: 100 }); // exact
  await defender.hub.conn.invoke('SubmitAnswer', { roundId: battleQ.roundId, number: 1 });
  const battleClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === battleQ.roundId)).payload;
  check(battleClosed.kind === Kind.Battle && battleClosed.winnerPlayerIds[0] === anna.id, 'battle winner = closest guess');
  const transfer = (await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.index === battleTarget.index))).payload;
  check(transfer.changedTerritories.find((c) => c.index === battleTarget.index).ownerPlayerId === anna.id, 'battle hands the territory to the winner');

  // End the game.
  await host.conn.invoke('AdvancePhase', Phase.Results);
  await host.waitFor('PhaseChanged', (p) => p === Phase.Results);
  const finished = (await host.waitFor('GameFinished')).payload;
  check(finished.standings.length === 3, 'GameFinished lists every player');
  check(finished.standings[0].rank === 1 && finished.standings.every((s, i) => i === 0 || s.rank >= finished.standings[i - 1].rank), 'standings are ranked best first');
  check(finished.standings.find((s) => s.playerId === anna.id).score >= grabbedCell.value, 'the leader score reflects owned territories');

  await Promise.allSettled([host.conn.stop(), anna.hub.conn.stop(), bela.hub.conn.stop(), cili.hub.conn.stop()]);
} catch (err) {
  console.error('ERROR', err);
  failures++;
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
