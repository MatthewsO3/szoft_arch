// Lobby screen: live player list. All dynamic text goes through textContent (names are untrusted input).
export function createLobby() {
  const players = new Map(); // id -> player
  let meId = null;

  const listEl = document.getElementById('player-list');
  const countEl = document.getElementById('lobby-count');
  const codeEl = document.getElementById('lobby-code');
  const offlineEl = document.getElementById('lobby-offline');

  function render() {
    const sorted = [...players.values()].sort((a, b) => a.name.localeCompare(b.name));
    listEl.replaceChildren(...sorted.map((p) => {
      const li = document.createElement('li');
      if (!p.connected) li.classList.add('offline');

      const chip = document.createElement('span');
      chip.className = 'chip';
      chip.style.background = p.colorHex;

      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = p.name;

      li.append(chip, name);
      if (p.id === meId) {
        const you = document.createElement('span');
        you.className = 'you';
        you.textContent = 'you';
        li.append(you);
      }
      return li;
    }));
    countEl.textContent = String(players.size);
  }

  return {
    setRoom(code, myId) { codeEl.textContent = code; meId = myId; },
    setState(state) {
      players.clear();
      for (const p of state.players) players.set(p.id, p);
      render();
    },
    upsert(player) { players.set(player.id, player); render(); },
    remove(id) { players.delete(id); render(); },
    showOffline(isOffline) { offlineEl.hidden = !isOffline; },
    reset() { players.clear(); render(); },
  };
}
