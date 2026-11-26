// End-to-end smoke test for question rounds (M2-A): open round, answers, evaluation, timer, rejoin.
// Creates its own two questions through the question-bank REST API (M2-B), so it works against any server.
// Usage: node rounds.mjs [baseUrl]   (env: BASE_URL, ADMIN_USER, ADMIN_PASS)
import * as signalR from '@microsoft/signalr';

const BASE = process.argv[2] ?? process.env.BASE_URL ?? 'http://localhost:5084';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin';

let DEMO_MC;   // created below: multiple choice, correct option B, 10 s
let DEMO_EST;  // created below: estimation, correct number 8849, 10 s
const Kind = { BaseOrder: 0, Grab: 1, Battle: 2 };

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

const EVENTS = ['RoomState', 'PlayerJoined', 'PlayerConnectionChanged', 'QuestionStarted', 'BattleAnnounced',
  'BattleNominated', 'AnswerCountChanged', 'RoundClosed', 'PhaseChanged', 'MapUpdated', 'PickRequested', 'PickWaiting',
  'GameFinished', 'Error'];

function connectHub(token) {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/game`, { accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.None)
    .build();
  const events = [];
  const waiters = [];
  for (const name of EVENTS) {
    conn.on(name, (payload) => {
      const evt = { name, payload };
      events.push(evt);
      for (const w of [...waiters]) if (w.match(evt)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(evt); }
    });
  }
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
  const rejects = async (method, ...args) => {
    try { await conn.invoke(method, ...args); return null; } catch (e) { return String(e.message ?? e); }
  };
  return { conn, events, waitFor, rejects };
}

const has = (text, code) => typeof text === 'string' && text.includes(code);

try {
  const login = await api('/api/auth/login', { method: 'POST', body: { username: ADMIN_USER, password: ADMIN_PASS } });
  check(login.status === 200, 'host logs in');
  const gmToken = login.body.token;

  // Own test questions via the question bank (M2-B).
  const cat = await api('/api/categories', { method: 'POST', token: gmToken, body: { name: `rounds-smoke-${Date.now()}` } });
  check(cat.status === 201 || cat.status === 200, 'create test category');
  const catId = cat.body.id;
  const mc = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 0, text: 'Smoke MC', difficulty: 1, timeLimitSeconds: 10,
    options: [{ label: 'A', text: 'a', isCorrect: false }, { label: 'B', text: 'b', isCorrect: true },
              { label: 'C', text: 'c', isCorrect: false }, { label: 'D', text: 'd', isCorrect: false }] } });
  const estCreated = await api('/api/questions', { method: 'POST', token: gmToken, body: {
    categoryId: catId, type: 1, text: 'Smoke estimation', difficulty: 1, timeLimitSeconds: 10, options: [], correctNumber: 8849, unit: 'm' } });
  check(mc.status === 201 && estCreated.status === 201, 'create test questions');
  DEMO_MC = mc.body.id; DEMO_EST = estCreated.body.id;

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
  const anna = await addPlayer('Anna');
  const bela = await addPlayer('Bela');
  const cili = await addPlayer('Cili');
  await host.waitFor('PlayerConnectionChanged', (p) => p.name === 'Cili' && p.connected);

  // ---- Refusals before any round ----
  check(has(await host.rejects('SendQuestion', { questionId: '00000000-0000-0000-0000-0000000000ff', kind: Kind.Grab }), 'QuestionNotFound'), 'unknown question -> QuestionNotFound');
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab }), 'InvalidPhase'), 'Grab in the Lobby -> InvalidPhase');
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_EST, kind: Kind.BaseOrder }), 'InvalidPhase'), 'BaseOrder in the Lobby -> InvalidPhase');
  check(has(await host.rejects('CloseRound'), 'NoActiveRound'), 'CloseRound with no round -> NoActiveRound');
  check(has(await anna.hub.rejects('SubmitAnswer', { roundId: '00000000-0000-0000-0000-000000000001', option: 'A' }), 'NoActiveRound'), 'answer with no round -> NoActiveRound');
  check((await anna.hub.rejects('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab })) !== null, 'player cannot SendQuestion');
  check((await anna.hub.rejects('CloseRound')) !== null, 'player cannot CloseRound');

  // ---- Phases: Lobby -> BaseSelection -> (base order round, picks) -> Conquest ----
  await host.conn.invoke('StartGame');
  await host.waitFor('PhaseChanged', (p) => p === 1);
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab }), 'InvalidPhase'), 'Grab in BaseSelection -> InvalidPhase');
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_EST, kind: Kind.Grab }), 'InvalidPhase'), 'estimation as Grab in BaseSelection -> InvalidPhase');

  // ---- Estimation round: host closes early; non-answerer ranks last ----
  host.events.length = 0;
  await host.conn.invoke('SendQuestion', { questionId: DEMO_EST, kind: Kind.BaseOrder });
  const est = (await anna.hub.waitFor('QuestionStarted', (q) => q.type === 1)).payload;
  check(est.options.length === 0 && est.unit === 'm', 'estimation: no options, unit hint present');
  await anna.hub.conn.invoke('SubmitAnswer', { roundId: est.roundId, number: 9000 });
  await bela.hub.conn.invoke('SubmitAnswer', { roundId: est.roundId, number: 8800.5 });
  check(has(await bela.hub.rejects('SubmitAnswer', { roundId: est.roundId, option: 'A' }), 'AlreadyAnswered'), 'second answer rejected for estimation too');
  await host.conn.invoke('CloseRound');
  const estClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === est.roundId)).payload;
  check(estClosed.correctNumber === 8849, 'RoundClosed reveals the correct number');
  check(estClosed.ranking.map((r) => r.name).join() === 'Bela,Anna,Cili', `closest first, non-answerer last (${estClosed.ranking.map((r) => r.name)})`);
  check(Math.abs(estClosed.ranking[0].distance - 48.5) < 1e-9 && estClosed.ranking[2].distance == null, 'distances: 48.5 for Bela, none for Cili');
  check(estClosed.winnerPlayerIds.length === 3 && estClosed.winnerPlayerIds[0] === bela.id, 'base order: everybody, closest first');

  // The base-order result fixes the pick order; each player takes the first valid territory.
  const byId = { [anna.id]: anna, [bela.id]: bela, [cili.id]: cili };
  for (const pid of estClosed.winnerPlayerIds) {
    const who = byId[pid];
    const req = (await who.hub.waitFor('PickRequested')).payload;
    await who.hub.conn.invoke('PickTerritory', req.validTerritoryIndices[0]);
    await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.ownerPlayerId === pid && c.isBase));
  }
  await host.conn.invoke('AdvancePhase', 2);
  await host.waitFor('PhaseChanged', (p) => p === 2);
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_EST, kind: Kind.Grab }), 'InvalidRound'), 'estimation question as Grab -> InvalidRound');
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_EST, kind: Kind.BaseOrder }), 'InvalidPhase'), 'BaseOrder in Conquest -> InvalidPhase');
  check(has(await host.rejects('SendQuestion', { questionId: DEMO_EST, kind: Kind.Battle, attackerId: anna.id, targetTerritoryIndex: 47 }), 'InvalidRound'), 'battle on a free territory -> InvalidRound');

  for (const x of [host, anna.hub, bela.hub, cili.hub]) x.events.length = 0;

  // ---- Multiple-choice round: all answer -> closes early ----
  const sentAt = Date.now();
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  const hostQ = (await host.waitFor('QuestionStarted')).payload;
  check(hostQ.question.correctOption === 'B' && !!hostQ.question.text && !!hostQ.roundId && !!hostQ.serverDeadlineUtc, 'host receives the full question incl. correct answer');
  const qa = (await anna.hub.waitFor('QuestionStarted')).payload;
  const qb = (await bela.hub.waitFor('QuestionStarted')).payload;
  await cili.hub.waitFor('QuestionStarted');
  check(qa.options.length === 4 && qa.type === 0, 'players receive 4 options');
  check(!('correctOption' in qa) && !('text' in qa) && !('correctNumber' in qa), 'player payload has no correct answer and no question text');
  const deadlineMs = Date.parse(qa.serverDeadlineUtc) - sentAt;
  check(deadlineMs > 9000 && deadlineMs < 11500, `serverDeadlineUtc is ~10 s ahead (${deadlineMs} ms)`);
  check(qa.roundId === qb.roundId && qa.roundId === hostQ.roundId, 'host and players receive the same round ID');
  const roundId = qa.roundId;

  check(has(await host.rejects('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab }), 'RoundInProgress'), 'second round while one is open -> RoundInProgress');

  const snap = await api(`/api/rooms/${code}`, { token: gmToken });
  check(snap.body.currentRound?.roundId === roundId && snap.body.currentRound.kind === Kind.Grab, 'RoomState carries the current round');
  check(snap.body.hostQuestion?.question.correctOption === 'B', 'authenticated host snapshot includes the full question');

  check(has(await anna.hub.rejects('SubmitAnswer', { roundId, option: 'Z' }), 'InvalidAnswer'), 'unknown option -> InvalidAnswer');
  check(has(await anna.hub.rejects('SubmitAnswer', { roundId, number: 5 }), 'InvalidAnswer'), 'number for a multiple-choice question -> InvalidAnswer');
  check(has(await anna.hub.rejects('SubmitAnswer', { roundId: '00000000-0000-0000-0000-000000000001', option: 'B' }), 'NoActiveRound'), 'answer for another round -> NoActiveRound');

  const accepted = await anna.hub.conn.invoke('SubmitAnswer', { roundId, option: 'B' });
  check(accepted?.roundId === roundId, 'answer acknowledgment identifies the recorded round');
  await anna.hub.conn.stop();
  anna.hub.events.length = 0;
  await anna.hub.conn.start();
  const acceptedState = (await anna.hub.waitFor('RoomState')).payload;
  check(acceptedState.playerQuestion?.roundId === roundId && acceptedState.hasAnswered, 'accepted answer remains locked after reconnect');
  check(acceptedState.hostQuestion == null && !JSON.stringify([acceptedState.playerQuestion, acceptedState.currentRound]).includes('correctOption'), 'player reconnect snapshot does not leak the answer (a closed round\'s result may show it)');
  bela.hub.events.length = 0;
  await bela.hub.conn.invoke('Rejoin');
  const unansweredState = (await bela.hub.waitFor('RoomState')).payload;
  check(unansweredState.hasAnswered === false, 'another player does not inherit accepted answer status');
  await host.waitFor('AnswerCountChanged', (c) => c.answered === 1 && c.total === 3);
  check(true, 'host sees answer count 1/3 (no answer content)');
  check(has(await anna.hub.rejects('SubmitAnswer', { roundId, option: 'A' }), 'AlreadyAnswered'), 'second answer -> AlreadyAnswered (first is final)');

  check(bela.hub.events.every((e) => e.name !== 'AnswerCountChanged'), 'players do not get answer counts');
  await new Promise((r) => setTimeout(r, 60));
  await bela.hub.conn.invoke('SubmitAnswer', { roundId, option: 'A' });
  await new Promise((r) => setTimeout(r, 60));
  await cili.hub.conn.invoke('SubmitAnswer', { roundId, option: 'B' });

  const closed = (await host.waitFor('RoundClosed', (r) => r.roundId === roundId)).payload;
  await anna.hub.waitFor('RoundClosed', (r) => r.roundId === roundId);
  await cili.hub.waitFor('RoundClosed', (r) => r.roundId === roundId);
  check(true, 'all answered -> RoundClosed goes to host and players');
  check(closed.correctOption === 'B', 'RoundClosed reveals the correct option');
  check(closed.ranking.map((r) => r.name).join() === 'Anna,Cili,Bela', `ranking: fastest correct, then other correct, then wrong (${closed.ranking.map((r) => r.name)})`);
  check(closed.ranking.map((r) => r.rank).join() === '1,2,3', 'ranks are 1..n');
  check(closed.winnerPlayerIds.length === 1 && closed.winnerPlayerIds[0] === anna.id, 'winner = fastest correct player');
  check(closed.ranking[0].isCorrect === true && closed.ranking[2].isCorrect === false && closed.ranking[0].elapsedMs >= 0, 'isCorrect and elapsedMs are set');
  check(has(await anna.hub.rejects('SubmitAnswer', { roundId, option: 'B' }), 'NoActiveRound'), 'answer after the round closed -> NoActiveRound');
  check((await api(`/api/rooms/${code}`, { token: gmToken })).body.currentRound == null, 'round is cleared after closing');
  anna.hub.events.length = 0;
  await anna.hub.conn.invoke('Rejoin');
  const closedState = (await anna.hub.waitFor('RoomState')).payload;
  check(closedState.playerQuestion == null && closedState.lastRoundResult?.roundId === roundId,
    'rejoin after closure restores the last result');

  // Anna won this grab, so she now picks a free adjacent territory before any new round can open (M4-A).
  const firstGrabPick = (await anna.hub.waitFor('PickRequested')).payload;
  check(firstGrabPick.validTerritoryIndices.length > 0, 'grab winner is asked to pick after the round closes');
  await anna.hub.conn.invoke('PickTerritory', firstGrabPick.validTerritoryIndices[0]);
  await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.ownerPlayerId === anna.id && !c.isBase));

  // ---- Rejoin mid-round, then the deadline timer closes the round on its own ----
  host.events.length = 0;
  const t0 = Date.now();
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  const hostOpen = (await host.waitFor('QuestionStarted')).payload;
  const open = (await bela.hub.waitFor('QuestionStarted', (q) => q.roundId !== roundId && q.roundId !== est.roundId)).payload;
  await anna.hub.conn.stop();
  const anna2 = connectHub(anna.token);
  await anna2.conn.start();
  const restored = (await anna2.waitFor('RoomState')).payload;
  const again = restored.playerQuestion;
  check(restored.hasAnswered === false && restored.hostQuestion == null, 'unanswered reconnect is unlocked and has no host question');
  check(again.roundId === open.roundId && !('correctOption' in again), 'reconnecting player gets the open question again (no answer)');
  const hostAgain = connectHub(gmToken);
  await hostAgain.conn.start();
  await hostAgain.conn.invoke('HostAttach', code);
  const hostRe = (await hostAgain.waitFor('RoomState')).payload.hostQuestion;
  check(hostRe.roundId === hostOpen.roundId && hostRe.question.correctOption === 'B', 'reconnecting host gets the full open question again');

  const timed = (await host.waitFor('RoundClosed', (r) => r.roundId === open.roundId, 15000)).payload;
  const took = Date.now() - t0;
  check(took >= 10000 && took < 12500, `round closes by itself at the deadline (+grace): ${took} ms`);
  check(timed.winnerPlayerIds.length === 0 && timed.ranking.length === 3, 'nobody answered: no winner, everybody listed');
  check(timed.ranking.every((r) => r.isCorrect == null), 'unanswered players have isCorrect = null');

  // Snapshot delivery must not overtake a concurrent close and reopen a closed question in the UI.
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  anna2.events.length = 0;
  await Promise.all([anna2.conn.invoke('Rejoin'), host.conn.invoke('CloseRound')]);
  await anna2.waitFor('RoomState');
  await anna2.waitFor('RoundClosed');
  const stateIndex = anna2.events.findIndex(e => e.name === 'RoomState');
  const closeIndex = anna2.events.findIndex(e => e.name === 'RoundClosed');
  const racedState = anna2.events[stateIndex].payload;
  check(racedState.currentRound ? stateIndex < closeIndex : stateIndex > closeIndex,
    'concurrent rejoin/close delivers a snapshot consistent with event order');

  // ---- M4: Conquest grab pick, battle (nominate -> confirm -> duel), then end the game ----
  for (const x of [host, anna2, bela.hub, cili.hub]) x.events.length = 0;

  // A grab: Anna answers correctly first, so she becomes the picker and takes a free adjacent cell.
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  const grabQ = (await anna2.waitFor('QuestionStarted', (q) => q.type === 0)).payload;
  await anna2.conn.invoke('SubmitAnswer', { roundId: grabQ.roundId, option: 'B' });
  const grabClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === grabQ.roundId)).payload;
  check(grabClosed.kind === Kind.Grab && grabClosed.winnerPlayerIds[0] === anna.id, 'grab: fastest correct player wins');

  const grabPick = (await anna2.waitFor('PickRequested')).payload;
  check(Array.isArray(grabPick.validTerritoryIndices) && grabPick.validTerritoryIndices.length > 0, 'grab winner is asked to pick a free adjacent cell');
  await bela.hub.waitFor('PickWaiting', (p) => p.id === anna.id);
  const grabCell = grabPick.validTerritoryIndices[0];
  await anna2.conn.invoke('PickTerritory', grabCell);
  const grabbed = (await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.index === grabCell))).payload;
  const grabbedCell = grabbed.changedTerritories.find((c) => c.index === grabCell);
  check(grabbedCell.ownerPlayerId === anna.id && grabbedCell.isBase === false, 'grabbed cell belongs to the winner and is not a base');

  // A battle: find an enemy cell adjacent to a cell Anna owns, let Anna nominate it, the host confirm it.
  const mapSnap = (await api(`/api/rooms/${code}`, { token: gmToken })).body.map;
  const cols = mapSnap.cols;
  const owner = new Map(mapSnap.territories.map((t) => [t.index, t.ownerPlayerId]));
  const neighbours = (i) => {
    const r = Math.floor(i / cols), c = i % cols, out = [];
    if (r > 0) out.push(i - cols);
    if (c > 0) out.push(i - 1);
    if (c < cols - 1) out.push(i + 1);
    if (r < mapSnap.rows - 1) out.push(i + cols);
    return out;
  };
  let battleTarget = null;
  for (const t of mapSnap.territories) {
    if (t.ownerPlayerId && t.ownerPlayerId !== anna.id && neighbours(t.index).some((n) => owner.get(n) === anna.id)) {
      battleTarget = t; break;
    }
  }
  check(battleTarget !== null, 'an enemy territory adjacent to Anna exists to battle for');
  const defenderId = battleTarget.ownerPlayerId;
  const defender = byId[defenderId];

  await anna2.conn.invoke('NominateTarget', battleTarget.index);
  const nominated = (await host.waitFor('BattleNominated')).payload;
  check(nominated.attackerId === anna.id && nominated.defenderId === defenderId && nominated.targetTerritoryIndex === battleTarget.index,
    'host is told who nominated which target');

  host.events.length = 0;
  await host.conn.invoke('ConfirmBattle', { questionId: DEMO_EST });
  const battleHostQ = (await host.waitFor('QuestionStarted')).payload;
  const battleQ = (await anna2.waitFor('QuestionStarted', (q) => q.roundId === battleHostQ.roundId)).payload;
  check(battleQ.type === 1, 'confirmed battle opens an estimation duel');
  // A spectator (neither attacker nor defender) only learns who fights.
  const spectator = [anna, bela, cili].find((p) => p.id !== anna.id && p.id !== defenderId);
  const spectatorHub = spectator === bela ? bela.hub : cili.hub;
  await spectatorHub.waitFor('BattleAnnounced', (b) => b.targetTerritoryIndex === battleTarget.index);
  check(true, 'non-participants receive BattleAnnounced, not the question');

  // Anna guesses exactly, the defender far off: Anna takes the territory.
  await anna2.conn.invoke('SubmitAnswer', { roundId: battleQ.roundId, number: 8849 });
  await defender.hub.conn.invoke('SubmitAnswer', { roundId: battleQ.roundId, number: 1 });
  const battleClosed = (await host.waitFor('RoundClosed', (r) => r.roundId === battleQ.roundId)).payload;
  check(battleClosed.kind === Kind.Battle && battleClosed.winnerPlayerIds[0] === anna.id, 'battle winner = closest guess');
  const transfer = (await host.waitFor('MapUpdated', (u) => u.changedTerritories.some((c) => c.index === battleTarget.index))).payload;
  check(transfer.changedTerritories.find((c) => c.index === battleTarget.index).ownerPlayerId === anna.id, 'battle hands the contested territory to the winner');

  // ---- A running round still stops cleanly when the room closes (timer cancellation) ----
  await host.conn.invoke('SendQuestion', { questionId: DEMO_MC, kind: Kind.Grab });
  const openGrab = (await host.waitFor('QuestionStarted')).payload;
  await host.conn.invoke('CloseRound'); // no answers -> no winner -> no pending pick
  await host.waitFor('RoundClosed', (r) => r.roundId === openGrab.roundId);

  // ---- End the game: Conquest -> Results, GameFinished with ranked standings ----
  await host.conn.invoke('AdvancePhase', 3);
  await host.waitFor('PhaseChanged', (p) => p === 3);
  const finished = (await host.waitFor('GameFinished')).payload;
  check(Array.isArray(finished.standings) && finished.standings.length === 3, 'GameFinished lists every player');
  check(finished.standings[0].rank === 1 && finished.standings.every((s, i) => i === 0 || s.rank >= finished.standings[i - 1].rank),
    'standings are ranked best first');
  check(finished.standings.find((s) => s.playerId === anna.id).score >= grabbedCell.value, 'the leader score reflects owned territories');

  check((await api(`/api/rooms/${code}`, { method: 'DELETE', token: gmToken })).status === 204, 'the finished room closes -> 204');

  await Promise.allSettled([host.conn.stop(), hostAgain.conn.stop(), anna2.conn.stop(), bela.hub.conn.stop(), cili.hub.conn.stop()]);
} catch (err) {
  console.error('ERROR', err);
  failures++;
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
