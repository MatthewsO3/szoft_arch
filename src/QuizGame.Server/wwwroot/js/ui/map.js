// Map screen: shows the territory grid, lets the current picker choose a base (BaseSelection), shows scores.
// All dynamic text goes through textContent (names are untrusted input).
export function createMap({ pick, playerId }) {
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
        if (t.isBase) b.textContent = '★';
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
      titleEl.textContent = 'Pick your base';
      statusEl.textContent = 'Tap a free cell. Not picking in time assigns one at random.';
      const tick = () => {
        const left = Math.ceil(remaining(request.deadline) / 1000);
        timeEl.textContent = `${left}s`;
      };
      tick();
      timer = setInterval(tick, 250);
    } else if (waiting) {
      titleEl.textContent = 'Base selection';
      statusEl.textContent = `${waiting.name} is picking a base…`;
    } else {
      titleEl.textContent = 'Map';
      statusEl.textContent = 'Look at the projector.';
    }
  }

  async function choose(index) {
    busy = true;
    renderGrid();
    try { await pick(index); request = null; }
    catch (err) { statusEl.textContent = err?.message ?? 'Pick rejected.'; }
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
          request = { valid: new Set(cp.validTerritoryIndices), deadline: cp.pickDeadlineUtc };
        } else waiting = players.get(cp.pickerPlayerId) ?? { name: 'Someone' };
      }
      renderStatus(); renderGrid(); renderScores();
    },
    /** MapUpdated: only changed cells + scoreboard. */
    update(update) {
      for (const t of update.changedTerritories) cells.set(t.index, t);
      map = { ...(map ?? { rows: 0, cols: 0 }), territories: [...cells.values()] };
      scoreboard = update.scoreboard ?? scoreboard;
      if (request && [...cells.values()].some((t) => t.ownerPlayerId === playerId() && t.isBase)) request = null;
      renderGrid(); renderScores();
    },
    pickRequested(req) {
      waiting = null;
      request = { valid: new Set(req.validTerritoryIndices), deadline: req.pickDeadlineUtc };
      renderStatus(); renderGrid();
    },
    pickWaiting(player) { request = null; waiting = player; renderStatus(); renderGrid(); },
    gameFinished(data) {
      finished = true; request = null; waiting = null;
      setMap(data.finalMap);
      renderStatus(); renderGrid();
    },
    get hasMap() { return map !== null; },
    reset() { clearInterval(timer); map = null; cells.clear(); request = null; waiting = null; finished = false; renderGrid(); },
  };
}
