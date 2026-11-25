// Bootstrap + screen state machine for the player web app (plan §10).
import { rejoinRoom, ApiError } from './api.js';
import { createHub } from './hub.js';
import { ErrorCodes } from './hubNames.js';
import { loadSession, saveSession, clearSession } from './session.js';
import { initJoin } from './ui/join.js';
import { createLobby } from './ui/lobby.js';

const screens = {
  join: document.getElementById('screen-join'),
  lobby: document.getElementById('screen-lobby'),
};
const header = document.getElementById('header');
const meChip = document.getElementById('me-chip');
const meName = document.getElementById('me-name');
const connEl = document.getElementById('conn');

const lobby = createLobby();
let hub = null;
let session = null;

const join = initJoin({ onJoined: (response, roomCode) => enterRoom(response, roomCode) });

function show(name) {
  for (const [key, el] of Object.entries(screens)) el.hidden = key !== name;
  header.hidden = name === 'join';
}

function setConnState(state) {
  connEl.dataset.state = state;
  lobby.showOffline(state === 'offline');
}

/** Leaves the room locally and returns to the join screen with an optional message. */
async function backToJoin(message) {
  clearSession();
  session = null;
  const old = hub;
  hub = null;
  try { await old?.stop(); } catch { /* ignore */ }
  lobby.reset();
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
  hub?.stop();
  hub = createHub(session.token, {
    onState: setConnState,
    RoomState: (state) => lobby.setState(state),
    PlayerJoined: (player) => lobby.upsert(player),
    PlayerConnectionChanged: (player) => lobby.upsert(player),
    PlayerLeft: (player) => lobby.remove(player.id),
    Kicked: () => backToJoin('You were removed by the host.'),
    RoomClosed: () => backToJoin('The host closed the room.'),
    Error: (err) => {
      if (err.code === ErrorCodes.RoomNotFound || err.code === ErrorCodes.PlayerNotFound) backToJoin(err.message);
    },
  });

  try {
    await hub.start();
  } catch {
    setConnState('offline');
  }
}

document.getElementById('reconnect-btn').addEventListener('click', connect);

document.getElementById('leave-btn').addEventListener('click', async () => {
  try { await hub?.leaveRoom(); } catch { /* server removes us on next join attempt anyway */ }
  backToJoin('');
});

// Mobile browsers suspend sockets when the screen locks: reconnect when the page is visible again.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && session && hub?.isDisconnected) connect();
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
