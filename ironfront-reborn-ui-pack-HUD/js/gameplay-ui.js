import { LOADOUT_OPTIONS, createMatch, selectSlot, selectSpawn, deploy, fire, reload, damage, respawn, scorePoint } from './gameplay.js';

let match = createMatch();
let timer = 0;
let timerId;
const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const labelForSpawn = { A:'BASE CAMP', B:'RIDGE', C:'OUTPOST' };

function render() {
  $$('[data-score-blue]').forEach(node => node.textContent = String(match.score.blue).padStart(2, '0'));
  $$('[data-score-orange]').forEach(node => node.textContent = String(match.score.orange).padStart(2, '0'));
  $$('[data-slot-label]').forEach(node => node.textContent = match.loadout[node.dataset.slotLabel]);
  $$('[data-spawn]').forEach(node => {
    node.classList.toggle('is-selected', match.spawn === node.dataset.spawn);
    node.setAttribute('aria-pressed', String(match.spawn === node.dataset.spawn));
  });
  $('#spawn-name').textContent = `${match.spawn} — ${labelForSpawn[match.spawn]}`;
  $('#hud-spawn').textContent = match.spawn;
  $('#hud-weapon').textContent = match.loadout.primary;
  $('#hud-health').textContent = match.health;
  $('#health-fill').style.width = `${match.health}%`;
  $('#hud-ammo').textContent = String(match.ammo).padStart(2, '0');
  $('#hud-reserve').textContent = match.reserve;
  $('#ammo-pips').innerHTML = Array.from({length:8}, (_,i) => `<i class="${i < match.ammo ? '' : 'is-empty'}"></i>`).join('');
  $('#eliminated-overlay').hidden = match.phase !== 'eliminated';
}

function showDeploy(config = {}) {
  clearInterval(timerId);
  match = createMatch(config.team || 'blue', config);
  timer = 0;
  $('#match-timer').textContent = '00:00';
  render();
  window.IRONFRONT.navigate('deploy');
}

function deployNow() {
  match = deploy(match);
  render();
  window.IRONFRONT.navigate('gameplay');
  clearInterval(timerId);
  timerId = setInterval(() => {
    timer++;
    $('#match-timer').textContent = `${String(Math.floor(timer / 60)).padStart(2, '0')}:${String(timer % 60).padStart(2, '0')}`;
  }, 1000);
}

$$('[data-slot]').forEach(button => button.addEventListener('click', () => {
  const slot = button.dataset.slot;
  const list = LOADOUT_OPTIONS[slot];
  match = selectSlot(match, slot, list[(list.indexOf(match.loadout[slot]) + 1) % list.length]);
  $$('[data-slot]').forEach(node => node.classList.toggle('is-selected', node === button));
  render();
}));
$$('[data-spawn]').forEach(button => button.addEventListener('click', () => {
  match = selectSpawn(match, button.dataset.spawn);
  render();
}));
$('#deploy-button').addEventListener('click', deployNow);
$('#game-fire').addEventListener('click', () => { match = fire(match); render(); });
$('#game-reload').addEventListener('click', () => { match = reload(match); render(); });
$('#game-damage').addEventListener('click', () => { match = damage(match, 25); render(); });
$('#game-score').addEventListener('click', () => { match = scorePoint(match, match.team); render(); });
function backToDeploy() { clearInterval(timerId); match = respawn(match); render(); window.IRONFRONT.navigate('deploy'); }
$('#game-change-kit').addEventListener('click', backToDeploy);
$('#respawn-button').addEventListener('click', backToDeploy);
$('#start-game').addEventListener('click', () => showDeploy({mode:'multiplayer'}));
$('#practice-form').addEventListener('submit', event => {
  const data = new FormData(event.currentTarget);
  showDeploy({mode:'practice', map:data.get('practiceMap'), bots:Number(data.get('botCount')), team:data.get('playerTeam') === 'TEAM ORANGE' ? 'orange' : 'blue'});
});
document.addEventListener('keydown', event => {
  if (window.IRONFRONT.getCurrentScreen() !== 'gameplay') return;
  if (event.code === 'Space' && !event.repeat) { event.preventDefault(); match = fire(match); render(); }
  if (event.key.toLowerCase() === 'r' && !event.repeat) { match = reload(match); render(); }
  if (event.key === 'Escape') backToDeploy();
});
render();
window.IRONFRONT.gameplay = { showDeploy, deployNow, getState: () => structuredClone(match) };
