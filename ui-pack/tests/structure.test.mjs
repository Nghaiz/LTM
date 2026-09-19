import test from 'node:test';
import assert from 'node:assert/strict';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');

const screens = ['main-menu', 'sign-in', 'create-account', 'practice', 'settings', 'rooms', 'create-room', 'waiting-room'];
const files = [
  'index.html', 'css/tokens.css', 'css/styles.css', 'js/app.js',
  'assets/backgrounds/main-menu.png', 'assets/backgrounds/auth.png', 'assets/backgrounds/multiplayer.png',
  'assets/branding/ironfront-reborn-logo.png', 'assets/branding/ironfront-symbol.png',
  'README.md', 'ASSET_MANIFEST.md'
];

test('delivery contains every required file', async () => {
  for (const file of files) await access(path.join(root, file));
});

test('HTML registers all required screens', async () => {
  const html = await readFile(path.join(root, 'index.html'), 'utf8');
  for (const screen of screens) assert.match(html, new RegExp(`data-screen=["']${screen}["']`));
});

test('application exposes screen navigation and multiplayer interactions', async () => {
  const js = await readFile(path.join(root, 'js/app.js'), 'utf8');
  for (const behavior of ['navigate', 'filterRooms', 'createRoom', 'switchTeam', 'toggleReady', 'copyInviteCode']) {
    assert.match(js, new RegExp(`function\\s+${behavior}|const\\s+${behavior}`));
  }
});

test('no background contains a filename suggesting baked UI', async () => {
  const manifest = await readFile(path.join(root, 'ASSET_MANIFEST.md'), 'utf8');
  assert.doesNotMatch(manifest, /screenshot|baked.ui|placeholder/i);
});
