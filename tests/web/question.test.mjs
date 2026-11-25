import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createQuestionState } from '../../src/QuizGame.Server/wwwroot/js/questionState.js';
import { createClock } from '../../src/QuizGame.Server/wwwroot/js/util/timer.js';

const question = { roundId: 'round-1', serverDeadlineUtc: '2026-11-25T12:00:20Z' };
function model(send, recover = async () => {}) {
  return createQuestionState({ send, recover, changed() {} });
}

test('countdown uses server time and elapsed monotonic time, including latency', () => {
  let elapsed = 100;
  const clock = createClock(() => elapsed);
  clock.sync('2026-11-25T12:00:00Z', 200);
  assert.equal(clock.remaining(question.serverDeadlineUtc), 19900);
  elapsed += 5000;
  assert.equal(clock.remaining(question.serverDeadlineUtc), 14900);
  elapsed += 60000; // background tab resumes after deadline
  assert.equal(clock.remaining(question.serverDeadlineUtc), 0);
});

test('bad clock samples do not corrupt a valid clock', () => {
  const clock = createClock(() => 0);
  clock.sync('2026-11-25T12:00:00Z');
  clock.sync('invalid');
  assert.equal(clock.remaining(question.serverDeadlineUtc), 20000);
});

test('double taps send only one answer and wait for acknowledgment', async () => {
  let resolve;
  const sent = [];
  const round = model(answer => { sent.push(answer); return new Promise(r => { resolve = r; }); });
  round.start(question);
  const pending = round.submit({ option: 'B' });
  await round.submit({ option: 'C' });
  assert.equal(round.state.status, 'submitting');
  assert.deepEqual(sent, [{ roundId: question.roundId, option: 'B' }]);
  resolve({ roundId: question.roundId });
  await pending;
  assert.equal(round.state.status, 'accepted');
});

test('numeric answers including zero are submitted unchanged', async () => {
  let sent;
  const round = model(async answer => { sent = answer; return { roundId: question.roundId }; });
  round.start(question);
  await round.submit({ number: 0 });
  assert.deepEqual(sent, { roundId: question.roundId, number: 0 });
});

for (const [code, status] of [['AlreadyAnswered', 'accepted'], ['AnswerTooLate', 'expired'], ['InvalidAnswer', 'answering']]) {
  test(`${code} has a defined recovery state`, async () => {
    const round = model(async () => { throw new Error(`Server error: ${code}`); });
    round.start(question);
    await round.submit({ option: 'A' });
    assert.equal(round.state.status, status);
  });
}

test('lost acknowledgment stays locked until a snapshot confirms acceptance', async () => {
  let recoveries = 0;
  const round = model(async () => { throw new Error('Connection lost'); }, async () => { recoveries++; });
  round.start(question);
  await round.submit({ option: 'A' });
  assert.equal(round.state.status, 'uncertain');
  assert.equal(recoveries, 1);
  await round.submit({ option: 'B' });
  assert.equal(recoveries, 1);
  round.start(question, true);
  assert.equal(round.state.status, 'accepted');
});

test('a missing or wrong acknowledgment is not treated as acceptance', async () => {
  for (const reply of [undefined, { roundId: 'other' }]) {
    const round = model(async () => reply);
    round.start(question);
    await round.submit({ option: 'A' });
    assert.equal(round.state.status, 'uncertain');
  }
});

test('an old submission cannot overwrite a newer round or closed results', async () => {
  for (const next of ['new-round', 'results', 'reset']) {
    let resolve;
    const round = model(() => new Promise(r => { resolve = r; }));
    round.start(question);
    const pending = round.submit({ option: 'A' });
    if (next === 'new-round') round.start({ ...question, roundId: 'round-2' });
    else if (next === 'results') round.close({ roundId: question.roundId });
    else round.reset();
    const status = round.state.status;
    resolve({ roundId: question.roundId });
    await pending;
    assert.equal(round.state.status, status);
  }
});

test('duplicate start events do not unlock accepted answers, but snapshots are authoritative', () => {
  const round = model(async () => {});
  round.start(question, true);
  round.start(question);
  assert.equal(round.state.status, 'accepted');
  round.start(question, false);
  assert.equal(round.state.status, 'answering');
});

test('results for another round do not replace the active question', () => {
  const round = model(async () => {});
  round.start(question);
  round.close({ roundId: 'older-round' });
  assert.equal(round.state.status, 'answering');
});
