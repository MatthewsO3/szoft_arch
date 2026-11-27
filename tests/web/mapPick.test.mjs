import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createMapPickState } from '../../src/QuizGame.Server/wwwroot/js/mapPickState.js';
import { PickPurpose, GamePhase } from '../../src/QuizGame.Server/wwwroot/js/hubNames.js';

function createClockControl(startIso = '2026-11-27T12:00:00Z') {
  let nowMs = Date.parse(startIso);
  return {
    remaining: (deadline) => Math.max(0, Date.parse(deadline) - nowMs),
    advance: (ms) => { nowMs += ms; },
  };
}

function makeModel({
  pick = async () => {},
  nominate = async () => {},
  playerId = () => 'p1',
  remaining,
} = {}) {
  let changes = 0;
  const model = createMapPickState({
    pick,
    nominate,
    playerId,
    remaining,
    changed: () => { changes++; },
  });
  return { model, changes: () => changes };
}

test('selecting a tile does not send immediately; confirm invokes pick or nominate by purpose', async () => {
  const picked = [];
  const nominated = [];
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({
    pick: async (idx) => { picked.push(idx); },
    nominate: async (idx) => { nominated.push(idx); },
    remaining: clock.remaining,
  });

  for (const purpose of [PickPurpose.Base, PickPurpose.ConquestClaim, PickPurpose.Expansion]) {
    const beforeCount = picked.length;
    model.pickRequested({
      validTerritoryIndices: [2, 5, 8],
      pickDeadlineUtc: '2026-11-27T12:00:20Z',
      purpose,
    });
    assert.equal(model.select(5), true);
    assert.equal(model.state.selectedIndex, 5);
    assert.equal(picked.length, beforeCount);
    assert.equal(model.canConfirm(), true);
    assert.equal(await model.confirm(), true);
    assert.equal(picked.at(-1), 5);
    assert.equal(model.state.request, null);
    assert.equal(model.state.selectedIndex, null);
  }
  assert.deepEqual(picked, [5, 5, 5]);

  model.pickRequested({
    validTerritoryIndices: [4, 7],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.DuelTarget,
  });
  assert.equal(model.select(7), true);
  assert.equal(nominated.length, 0);
  assert.equal(await model.confirm(), true);
  assert.deepEqual(nominated, [7]);
});

test('switching selection, cancelling, and ignoring invalid cells work as expected', () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({ remaining: clock.remaining });
  model.pickRequested({
    validTerritoryIndices: [1, 3],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });

  assert.equal(model.select(99), false);
  assert.equal(model.state.selectedIndex, null);
  assert.equal(model.canConfirm(), false);

  assert.equal(model.select(1), true);
  assert.equal(model.select(3), true);
  assert.equal(model.state.selectedIndex, 3);
  assert.equal(model.canConfirm(), true);

  assert.equal(model.cancelSelection(), true);
  assert.equal(model.state.selectedIndex, null);
  assert.equal(model.canConfirm(), false);
  assert.equal(model.cancelSelection(), false);
});

test('double confirm while in flight sends only one request', async () => {
  let resolvePick;
  const sent = [];
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({
    pick: (idx) => {
      sent.push(idx);
      return new Promise((r) => { resolvePick = r; });
    },
    remaining: clock.remaining,
  });

  model.pickRequested({
    validTerritoryIndices: [4],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });
  model.select(4);

  const first = model.confirm();
  const second = await model.confirm();
  assert.equal(second, false);
  assert.equal(model.state.busy, true);
  assert.deepEqual(sent, [4]);

  resolvePick();
  assert.equal(await first, true);
  assert.equal(model.state.busy, false);
  assert.equal(model.state.request, null);
});

test('waiting players track countdown from PickWaitingDto and RoomState.currentPick', () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({ remaining: clock.remaining });

  model.setState({
    phase: GamePhase.BaseSelection,
    settings: { pickTimeoutSeconds: 20 },
    players: [
      { id: 'p1', name: 'Anna' },
      { id: 'p2', name: 'Bela' },
    ],
    currentPick: {
      pickerPlayerId: 'p2',
      pickDeadlineUtc: '2026-11-27T12:00:20Z',
      validTerritoryIndices: [],
      purpose: PickPurpose.Base,
    },
  });

  assert.equal(model.state.waiting?.picker.name, 'Bela');
  let cd = model.getCountdown();
  assert.equal(cd.active, true);
  assert.equal(cd.seconds, 20);
  assert.equal(cd.fraction, 1);
  assert.equal(cd.expired, false);

  clock.advance(5000);
  cd = model.getCountdown();
  assert.equal(cd.seconds, 15);
  assert.equal(cd.fraction, 0.75);

  model.pickWaiting({
    picker: { id: 'p3', name: 'Cili' },
    pickDeadlineUtc: '2026-11-27T12:00:25Z',
    purpose: PickPurpose.Base,
  });
  assert.equal(model.state.waiting?.picker.name, 'Cili');
  assert.equal(model.getCountdown().seconds, 20);

  clock.advance(20000);
  cd = model.getCountdown();
  assert.equal(cd.seconds, 0);
  assert.equal(cd.fraction, 0);
  assert.equal(cd.expired, true);
});

test('active picker cannot select or confirm after countdown expires', async () => {
  const sent = [];
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({
    pick: async (idx) => { sent.push(idx); },
    remaining: clock.remaining,
  });

  model.pickRequested({
    validTerritoryIndices: [0, 1],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });
  model.select(0);
  assert.equal(model.canConfirm(), true);

  clock.advance(20000);
  assert.equal(model.getCountdown().expired, true);
  assert.equal(model.canSelect(), false);
  assert.equal(model.canConfirm(), false);
  assert.equal(model.select(1), false);
  assert.equal(await model.confirm(), false);
  assert.deepEqual(sent, []);
});

test('duplicate RoomState or PickRequested preserves selectedIndex for same pick and resets for new pick', () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({ remaining: clock.remaining });

  model.setState({
    phase: GamePhase.BaseSelection,
    settings: { pickTimeoutSeconds: 20 },
    players: [{ id: 'p1', name: 'Anna' }],
    currentPick: {
      pickerPlayerId: 'p1',
      pickDeadlineUtc: '2026-11-27T12:00:20Z',
      validTerritoryIndices: [1, 2, 3],
      purpose: PickPurpose.Base,
    },
  });
  model.select(2);

  model.pickRequested({
    validTerritoryIndices: [1, 2, 3],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });
  assert.equal(model.state.selectedIndex, 2);

  // If a new pick starts with a new deadline, previous selection resets.
  model.pickRequested({
    validTerritoryIndices: [1, 2, 3],
    pickDeadlineUtc: '2026-11-27T12:00:40Z',
    purpose: PickPurpose.Base,
  });
  assert.equal(model.state.selectedIndex, null);
});

test('stale confirm response after pickWaiting, onMapUpdated, or phaseChanged does not overwrite newer state', async () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');

  for (const scenario of ['waiting', 'map', 'phase']) {
    let resolvePick;
    const { model } = makeModel({
      pick: () => new Promise((r) => { resolvePick = r; }),
      remaining: clock.remaining,
    });
    model.pickRequested({
      validTerritoryIndices: [3],
      pickDeadlineUtc: '2026-11-27T12:00:20Z',
      purpose: PickPurpose.Base,
    });
    model.select(3);
    const pending = model.confirm();

    if (scenario === 'waiting') {
      model.pickWaiting({
        picker: { id: 'p2', name: 'Bela' },
        pickDeadlineUtc: '2026-11-27T12:00:40Z',
        purpose: PickPurpose.Base,
      });
    } else if (scenario === 'map') {
      assert.equal(model.onMapUpdated([{ index: 3, ownerPlayerId: 'p1', isBase: true }]), true);
    } else {
      model.phaseChanged(GamePhase.Conquest);
    }

    resolvePick();
    assert.equal(await pending, false);
    if (scenario === 'waiting') assert.equal(model.state.waiting?.picker.name, 'Bela');
    else assert.equal(model.state.waiting, null);
    assert.equal(model.state.busy, false);
  }
});

test('server error mapping distinguishes NotYourPick, InvalidPick, and transient errors', async () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');
  let nextError = new Error('Server error: InvalidPick');
  const { model } = makeModel({
    pick: async () => { throw nextError; },
    remaining: clock.remaining,
  });

  model.pickRequested({
    validTerritoryIndices: [1, 2],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });
  model.select(1);
  assert.equal(await model.confirm(), false);
  assert.equal(model.state.selectedIndex, null);
  assert.notEqual(model.state.request, null);
  assert.match(model.state.error, /no longer valid/i);

  nextError = new Error('Connection lost');
  model.select(2);
  assert.equal(await model.confirm(), false);
  assert.equal(model.state.selectedIndex, 2); // kept so player can retry
  assert.match(model.state.error, /try again/i);

  nextError = new Error('Server error: NotYourPick');
  assert.equal(await model.confirm(), false);
  assert.equal(model.state.request, null);
  assert.equal(model.state.selectedIndex, null);
  assert.match(model.state.error, /already ended/i);
});

test('onMapUpdated with own base and phaseChanged clear active pick and stop countdown', () => {
  const clock = createClockControl('2026-11-27T12:00:00Z');
  const { model } = makeModel({ remaining: clock.remaining });

  model.pickRequested({
    validTerritoryIndices: [0, 1],
    pickDeadlineUtc: '2026-11-27T12:00:20Z',
    purpose: PickPurpose.Base,
  });
  assert.equal(model.onMapUpdated([{ index: 0, ownerPlayerId: 'p1', isBase: true }]), true);
  assert.equal(model.state.request, null);
  assert.equal(model.getCountdown().active, false);

  model.pickWaiting({
    picker: { id: 'p2', name: 'Bela' },
    pickDeadlineUtc: '2026-11-27T12:00:30Z',
    purpose: PickPurpose.Base,
  });
  assert.equal(model.getCountdown().active, true);
  model.phaseChanged(GamePhase.Conquest);
  assert.equal(model.state.waiting, null);
  assert.equal(model.getCountdown().active, false);
});
