import { ErrorCodes, GamePhase, PickPurpose } from './hubNames.js';

// Pure state machine for territory selection confirmation and countdown on the player map screen.
export function createMapPickState({
  pick,
  nominate,
  playerId,
  remaining = (deadline) => Math.max(0, Date.parse(deadline) - Date.now()),
  changed = () => {},
}) {
  let version = 0;
  let clockRemaining = remaining;

  const state = {
    request: null,         // { valid: Set<number>, deadline: string, purpose: number }
    waiting: null,         // { picker: object, deadline: string | null, purpose: number | null }
    selectedIndex: null,   // number | null
    busy: false,
    online: true,
    finished: false,
    error: '',
    pickTimeoutSeconds: 20,
  };

  function getCountdown() {
    const deadline = state.request?.deadline ?? state.waiting?.deadline ?? null;
    if (!deadline || state.finished) {
      return { active: false, remainingMs: 0, seconds: 0, fraction: 0, expired: false };
    }
    const raw = clockRemaining(deadline);
    const remainingMs = Number.isFinite(raw) ? Math.max(0, raw) : 0;
    const seconds = Math.ceil(remainingMs / 1000);
    const totalMs = Math.max(1000, state.pickTimeoutSeconds * 1000);
    const fraction = Math.min(1, Math.max(0, remainingMs / totalMs));
    return {
      active: true,
      remainingMs,
      seconds,
      fraction,
      expired: remainingMs <= 0,
    };
  }

  function canSelect() {
    return Boolean(state.request && !state.busy && state.online && !state.finished && !getCountdown().expired);
  }

  function canConfirm() {
    return Boolean(canSelect() && state.selectedIndex !== null && state.request.valid.has(state.selectedIndex));
  }

  function clearPickFields() {
    state.request = null;
    state.waiting = null;
    state.selectedIndex = null;
    state.busy = false;
    state.error = '';
  }

  return {
    state,
    getCountdown,
    canSelect,
    canConfirm,
    setClock(fn) {
      clockRemaining = fn;
    },
    setOnline(value) {
      const next = Boolean(value);
      if (state.online === next) return;
      state.online = next;
      changed();
    },
    setState(roomState) {
      const players = new Map((roomState.players ?? []).map((p) => [p.id, p]));
      state.pickTimeoutSeconds = Math.max(1, roomState.settings?.pickTimeoutSeconds || 20);
      state.finished = roomState.phase === GamePhase.Results;
      state.error = '';

      const cp = roomState.currentPick;
      if (cp && !state.finished) {
        if (cp.pickerPlayerId === playerId()) {
          const nextValid = new Set(cp.validTerritoryIndices ?? []);
          const samePick = state.request
            && state.request.purpose === cp.purpose
            && state.request.deadline === cp.pickDeadlineUtc;

          state.waiting = null;
          if (samePick) {
            state.request = { valid: nextValid, deadline: cp.pickDeadlineUtc, purpose: cp.purpose };
            if (state.selectedIndex !== null && !nextValid.has(state.selectedIndex)) {
              state.selectedIndex = null;
            }
          } else {
            version++;
            state.request = { valid: nextValid, deadline: cp.pickDeadlineUtc, purpose: cp.purpose };
            state.selectedIndex = null;
            state.busy = false;
          }
        } else {
          version++;
          state.request = null;
          state.selectedIndex = null;
          state.busy = false;
          state.waiting = {
            picker: players.get(cp.pickerPlayerId) ?? { id: cp.pickerPlayerId, name: 'Someone' },
            deadline: cp.pickDeadlineUtc ?? null,
            purpose: cp.purpose ?? null,
          };
        }
      } else {
        version++;
        clearPickFields();
      }
      changed();
    },
    pickRequested(req) {
      const nextValid = new Set(req.validTerritoryIndices ?? []);
      const samePick = state.request
        && state.request.purpose === req.purpose
        && state.request.deadline === req.pickDeadlineUtc;

      state.waiting = null;
      state.error = '';
      if (samePick) {
        state.request = { valid: nextValid, deadline: req.pickDeadlineUtc, purpose: req.purpose };
        if (state.selectedIndex !== null && !nextValid.has(state.selectedIndex)) {
          state.selectedIndex = null;
        }
      } else {
        version++;
        state.request = { valid: nextValid, deadline: req.pickDeadlineUtc, purpose: req.purpose };
        state.selectedIndex = null;
        state.busy = false;
      }
      changed();
    },
    pickWaiting(payload) {
      version++;
      clearPickFields();
      const picker = payload?.picker ?? payload ?? { name: 'Someone' };
      state.waiting = {
        picker,
        deadline: payload?.pickDeadlineUtc ?? null,
        purpose: payload?.purpose ?? null,
      };
      changed();
    },
    select(index) {
      if (!canSelect() || !state.request.valid.has(index)) return false;
      state.selectedIndex = index;
      state.error = '';
      changed();
      return true;
    },
    cancelSelection() {
      if (state.busy || state.selectedIndex === null) return false;
      state.selectedIndex = null;
      state.error = '';
      changed();
      return true;
    },
    async confirm() {
      if (!canConfirm()) return false;
      const currentVersion = version;
      const index = state.selectedIndex;
      const purpose = state.request.purpose;
      state.busy = true;
      state.error = '';
      changed();

      try {
        if (purpose === PickPurpose.DuelTarget) await nominate(index);
        else await pick(index);
        if (currentVersion !== version) return false;
        state.request = null;
        state.selectedIndex = null;
        state.busy = false;
        state.error = '';
        changed();
        return true;
      } catch (err) {
        if (currentVersion !== version) return false;
        state.busy = false;
        const message = String(err?.message ?? err ?? '');
        if (message.includes(ErrorCodes.NotYourPick) || message.includes(ErrorCodes.NotYourTurn)) {
          state.request = null;
          state.selectedIndex = null;
          state.error = 'Your turn has already ended.';
        } else if (message.includes(ErrorCodes.InvalidPick) || message.includes(ErrorCodes.InvalidTarget)) {
          state.selectedIndex = null;
          state.error = 'That tile is no longer valid. Choose another highlighted tile.';
        } else if (message.includes(ErrorCodes.InvalidPhase)) {
          state.request = null;
          state.selectedIndex = null;
          state.error = 'This action is no longer available in the current phase.';
        } else {
          state.error = 'Could not submit your selection. Try again.';
        }
        changed();
        return false;
      }
    },
    onMapUpdated(cellsIterable) {
      if (state.request?.purpose !== PickPurpose.Base) return false;
      const me = playerId();
      for (const cell of cellsIterable) {
        if (cell.ownerPlayerId === me && cell.isBase) {
          version++;
          clearPickFields();
          changed();
          return true;
        }
      }
      return false;
    },
    phaseChanged(phase) {
      version++;
      clearPickFields();
      state.finished = phase === GamePhase.Results;
      changed();
    },
    clearPick() {
      version++;
      clearPickFields();
      changed();
    },
    duelAnnounced() {
      version++;
      clearPickFields();
      changed();
    },
    gameFinished() {
      version++;
      clearPickFields();
      state.finished = true;
      changed();
    },
    reset() {
      version++;
      clearPickFields();
      state.finished = false;
      state.pickTimeoutSeconds = 20;
      changed();
    },
  };
}
