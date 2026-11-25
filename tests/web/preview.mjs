// Local UI fixture only: exercises the real question UI without the unfinished round backend.
// Run: node tests/web/preview.mjs, then open http://127.0.0.1:5085.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, sep, extname } from 'node:path';

const root = fileURLToPath(new URL('../../src/QuizGame.Server/wwwroot/', import.meta.url));
const fixture = `
import { createQuestion } from '/js/ui/question.js';
import { createClock } from '/js/util/timer.js';
const clock = createClock();
clock.sync(new Date().toISOString());
let current, answer;
const ui = createQuestion({ clock, playerId: () => 'me',
  send: async value => { answer = value; return { roundId: value.roundId }; },
  recover: async () => ui.start(current, !!answer),
});
ui.setOnline(true);
document.getElementById('screen-question').hidden = false;
const toolbar = document.createElement('nav');
toolbar.setAttribute('aria-label', 'Preview controls');
toolbar.style.cssText = 'padding: 1rem; display: flex; gap: .5rem; flex-wrap: wrap';
document.body.prepend(toolbar);
const label = document.createElement('p');
label.textContent = 'Local UI fixture — sample data, no server round';
document.body.prepend(label);
function button(text, action) {
  const b = document.createElement('button'); b.textContent = text;
  b.addEventListener('click', action); toolbar.append(b);
}
function start(type) {
  answer = null;
  current = { roundId: crypto.randomUUID(), type,
    serverDeadlineUtc: new Date(Date.now() + 20000).toISOString(),
    options: type === 0 ? [
      { label: 'A', text: 'Budapest' }, { label: 'B', text: 'Vienna' },
      { label: 'C', text: 'Prague' }, { label: 'D', text: 'Bratislava' },
    ] : [], unit: type === 1 ? 'km' : null,
  };
  ui.start(current);
}
button('Multiple choice', () => start(0));
button('Estimation', () => start(1));
button('Close round', () => ui.close({ roundId: current.roundId,
  correctOption: current.type === 0 ? 'A' : null,
  correctNumber: current.type === 1 ? 42 : null,
  ranking: answer ? [{ playerId: 'me', name: 'You', rank: 1,
    isCorrect: current.type === 0 ? answer.option === 'A' : null,
    distance: current.type === 1 ? Math.abs(answer.number - 42) : null,
  }] : [], winnerPlayerIds: answer && (current.type === 1 || answer.option === 'A') ? ['me'] : [],
}));
start(0);
`;

createServer(async (request, response) => {
  try {
    const pathname = new URL(request.url, 'http://localhost').pathname;
    if (pathname === '/_preview.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(fixture);
      return;
    }
    const path = resolve(root, '.' + decodeURIComponent(pathname === '/' ? '/index.html' : pathname));
    if (!path.startsWith(resolve(root) + sep)) { response.writeHead(403); response.end(); return; }
    let content = await readFile(path);
    if (path === resolve(root, 'index.html')) content = content.toString()
      .replace('<script src="js/lib/signalr.min.js"></script>', '')
      .replace('src="js/main.js"', 'src="/_preview.js"');
    response.writeHead(200, { 'Content-Type': ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' })[extname(path)] ?? 'application/octet-stream' });
    response.end(content);
  } catch { response.writeHead(404); response.end(); }
}).listen(5085, '127.0.0.1', () => console.log('Question UI preview: http://127.0.0.1:5085'));
