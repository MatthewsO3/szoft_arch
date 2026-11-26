import { QuestionType, RoundKind } from '../hubNames.js';
import { createQuestionState } from '../questionState.js';

export function createQuestion({ clock, send, recover, playerId }) {
  const options = document.getElementById('question-options');
  const form = document.getElementById('estimate-form');
  const input = document.getElementById('estimate');
  const submit = document.getElementById('estimate-submit');
  const timer = document.getElementById('question-time');
  const message = document.getElementById('question-status');
  const result = document.getElementById('round-result');
  let online = false;
  let interval;
  let validationMessage = '';
  const model = createQuestionState({ send, recover, changed: renderStatus });

  function canAnswer() {
    return online && model.state.status === 'answering' &&
      clock.remaining(model.state.question.serverDeadlineUtc) > 0;
  }

  function renderStatus() {
    const state = model.state;
    const remaining = state.question ? clock.remaining(state.question.serverDeadlineUtc) : 0;
    timer.textContent = state.status === 'results' ? 'Round closed' : `${Math.ceil(remaining / 1000)} s`;
    for (const button of options.children) button.disabled = !canAnswer();
    input.disabled = submit.disabled = !canAnswer();
    const messages = {
      submitting: 'Sending answer…', accepted: 'Answer received. Waiting for the result…',
      expired: 'Time is up. Waiting for the result…', results: '',
    };
    message.textContent = state.message || (messages[state.status] ??
      ((remaining > 0 && online ? validationMessage : '') ||
      (!online ? 'Disconnected. Reconnect before answering.' : remaining <= 0 ? 'Time is up. Waiting for the result…' : 'Choose your answer. Your first answer is final.')));
  }

  function start(question, answered) {
    if (answered === undefined && model.state.question?.roundId === question.roundId) return;
    clearInterval(interval);
    validationMessage = '';
    options.replaceChildren();
    result.hidden = true;
    input.value = '';
    const estimation = question.type === QuestionType.Estimation;
    options.hidden = estimation;
    form.hidden = !estimation;
    document.getElementById('estimate-unit').textContent = question.unit ?? '';
    for (const option of estimation ? [] : question.options) {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = `${option.label} — ${option.text}`;
      button.addEventListener('click', () => {
        if (canAnswer()) model.submit({ option: option.label });
      });
      options.append(button);
    }
    model.start(question, answered);
    interval = setInterval(renderStatus, 100);
  }

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (!canAnswer()) return;
    const text = input.value.trim().replace(',', '.');
    if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text) || !Number.isFinite(Number(text))) {
      validationMessage = 'Enter a valid number.';
      renderStatus();
      input.focus();
      return;
    }
    validationMessage = '';
    model.submit({ number: Number(text) });
  });

  return {
    start,
    setOnline(value) { online = value; if (model.state.question) renderStatus(); },
    close(data) {
      if (model.state.question && model.state.question.roundId !== data.roundId) return false;
      model.close(data);
      clearInterval(interval);
      options.hidden = form.hidden = true;
      result.hidden = false;
      const own = data.ranking.find(row => row.playerId === playerId());
      document.getElementById('correct-answer').textContent = `Correct answer: ${data.correctOption ?? data.correctNumber ?? '—'}`;
      document.getElementById('personal-result').textContent = own
        ? `Rank ${own.rank}${own.distance != null ? ` · Distance: ${own.distance}` : own.isCorrect != null ? own.isCorrect ? ' · Correct' : ' · Incorrect' : ' · No answer recorded'}${own.wonTerritoryIndex != null ? ' · Won a tile!' : own.bonusAwarded != null ? ` · +${own.bonusAwarded} bonus` : data.winnerPlayerIds.includes(own.playerId) ? ' · Winner!' : ''}`
        : 'No answer recorded for you this round.';
      const ranking = document.getElementById('round-ranking');
      ranking.replaceChildren();
      for (const row of data.ranking) {
        const item = document.createElement('li');
        item.textContent = `${row.rank}. ${row.name}${row.distance != null ? ` — distance ${row.distance}` : row.elapsedMs != null ? ` — ${row.elapsedMs} ms` : ''}`;
        ranking.append(item);
      }
      return true;
    },
    reset() { clearInterval(interval); model.reset(); options.replaceChildren(); result.hidden = true; },
  };
}
