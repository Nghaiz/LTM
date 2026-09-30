import test from 'node:test';
import assert from 'node:assert/strict';
import { createMatch, selectSlot, selectSpawn, deploy, fire, reload, damage, respawn, scorePoint } from '../js/gameplay.js';

test('loadout selection updates only requested slot', () => {
  const match = createMatch();
  const next = selectSlot(match, 'primary', 'KAR-98');
  assert.equal(next.loadout.primary, 'KAR-98');
  assert.equal(next.loadout.secondary, 'P-12');
  assert.equal(match.loadout.primary, 'SL-DEFENDER');
});

test('spawn selection is preserved when deploying', () => {
  const match = selectSpawn(createMatch(), 'B');
  const deployed = deploy(match);
  assert.equal(deployed.phase, 'playing');
  assert.equal(deployed.spawn, 'B');
  assert.equal(deployed.health, 100);
  assert.equal(deployed.ammo, 8);
});

test('firing consumes ammunition without going below zero', () => {
  let match = deploy(createMatch());
  for (let i = 0; i < 12; i++) match = fire(match);
  assert.equal(match.ammo, 0);
  assert.equal(reload(match).ammo, 8);
});

test('damage, respawn and score preserve match state', () => {
  const match = scorePoint(damage(deploy(createMatch()), 120), 'blue');
  assert.equal(match.phase, 'eliminated');
  assert.equal(match.health, 0);
  assert.equal(match.score.blue, 1);
  const revived = respawn(match);
  assert.equal(revived.phase, 'deploy');
  assert.equal(revived.health, 100);
  assert.equal(revived.score.blue, 1);
});
