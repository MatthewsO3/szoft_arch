import { joinRoom, ApiError } from '../api.js';
import { ErrorCodes } from '../hubNames.js';

/**
 * Wires the join form. `onJoined(joinResponse, roomCode)` is called after a successful join.
 * Returns helpers to prefill the code and show a message.
 */
export function initJoin({ onJoined }) {
  const form = document.getElementById('join-form');
  const codeInput = document.getElementById('code');
  const nameInput = document.getElementById('name');
  const button = document.getElementById('join-btn');
  const errorEl = document.getElementById('join-error');

  const showError = (message) => {
    errorEl.textContent = message;
    errorEl.hidden = !message;
  };

  // Digits only, even if the OS keyboard allows other characters.
  codeInput.addEventListener('input', () => {
    codeInput.value = codeInput.value.replace(/\D/g, '').slice(0, 6);
  });

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const code = codeInput.value.trim();
    const name = nameInput.value.trim();

    if (!/^\d{6}$/.test(code)) return showError('Enter the 6-digit room code.');
    if (!name) return showError('Enter a name.');

    showError('');
    button.disabled = true;
    try {
      onJoined(await joinRoom(code, name), code);
    } catch (err) {
      showError(err instanceof ApiError ? friendly(err) : 'Something went wrong.');
    } finally {
      button.disabled = false;
    }
  });

  return {
    prefill(code) { if (code) codeInput.value = String(code).replace(/\D/g, '').slice(0, 6); },
    message: showError,
    focus() { (codeInput.value ? nameInput : codeInput).focus(); },
  };
}

function friendly(err) {
  switch (err.code) {
    case ErrorCodes.RoomNotFound: return 'Room not found. Check the code.';
    case ErrorCodes.NameTaken: return 'That name is already taken. Pick another.';
    case ErrorCodes.RoomFull: return 'This room is full.';
    case ErrorCodes.NotInLobby: return 'The game has already started.';
    case ErrorCodes.InvalidName: return 'Name must be 1–20 characters.';
    default: return err.message;
  }
}
