// Map screen: the territory grid. Lets the current player select & confirm (base, conquest claim, expansion)
// or nominate a duel target, shows base markers, countdown, and the scoreboard.
// All dynamic text uses textContent (names are untrusted).
import { GamePhase, PickPurpose, RoundKind } from '../hubNames.js';
import { createMapPickState } from '../mapPickState.js';

export function createMap({ pick, nominate, playerId, onScoreChange }) {
  const gridEl = document.getElementById('map-grid');
  const titleEl = document.getElementById('map-title');
  const statusEl = document.getElementById('map-status');
  const timeEl = document.getElementById('map-time');
  const progressEl = document.getElementById('map-progress');
  const progressFillEl = document.getElementById('map-progress-fill');
  const confirmBarEl = document.getElementById('map-confirm-bar');
  const selectedLabelEl = document.getElementById('map-selected-label');
  const confirmBtnEl = document.getElementById('map-confirm-btn');
  const cancelBtnEl = document.getElementById('map-cancel-btn');
  const scoreEl = document.getElementById('map-scores');

  let map = null;               // { rows, cols, territories }
  let cells = new Map();        // index -> territory
  let players = new Map();      // id -> player summary
  let scoreboard = [];
  let finalStandings = null;
  let duelNotice = null;
  let duelTargetIndex = null;
  let timer = null;
  let lastExpired = false;

  const pickState = createMapPickState({
    pick,
    nominate,
    playerId,
    changed: renderInteractive,
  });

  confirmBtnEl.addEventListener('click', () => {
    void pickState.confirm();
  });
  cancelBtnEl.addEventListener('click', () => {
    pickState.cancelSelection();
  });

  function colorOf(ownerId) { return players.get(ownerId)?.colorHex ?? '#444'; }

  function notifyMyScore() {
    if (!onScoreChange) return;
    const me = playerId();
    const row = (finalStandings ?? scoreboard).find((s) => s.playerId === me);
    onScoreChange(row ? row.score : null);
  }

  function buildRankedScoreboard() {
    let prevRank = 1;
    return scoreboard.map((s, i) => {
      const rank = i > 0 && s.score === scoreboard[i - 1].score ? prevRank : i + 1;
      prevRank = rank;
      return { ...s, rank };
    });
  }

  function renderGrid() {
    if (!map) { gridEl.replaceChildren(); return; }
    gridEl.style.gridTemplateColumns = `repeat(${map.cols}, 1fr)`;
    const me = playerId();
    const { request, selectedIndex } = pickState.state;
    const canSelect = pickState.canSelect();

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

      const isValid = Boolean(request?.valid.has(t.index));
      const pickable = isValid && canSelect;
      const isSelected = isValid && selectedIndex === t.index;

      b.disabled = !pickable;
      if (pickable) {
        b.classList.add('pickable');
        b.setAttribute('aria-pressed', isSelected ? 'true' : 'false');
        b.addEventListener('click', () => pickState.select(t.index));
      }
      if (isSelected) b.classList.add('selected');
      if (duelTargetIndex === t.index && !pickable) b.classList.add('duel-target');
      return b;
    });
    gridEl.replaceChildren(...nodes);
  }

  function renderScores() {
    const me = playerId();
    const rows = finalStandings ?? (pickState.state.finished ? buildRankedScoreboard() : scoreboard);
    scoreEl.replaceChildren(...rows.map((s) => {
      const li = document.createElement('li');
      if (s.playerId === me) li.classList.add('me');
      if (s.rank != null) {
        const rank = document.createElement('span');
        rank.className = 'rank';
        rank.textContent = `#${s.rank}`;
        li.append(rank);
      }
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

  function renderConfirmBar() {
    const { request, selectedIndex, busy, finished } = pickState.state;
    confirmBarEl.hidden = !(request && !finished);
    if (confirmBarEl.hidden) return;

    if (selectedIndex === null) {
      selectedLabelEl.textContent = 'Tap a highlighted tile to select it.';
    } else {
      const tileNum = selectedIndex + 1;
      const tile = cells.get(selectedIndex);
      const ownerName = tile?.ownerPlayerId ? players.get(tile.ownerPlayerId)?.name : null;
      const selectionLabels = {
        [PickPurpose.Base]: `Tile #${tileNum} selected as your base`,
        [PickPurpose.ConquestClaim]: `Tile #${tileNum} selected to claim`,
        [PickPurpose.Expansion]: `Tile #${tileNum} selected`,
        [PickPurpose.DuelTarget]: ownerName
          ? `Tile #${tileNum} (${ownerName}${tile?.isBase ? "'s base" : ''}) selected to attack`
          : `Tile #${tileNum} selected to attack`,
      };
      selectedLabelEl.textContent = selectionLabels[request.purpose] ?? `Tile #${tileNum} selected`;
    }

    const buttonLabels = {
      [PickPurpose.Base]: 'Confirm base',
      [PickPurpose.ConquestClaim]: 'Confirm claim',
      [PickPurpose.Expansion]: 'Confirm tile',
      [PickPurpose.DuelTarget]: 'Confirm attack',
    };
    confirmBtnEl.textContent = busy ? 'Sending…' : (buttonLabels[request.purpose] ?? 'Confirm');
    confirmBtnEl.disabled = !pickState.canConfirm();

    cancelBtnEl.hidden = selectedIndex === null || busy;
    cancelBtnEl.disabled = busy;
  }

  function renderStatus() {
    const { request, waiting, busy, online, finished, error } = pickState.state;
    const cd = pickState.getCountdown();

    if (finished) {
      titleEl.textContent = 'Game over';
      const rows = finalStandings ?? buildRankedScoreboard();
      const mine = rows.find((s) => s.playerId === playerId());
      statusEl.textContent = mine
        ? `You finished #${mine.rank} with ${mine.score} pts`
        : 'Final map';
      statusEl.className = 'muted';
      return;
    }

    if (request) {
      const labels = {
        [PickPurpose.Base]: ['Pick your base', 'Tap a highlighted tile, then confirm your base. Not picking in time assigns one at random.'],
        [PickPurpose.ConquestClaim]: ['Claim a tile', 'Tap a highlighted tile and confirm, then answer the question to keep it.'],
        [PickPurpose.Expansion]: ['Pick a tile', 'You were closest — tap a highlighted tile and confirm to take it.'],
        [PickPurpose.DuelTarget]: ['Attack a tile', 'Tap an enemy tile next to yours and confirm to duel.'],
      };
      const [title, guidance] = labels[request.purpose] ?? ['Your turn', 'Tap a highlighted tile and confirm.'];
      titleEl.textContent = title;
      if (error) {
        statusEl.textContent = error;
        statusEl.className = 'error';
      } else if (!online) {
        statusEl.textContent = 'Disconnected. Reconnect before confirming.';
        statusEl.className = 'error';
      } else if (cd.expired) {
        statusEl.textContent = 'Time is up — waiting for the server…';
        statusEl.className = 'muted';
      } else if (busy) {
        statusEl.textContent = 'Confirming your selection…';
        statusEl.className = 'muted';
      } else {
        statusEl.textContent = guidance;
        statusEl.className = 'muted';
      }
      return;
    }

    if (waiting) {
      const name = waiting.picker?.name ?? 'Someone';
      const waitingTitles = {
        [PickPurpose.Base]: 'Base selection',
        [PickPurpose.ConquestClaim]: 'Conquest',
        [PickPurpose.Expansion]: 'Expansion',
        [PickPurpose.DuelTarget]: 'Duel',
      };
      const waitingStatuses = {
        [PickPurpose.Base]: `${name} is picking a base…`,
        [PickPurpose.ConquestClaim]: `${name} is claiming a tile…`,
        [PickPurpose.Expansion]: `${name} is picking a tile…`,
        [PickPurpose.DuelTarget]: `${name} is choosing a duel target…`,
      };
      titleEl.textContent = waitingTitles[waiting.purpose] ?? 'Your move is not up';
      statusEl.textContent = waitingStatuses[waiting.purpose] ?? `${name} is choosing…`;
      statusEl.className = 'muted';
      return;
    }

    if (duelNotice) {
      titleEl.textContent = 'Duel';
      statusEl.textContent = duelNotice;
      statusEl.className = 'muted';
      return;
    }

    titleEl.textContent = 'Map';
    statusEl.textContent = 'Look at the projector.';
    statusEl.className = 'muted';
  }

  function stopTimer() {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  }

  function renderCountdownDisplay(cd) {
    if (!cd.active) {
      timeEl.textContent = '';
      timeEl.classList.remove('urgent');
      progressEl.hidden = true;
      progressFillEl.style.width = '0%';
      progressFillEl.classList.remove('warn', 'urgent');
      stopTimer();
      return;
    }

    const isPicker = Boolean(pickState.state.request);
    timeEl.textContent = isPicker && cd.expired ? 'Time is up…' : `${cd.seconds} s`;

    const isUrgent = cd.seconds <= 3;
    const isWarn = !isUrgent && cd.seconds <= 5;
    timeEl.classList.toggle('urgent', isUrgent);
    progressEl.hidden = false;
    progressFillEl.style.width = `${(cd.fraction * 100).toFixed(1)}%`;
    progressFillEl.classList.toggle('urgent', isUrgent);
    progressFillEl.classList.toggle('warn', isWarn);

    if (timer === null) {
      timer = setInterval(tickCountdown, 100);
    }
  }

  function tickCountdown() {
    const cd = pickState.getCountdown();
    renderCountdownDisplay(cd);
    if (cd.active && cd.expired && !lastExpired) {
      lastExpired = true;
      renderStatus();
      renderGrid();
      renderConfirmBar();
    }
  }

  function renderInteractive() {
    const cd = pickState.getCountdown();
    lastExpired = cd.expired;
    renderCountdownDisplay(cd);
    renderStatus();
    renderGrid();
    renderConfirmBar();
  }

  function setMap(m) {
    map = m;
    cells = new Map((m?.territories ?? []).map((t) => [t.index, t]));
  }

  return {
    setClock(fn) { pickState.setClock(fn); },
    setOnline(value) { pickState.setOnline(value); },
    /** From RoomState. */
    setState(state) {
      duelNotice = null;
      duelTargetIndex = null;
      finalStandings = null;
      players = new Map(state.players.map((p) => [p.id, p]));
      setMap(state.map ?? null);
      scoreboard = state.scoreboard ?? [];
      if (!state.playerQuestion && state.currentRound?.kind === RoundKind.Duel && state.currentRound.targetTerritoryIndex != null) {
        const attacker = players.get(state.currentRound.attackerId)?.name ?? 'Attacker';
        const defender = players.get(state.currentRound.defenderId)?.name ?? 'Defender';
        duelNotice = `${attacker} attacks ${defender}. Watch the projector.`;
        duelTargetIndex = state.currentRound.targetTerritoryIndex;
      }
      pickState.setState(state);
      renderScores();
      notifyMyScore();
    },
    /** MapUpdated: only changed cells + scoreboard. */
    update(update) {
      for (const t of update.changedTerritories) cells.set(t.index, t);
      map = { ...(map ?? { rows: 0, cols: 0 }), territories: [...cells.values()] };
      scoreboard = update.scoreboard ?? scoreboard;
      const cleared = pickState.onMapUpdated(cells.values());
      if (!cleared) {
        renderGrid();
        renderConfirmBar();
      }
      renderScores();
      notifyMyScore();
    },
    updateScores(nextScoreboard) {
      scoreboard = nextScoreboard ?? [];
      renderScores();
      notifyMyScore();
    },
    pickRequested(req) {
      duelNotice = null;
      duelTargetIndex = null;
      pickState.pickRequested(req);
    },
    pickWaiting(waiting) {
      duelNotice = null;
      duelTargetIndex = null;
      const picker = waiting?.picker ?? waiting;
      if (picker?.id) players.set(picker.id, picker);
      pickState.pickWaiting(waiting);
    },
    phaseChanged(phase) {
      duelNotice = null;
      duelTargetIndex = null;
      if (phase === GamePhase.Results && !finalStandings) {
        renderScores();
      }
      pickState.phaseChanged(phase);
    },
    clearPick() {
      duelNotice = null;
      duelTargetIndex = null;
      pickState.clearPick();
    },
    clearDuelTarget() {
      if (duelNotice === null && duelTargetIndex === null) return;
      duelNotice = null;
      duelTargetIndex = null;
      renderStatus();
      renderGrid();
    },
    duelAnnounced(data) {
      const attacker = players.get(data.attackerId)?.name ?? 'Attacker';
      const defender = players.get(data.defenderId)?.name ?? 'Defender';
      duelNotice = `${attacker} attacks ${defender}. Watch the projector.`;
      duelTargetIndex = data.targetTerritoryIndex ?? null;
      pickState.duelAnnounced();
    },
    gameFinished(data) {
      duelNotice = null;
      duelTargetIndex = null;
      finalStandings = data.standings ?? null;
      if (finalStandings) {
        scoreboard = finalStandings.map((s) => ({
          playerId: s.playerId,
          name: s.name,
          score: s.score,
          territoryCount: s.territoryCount,
        }));
      }
      setMap(data.finalMap);
      pickState.gameFinished();
      renderScores();
      notifyMyScore();
    },
    get hasMap() { return map !== null; },
    reset() {
      stopTimer();
      duelNotice = null;
      duelTargetIndex = null;
      finalStandings = null;
      map = null;
      cells.clear();
      players.clear();
      scoreboard = [];
      pickState.reset();
      renderScores();
      onScoreChange?.(null);
    },
  };
}
