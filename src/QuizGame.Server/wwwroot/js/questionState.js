import { ErrorCodes } from './hubNames.js';

// The server owns acceptance. Unknown transport failures stay locked until a fresh snapshot arrives.
export function createQuestionState({ send, recover, changed }) {
  let version = 0;
  const state = { question: null, status: 'idle', message: '', result: null };
  return {
    state,
    start(question, answered) {
      if (answered === undefined && state.question?.roundId === question.roundId) return;
      version++;
      Object.assign(state, { question, status: answered ? 'accepted' : 'answering', message: '', result: null });
      changed();
    },
    close(result) {
      if (state.question && state.question.roundId !== result.roundId) return;
      version++;
      Object.assign(state, { status: 'results', result, message: '' });
      changed();
    },
    reset() {
      version++;
      Object.assign(state, { question: null, status: 'idle', message: '', result: null });
      changed();
    },
    async submit(value) {
      if (state.status !== 'answering' || !state.question) return;
      const current = version;
      const roundId = state.question.roundId;
      state.status = 'submitting';
      state.message = '';
      changed();
      try {
        const accepted = await send({ roundId, ...value });
        if (current !== version) return;
        if (accepted?.roundId !== roundId) throw new Error('Missing answer acknowledgment');
        state.status = 'accepted';
      } catch (error) {
        if (current !== version) return;
        const message = String(error.message ?? error);
        if (message.includes(ErrorCodes.AlreadyAnswered)) state.status = 'accepted';
        else if (message.includes(ErrorCodes.AnswerTooLate)) state.status = 'expired';
        else if (message.includes(ErrorCodes.InvalidAnswer)) {
          state.status = 'answering';
          state.message = 'Invalid answer. Please check your input.';
        } else {
          state.status = 'uncertain';
          state.message = 'Checking whether your answer was received…';
          changed();
          try { await recover(); } catch {
            if (current === version) state.message = 'Unable to confirm your answer. Reconnect to check its status.';
          }
        }
      }
      changed();
    },
  };
}
