// Bootstrap + screen state machine for the player web app (plan §10).
import { rejoinRoom, ApiError } from './api.js';
import { createHub } from './hub.js';
import { ErrorCodes } from './hubNames.js';
import { loadSession, saveSession, clearSession } from './session.js';
import { initJoin } from './ui/join.js';
import { createLobby } from './ui/lobby.js';
import { createQuestion } from './ui/question.js';
import { createMap } from './ui/map.js';
import { GamePhase } from './hubNames.js';
import { createClock } from './util/timer.js';

const screens = {
  join: document.getElementById('screen-join'),
  lobby: document.getElementById('screen-lobby'),
  question: document.getElementById('screen-question'),
  map: document.getElementById('screen-map'),
};
const header = document.getElementById('header');
const meChip = document.getElementById('me-chip');
const meName = document.getElementById('me-name');
const connEl = document.getElementById('conn');

const lobby = createLobby();
let hub = null;
let session = null;
let connectionVersion = 0;
const clock = createClock();
const question = createQuestion({
  clock,
  send: (answer) => hub.submitAnswer(answer),
  recover: () => hub.rejoin(),
  playerId: () => session?.player.id,
});

const map = createMap({ pick: (i) => hub.pickTerritory(i), nominate: (i) => hub.nominateTarget(i), playerId: () => session?.player.id });

map.setClock((deadline) => clock.remaining(deadline));

async function syncClock() {
  const current = hub;
  if (!current?.isConnected) return;
  const started = performance.now();
  try {
    const utc = await current.ping();
    if (hub === current) clock.sync(utc, performance.now() - started);
  } catch { /* A new RoomState also synchronizes the clock. */ }
}

function applyState(state) {
  clock.sync(state.serverUtcNow);
  lobby.setState(state);
  question.reset();
  
  map.setState(state);
  if (state.playerQuestion) {
    question.start(state.playerQuestion, state.hasAnswered);
    show('question');
  } else if (state.phase !== GamePhase.Lobby && map.hasMap
      && (state.currentPick || state.phase !== GamePhase.BaseSelection || !state.lastRoundResult)) {
    show('map');
  } else if (state.lastRoundResult) {
    question.close(state.lastRoundResult);
    show('question');
  } else show('lobby');
}

const join = initJoin({ onJoined: (response, roomCode) => enterRoom(response, roomCode) });

function show(name) {
  for (const [key, el] of Object.entries(screens)) el.hidden = key !== name;
  header.hidden = name === 'join';
}

function setConnState(state) {
  connEl.dataset.state = state;
  lobby.showOffline(state === 'offline');
  question.setOnline(state === 'online');
  document.getElementById('game-offline').hidden = state === 'online';
  if (state === 'online') syncClock();
}

/** Leaves the room locally and returns to the join screen with an optional message. */
async function backToJoin(message) {
  connectionVersion++;
  clearSession();
  session = null;
  const old = hub;
  hub = null;
  try { await old?.stop(); } catch { /* ignore */ }
  lobby.reset();
  question.reset();
  map.reset();
  show('join');
  join.message(message ?? '');
  join.focus();
}

async function enterRoom(response, roomCode) {
  session = { token: response.token, roomCode, player: response.player };
  saveSession(session);

  meChip.style.background = response.player.colorHex;
  meName.textContent = response.player.name;
  lobby.setRoom(roomCode, response.player.id);
  show('lobby');
  await connect();
}

async function connect() {
  if (!session) return;
  const version = ++connectionVersion;
  const old = hub;
  hub = null;
  setConnState('reconnecting');
  try { await old?.stop(); } catch { /* the old connection is already unusable */ }
  if (version !== connectionVersion || !session) return;
  const handlers = {
    onState: setConnState,
    RoomState: applyState,
    QuestionStarted: (data) => { question.start(data); show('question'); },
    DuelAnnounced: (data) => { map.duelAnnounced(data); show('map'); },
    MapUpdated: (data) => { map.update(data); },
    PickRequested: (req) => { map.pickRequested(req); show('map'); },
    PickWaiting: (player) => { map.pickWaiting(player); show('map'); },
    GameFinished: (data) => { map.gameFinished(data); show('map'); },
    RoundClosed: (data) => { if (question.close(data)) show('question'); },
    PlayerJoined: (player) => lobby.upsert(player),
    PlayerConnectionChanged: (player) => lobby.upsert(player),
    PlayerLeft: (player) => lobby.remove(player.id),
    Kicked: () => backToJoin('You were removed by the host.'),
    RoomClosed: () => backToJoin('The host closed the room.'),
    Error: (err) => {
      if (err.code === ErrorCodes.RoomNotFound || err.code === ErrorCodes.PlayerNotFound) backToJoin(err.message);
    },
  };
  hub = createHub(session.token, Object.fromEntries(Object.entries(handlers).map(([name, handler]) =>
    [name, (...args) => { if (version === connectionVersion) handler(...args); }])));

  try {
    await hub.start();
  } catch {
    if (version === connectionVersion) setConnState('offline');
  }
}

document.getElementById('reconnect-btn').addEventListener('click', connect);
document.getElementById('game-reconnect-btn').addEventListener('click', connect);

document.getElementById('leave-btn').addEventListener('click', async () => {
  try { await hub?.leaveRoom(); } catch { /* server removes us on next join attempt anyway */ }
  backToJoin('');
});

// Mobile browsers suspend sockets when the screen locks: reconnect when the page is visible again.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && session && hub?.isDisconnected) connect();
  else if (document.visibilityState === 'visible') syncClock();
});

async function boot() {
  const params = new URLSearchParams(location.search);
  join.prefill(params.get('code'));

  const stored = loadSession();
  if (stored?.token && stored.roomCode) {
    try {
      // Rejoin: validates the stored token and that we are still in the room.
      const response = await rejoinRoom(stored.roomCode, stored.token);
      await enterRoom(response, stored.roomCode);
      return;
    } catch (err) {
      if (err instanceof ApiError && err.status === 0) {
        // Server unreachable: keep the session and let the user retry by reloading.
        show('join');
        join.message(err.message);
        return;
      }
      clearSession();
    }
  }

  show('join');
  join.focus();
}

boot();
