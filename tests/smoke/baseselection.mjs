// End-to-end smoke test for phases, the map and base selection (M3-A): Lobby -> BaseSelection -> Conquest -> Results.
// Creates its own question through the question-bank REST API, so it works against any server.
// Usage: node baseselection.mjs [baseUrl]   (env: BASE_URL, ADMIN_USER, ADMIN_PASS)   ~10 s (pick timeout is set to 3 s)
import * as signalR from '@microsoft/signalr';

const BASE = process.argv[2] ?? process.env.BASE_URL ?? 'http://localhost:5084';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin';
const Kind = { BaseOrder: 0, Grab: 1, Battle: 2 };
const Phase = { Lobby: 0, BaseSelection: 1, Conquest: 2, Results: 3 };

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

const EVENTS = ['RoomState', 'PlayerJoined', 'PlayerLeft', 'PlayerConnectionChanged', 'QuestionStarted', 'RoundClosed',
  'PhaseChanged', 'MapUpdated', 'PickRequested', 'PickWaiting', 'GameFinished', 'Error'];

function connectHub(token) {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/game`, { accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.None)
    .build();
  const events = [];
  const waiters = [];
  for (const name of EVENTS) {
    conn.on(name, (payload) => {
      const evt = { name, payload, at: Date.now() };
      events.push(evt);
      for (const w of [...waiters]) if (w.match(evt)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(evt); }
    });
  }
  // Resolves with an event that already arrived or the next one that matches.
  const waitFor = (name, pred = () => true, ms = 6000) => {
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
  const never = async (name, pred = () => true, ms = 400) => {
    await new Promise((r) => setTimeout(r, ms));
    return !events.some((e) => e.name === name && pred(e.payload));
  };
  const rejects = async (method, ...args) => {
    try { await conn.invoke(method, ...args); return null; } catch (e) { return String(e.message ?? e); }
  };
  return { conn, events, waitFor, never, rejects };
}

const has = (text, code) => typeof text === 'string' && text.includes(code);
const settings = (over = {}) => ({
  mapPreset: 0, territoryValue: 100, baseValue: 300, winnersPerRound: 1, minPlayers: 2,
  pickTimeoutSeconds: 3, defaultQuestionSeconds: 20, suggestedMaxRounds: 10, disallowAdjacentBases: false, ...over,
});

try {
  const login = await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: ADMIN_PASS } });
  check(login.status === 200, 'host logs in');
  const gmToken = login.body.token;

  const cat = await api('/api/categories', { method: 'POST', token: gmToken, body: { name: `base-smoke-${Date.now()}` } });
  const est = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: cat.body.id, type: 1, text: 'Smoke estimation', difficulty: 1, timeLimitSeconds: 10, options: [], correctNumber: 8849, unit: 'm' } });
  check(est.status === 201, 'create the base-order question');
  const EST = est.body.id;

  const room = (await api('/api/rooms', { method: 'POST', token: gmToken })).body;
  const code = room.roomCode;
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

  // ---- Lobby ----
  const anna = await addPlayer('Anna');
  check(has(await host.rejects('StartGame'), 'NotEnoughPlayers'), 'one connected player is not enough -> NotEnoughPlayers');
  const bela = await addPlayer('Bela');
  const cili = await addPlayer('Cili');
  const dani = await addPlayer('Dani');
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Dani' && p.connected);
  const all = [anna, bela, cili, dani];
  const byId = Object.fromEntries(all.map((p) => [p.id, p]));

  check((await anna.hub.rejects('StartGame')) !== null, 'a player cannot StartGame');
  check((await anna.hub.rejects('UpdateSettings', settings())) !== null, 'a player cannot UpdateSettings');
  check((await anna.hub.rejects('AdvancePhase', Phase.Conquest)) !== null, 'a player cannot AdvancePhase');
  check(has(await host.rejects('AdvancePhase', Phase.Conquest), 'InvalidTransition'), 'Lobby -> Conquest is skipping a phase -> InvalidTransition');
  check(has(await host.rejects('AdvancePhase', Phase.Lobby), 'InvalidTransition'), 'Lobby -> Lobby -> InvalidTransition');
  check(has(await host.rejects('UpdateSettings', settings({ pickTimeoutSeconds: 0 })), 'InvalidSettings'), 'out-of-range settings -> InvalidSettings');
  check(has(await host.rejects('SendQuestion', { questionId: EST, kind: Kind.BaseOrder }), 'InvalidPhase'), 'BaseOrder in the Lobby -> InvalidPhase');
  check((await host.rejects('UpdateSettings', settings())) === null, 'UpdateSettings in the Lobby works');

  // ---- Start ----
  for (const x of [host, anna.hub, bela.hub, cili.hub, dani.hub]) x.events.length = 0;
  await host.conn.invoke('StartGame');
  for (const x of [host, anna.hub, dani.hub]) await x.waitFor('PhaseChanged', (p) => p === Phase.BaseSelection);
  check(true, 'StartGame -> PhaseChanged(BaseSelection) reaches host and players');
  const hostState = (await host.waitFor('RoomState')).payload;
  const playerState = (await dani.hub.waitFor('RoomState')).payload;
  check(hostState.phase === Phase.BaseSelection && playerState.phase === Phase.BaseSelection, 'everybody gets a fresh RoomState');
  check(hostState.map?.rows === 5 && hostState.map.cols === 5 && hostState.map.territories.length === 25, 'the Small preset gives a 5x5 map');
  check(hostState.map.territories.every((t) => t.ownerPlayerId == null && !t.isBase && t.value === 100), 'all territories are free and worth 100');
  check(playerState.map?.territories.length === 25 && playerState.hostQuestion == null, 'players get the map, no host data');
  const initial = (await host.waitFor('MapUpdated')).payload;
  check(initial.changedTerritories.length === 25 && initial.scoreboard.length === 4, 'MapUpdated carries the whole map and a scoreboard for the first time');
  check(has(await host.rejects('StartGame'), 'InvalidPhase'), 'StartGame twice -> InvalidPhase');
  check(has(await host.rejects('UpdateSettings', settings()), 'InvalidPhase'), 'settings are locked after the start -> InvalidPhase');
  check((await api(`/api/rooms/${code}/join`, { method: 'POST', body: { name: 'Late' } })).status === 409, 'joining after the start is refused (409)');
  check(has(await host.rejects('AdvancePhase', Phase.Conquest), 'BaseSelectionIncomplete'), 'Conquest before any base -> BaseSelectionIncomplete');
  check(has(await host.rejects('SendQuestion', { questionId: EST, kind: Kind.Grab }), 'InvalidPhase'), 'Grab is not a BaseSelection round');

  // ---- Base-order round: Cili closest, Anna next, Bela far off, Dani silent ----
  await host.conn.invoke('SendQuestion', { questionId: EST, kind: Kind.BaseOrder });
  const q = {};
  for (const p of all) q[p.name] = (await p.hub.waitFor('QuestionStarted')).payload;
  await cili.hub.conn.invoke('SubmitAnswer', { roundId: q.Cili.roundId, number: 8849 });
  await anna.hub.conn.invoke('SubmitAnswer', { roundId: q.Anna.roundId, number: 8000 });
  await bela.hub.conn.invoke('SubmitAnswer', { roundId: q.Bela.roundId, number: 100 });
  for (const x of [host, anna.hub, bela.hub, cili.hub, dani.hub]) x.events.length = 0;
  await host.conn.invoke('CloseRound');
  const closed = (await host.waitFor('RoundClosed')).payload;
  check(closed.ranking.map((r) => r.name).join() === 'Cili,Anna,Bela,Dani', `pick order: ${closed.ranking.map((r) => r.name)}`);
  check(closed.winnerPlayerIds.join() === [cili, anna, bela, dani].map((p) => p.id).join(), 'BaseOrder winners = everybody in ranking order');
  check(has(await host.rejects('SendQuestion', { questionId: EST, kind: Kind.BaseOrder }), 'InvalidPhase'), 'the base-order round is played once -> InvalidPhase');

  // ---- Cili picks (and reconnects while it is their turn) ----
  const t0 = Date.now();
  let req = (await cili.hub.waitFor('PickRequested')).payload;
  check(req.validTerritoryIndices.length === 25, 'Cili may choose any of the 25 free territories');
  const left = Date.parse(req.pickDeadlineUtc) - t0;
  check(left > 1500 && left < 3500, `pick deadline is ~3 s ahead (${left} ms)`);
  for (const x of [host, anna, bela, dani]) {
    const e = (await (x.hub ?? x).waitFor('PickWaiting')).payload;
    check(e.name === 'Cili', `${x === host ? 'host' : x.name} is told that Cili is choosing`);
  }
  check(await cili.hub.never('PickWaiting'), 'the picker does not get PickWaiting');
  check(await anna.hub.never('PickRequested'), 'other players do not get PickRequested');

  check(has(await anna.hub.rejects('PickTerritory', 3), 'NotYourPick'), 'a player who is not up -> NotYourPick');
  check(has(await cili.hub.rejects('PickTerritory', 99), 'InvalidPick'), 'territory out of range -> InvalidPick');
  check(has(await cili.hub.rejects('PickTerritory', -1), 'InvalidPick'), 'negative index -> InvalidPick');
  check(has(await host.rejects('AdvancePhase', Phase.Conquest), 'BaseSelectionIncomplete'), 'Conquest while picks are pending -> BaseSelectionIncomplete');

  await cili.hub.conn.stop();
  const cili2 = connectHub(cili.token);
  await cili2.conn.start();
  const re = (await cili2.waitFor('RoomState')).payload;
  check(re.currentPick?.pickerPlayerId === cili.id && re.currentPick.validTerritoryIndices.length === 25, 'reconnect snapshot restores the pick (valid cells for the picker)');
  check((await cili2.waitFor('PickRequested')).payload.validTerritoryIndices.length === 25, 'reconnecting picker gets PickRequested again');
  check(re.players.find((p) => p.id === cili.id).baseRank === 1 && re.players.find((p) => p.id === dani.id).baseRank === 4, 'players carry their base rank');
  const dani2 = connectHub(dani.token);
  await dani2.conn.start();
  const daniRe = (await dani2.waitFor('RoomState')).payload;
  check(daniRe.currentPick?.pickerPlayerId === cili.id && daniRe.currentPick.validTerritoryIndices.length === 0, 'other players see who is picking but not the valid cells');
  check((await dani2.waitFor('PickWaiting')).payload.name === 'Cili', 'reconnecting waiter gets PickWaiting again');
  cili.hub = cili2;

  for (const x of [host, anna.hub, bela.hub, dani2, cili2]) x.events.length = 0;
  await cili2.conn.invoke('PickTerritory', 12);
  for (const x of [host, anna.hub, bela.hub, dani2, cili2]) {
    const u = (await x.waitFor('MapUpdated')).payload;
    const cell = u.changedTerritories.find((c) => c.index === 12);
    check(cell?.ownerPlayerId === cili.id && cell.isBase && cell.value === 300, 'MapUpdated: territory 12 is Cili\'s base worth 300');
    check(u.scoreboard.find((s) => s.playerId === cili.id)?.score === 300, 'scoreboard shows Cili at 300');
    break; // the first receiver is enough for the payload; delivery to all is checked below
  }
  for (const x of [anna.hub, bela.hub, dani2, cili2]) await x.waitFor('MapUpdated');
  check(true, 'MapUpdated reaches every client');
  check(has(await cili2.rejects('PickTerritory', 13), 'NotYourPick'), 'Cili cannot pick twice');

  // ---- Anna lets the timer run out ----
  const annaReq = (await anna.hub.waitFor('PickRequested')).payload;
  const annaAsked = anna.hub.events.find((e) => e.name === 'PickRequested').at;
  check(!annaReq.validTerritoryIndices.includes(12) && annaReq.validTerritoryIndices.length === 24, 'territory 12 is no longer offered');
  const auto = (await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.ownerPlayerId === anna.id), 8000));
  const waited = auto.at - annaAsked;
  check(waited >= 2500 && waited < 5000, `Anna's pick timed out and a territory was assigned (${waited} ms)`);
  const annaCell = auto.payload.changedTerritories.find((c) => c.ownerPlayerId === anna.id);
  check(annaCell.isBase && annaCell.value === 300 && annaReq.validTerritoryIndices.includes(annaCell.index), 'the random territory was one of the valid ones');
  check(has(await anna.hub.rejects('PickTerritory', 20), 'NotYourPick'), 'picking after the timeout -> NotYourPick');

  // ---- Bela picks, Dani is kicked while it is their turn ----
  const belaReq = (await bela.hub.waitFor('PickRequested')).payload;
  check(belaReq.validTerritoryIndices.length === 23, 'Bela has 23 cells left');
  await bela.hub.conn.invoke('PickTerritory', belaReq.validTerritoryIndices[0]);
  await dani2.waitFor('PickRequested');
  check(true, 'Dani (silent in the base-order round) is asked last');
  await host.conn.invoke('KickPlayer', dani.id);
  await host.waitFor('PlayerLeft', (p) => p.id === dani.id);
  await new Promise((r) => setTimeout(r, 200));
  check((await api(`/api/rooms/${code}`, { token: gmToken })).body.currentPick == null, 'kicking the picker ends base selection (no pick pending)');

  // ---- Conquest, then Results ----
  await host.conn.invoke('AdvancePhase', Phase.Conquest);
  for (const x of [host, anna.hub, bela.hub, cili2]) await x.waitFor('PhaseChanged', (p) => p === Phase.Conquest);
  check(true, 'AdvancePhase(Conquest) -> PhaseChanged for everybody');
  check(has(await host.rejects('AdvancePhase', Phase.BaseSelection), 'InvalidTransition'), 'going back is refused -> InvalidTransition');
  const snap = (await api(`/api/rooms/${code}`, { token: gmToken })).body;
  const bases = snap.map.territories.filter((t) => t.isBase);
  check(bases.length === 3 && new Set(bases.map((b) => b.ownerPlayerId)).size === 3, 'REST snapshot: three distinct bases');
  check(snap.scoreboard.length === 3 && snap.scoreboard.every((s) => s.score === 300 && s.territoryCount === 1), 'REST snapshot: scoreboard 300 each');
  check(snap.players.length === 3 && !snap.players.some((p) => p.id === dani.id), 'kicked player is gone');

  await host.conn.invoke('AdvancePhase', Phase.Results);
  const fin = (await anna.hub.waitFor('GameFinished')).payload;
  await host.waitFor('PhaseChanged', (p) => p === Phase.Results);
  check(fin.standings.length === 3 && fin.standings.every((s) => s.rank === 1 && s.score === 300), 'GameFinished: three-way tie shares rank 1');
  check(fin.finalMap.territories.filter((t) => t.isBase).length === 3, 'GameFinished carries the final map');
  check(has(await host.rejects('AdvancePhase', Phase.Results), 'InvalidTransition'), 'Results is final -> InvalidTransition');

  for (const p of [...all]) await p.hub.conn.stop().catch(() => {});
  await host.conn.stop();
} catch (e) {
  console.error(e);
  failures++;
}

console.log(failures === 0 ? '\nALL CHECKS PASSED' : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
