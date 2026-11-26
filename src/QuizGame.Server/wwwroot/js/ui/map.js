// Map screen: the territory grid. Lets the current player pick (base, conquest claim, expansion) or nominate a
// duel target, shows base markers and the scoreboard. All dynamic text uses textContent (names are untrusted).
import { PickPurpose } from '../hubNames.js';

export function createMap({ pick, nominate, playerId }) {
  const gridEl = document.getElementById('map-grid');
  const titleEl = document.getElementById('map-title');
  const statusEl = document.getElementById('map-status');
  const timeEl = document.getElementById('map-time');
  const scoreEl = document.getElementById('map-scores');

  let map = null;               // { rows, cols, territories }
  let cells = new Map();        // index -> territory
  let players = new Map();      // id -> player summary
  let scoreboard = [];
  let request = null;           // { valid:Set<number>, deadline:number } when it is my turn
  let waiting = null;           // player summary of the current picker (somebody else)
  let timer = null;
  let remaining = (deadline) => Math.max(0, Date.parse(deadline) - Date.now());
  let busy = false;
  let finished = false;

  function colorOf(ownerId) { return players.get(ownerId)?.colorHex ?? '#444'; }

  function renderGrid() {
    if (!map) { gridEl.replaceChildren(); return; }
    gridEl.style.gridTemplateColumns = `repeat(${map.cols}, 1fr)`;
    const me = playerId();
    const nodes = [...cells.values()].sort((a, b) => a.index - b.index).map((t) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'cell';
      if (t.ownerPlayerId) {
        b.style.background = colorOf(t.ownerPlayerId);
        b.classList.add('owned');
        if (t.ownerPlayerId === me) b.classList.add('mine');
        if (t.isBase) b.textContent = t.health > 1 ? `★${t.health}` : '★';
      } else {
        b.textContent = String(t.index + 1);
      }
      const pickable = request?.valid.has(t.index) && !busy;
      b.disabled = !pickable;
      if (pickable) {
        b.classList.add('pickable');
        b.addEventListener('click', () => choose(t.index));
      }
      return b;
    });
    gridEl.replaceChildren(...nodes);
  }

  function renderScores() {
    scoreEl.replaceChildren(...scoreboard.map((s) => {
      const li = document.createElement('li');
      const chip = document.createElement('span');
      chip.className = 'chip';
      chip.style.background = colorOf(s.playerId);
      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = s.name;
      const pts = document.createElement('span');
      pts.textContent = `${s.score} (${s.territoryCount})`;
      li.append(chip, name, pts);
      return li;
    }));
  }

  function renderStatus() {
    clearInterval(timer);
    timeEl.textContent = '';
    if (finished) { titleEl.textContent = 'Game over'; statusEl.textContent = 'Final map'; return; }
    if (request) {
      const labels = {
        [PickPurpose.Base]: ['Pick your base', 'Tap a free cell. Not picking in time assigns one at random.'],
        [PickPurpose.ConquestClaim]: ['Claim a tile', 'Tap a free cell, then answer the question to keep it.'],
        [PickPurpose.Expansion]: ['Pick a tile', 'You were closest — tap a free cell to take it.'],
        [PickPurpose.DuelTarget]: ['Attack a tile', 'Tap an enemy tile next to yours to attack it.'],
      };
      const [title, status] = labels[request.purpose] ?? ['Your turn', 'Tap a highlighted cell.'];
      titleEl.textContent = title;
      statusEl.textContent = status;
      const tick = () => {
        const left = Math.ceil(remaining(request.deadline) / 1000);
        timeEl.textContent = `${left}s`;
      };
      tick();
      timer = setInterval(tick, 250);
    } else if (waiting) {
      titleEl.textContent = 'Your move is not up';
      statusEl.textContent = `${waiting.name} is choosing…`;
    } else {
      titleEl.textContent = 'Map';
      statusEl.textContent = 'Look at the projector.';
    }
  }

  async function choose(index) {
    busy = true;
    const purpose = request?.purpose;
    renderGrid();
    try {
      if (purpose === PickPurpose.DuelTarget) await nominate(index);
      else await pick(index);
      request = null;
    }
    catch (err) { statusEl.textContent = err?.message ?? 'Rejected.'; }
    finally { busy = false; renderStatus(); renderGrid(); }
  }

  function setMap(m) {
    map = m;
    cells = new Map((m?.territories ?? []).map((t) => [t.index, t]));
  }

  return {
    setClock(fn) { remaining = fn; },
    /** From RoomState. */
    setState(state) {
      players = new Map(state.players.map((p) => [p.id, p]));
      setMap(state.map ?? null);
      scoreboard = state.scoreboard ?? [];
      finished = false;
      request = null;
      waiting = null;
      const cp = state.currentPick;
      if (cp) {
        if (cp.pickerPlayerId === playerId()) {
          request = { valid: new Set(cp.validTerritoryIndices), deadline: cp.pickDeadlineUtc, purpose: cp.purpose };
        } else waiting = players.get(cp.pickerPlayerId) ?? { name: 'Someone' };
      }
      renderStatus(); renderGrid(); renderScores();
    },
    /** MapUpdated: only changed cells + scoreboard. */
    update(update) {
      for (const t of update.changedTerritories) cells.set(t.index, t);
      map = { ...(map ?? { rows: 0, cols: 0 }), territories: [...cells.values()] };
      scoreboard = update.scoreboard ?? scoreboard;
      // Base pick resolves when my base appears; other picks resolve on their own server events.
      if (request?.purpose === PickPurpose.Base && [...cells.values()].some((t) => t.ownerPlayerId === playerId() && t.isBase)) request = null;
      renderGrid(); renderScores();
    },
    pickRequested(req) {
      waiting = null;
      request = { valid: new Set(req.validTerritoryIndices), deadline: req.pickDeadlineUtc, purpose: req.purpose };
      renderStatus(); renderGrid();
    },
    pickWaiting(player) { request = null; waiting = player; renderStatus(); renderGrid(); },
    duelAnnounced(data) {
      request = null; waiting = null;
      const attacker = players.get(data.attackerId)?.name ?? 'Attacker';
      const defender = players.get(data.defenderId)?.name ?? 'Defender';
      titleEl.textContent = 'Duel';
      statusEl.textContent = `${attacker} attacks ${defender}. Watch the projector.`;
      renderGrid();
    },
    gameFinished(data) {
      finished = true; request = null; waiting = null;
      setMap(data.finalMap);
      renderStatus(); renderGrid();
    },
    get hasMap() { return map !== null; },
    reset() { clearInterval(timer); map = null; cells.clear(); request = null; waiting = null; finished = false; renderGrid(); },
  };
}
