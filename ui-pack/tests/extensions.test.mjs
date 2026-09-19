import test from 'node:test';
import assert from 'node:assert/strict';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');

test('practice and settings screens are included', async () => {
  const html = await readFile(path.join(root, 'index.html'), 'utf8');
  assert.match(html, /data-screen="practice"/);
  assert.match(html, /data-screen="settings"/);
  assert.match(html, /id="practice-form"/);
  assert.match(html, /id="settings-form"/);
});

test('main menu routes to both new screens', async () => {
  const html = await readFile(path.join(root, 'index.html'), 'utf8');
  assert.match(html, /data-nav="practice"[^>]*>[\s\S]*?Practice Offline/i);
  assert.match(html, /data-nav="settings"[^>]*>[\s\S]*?Settings/i);
});

test('practice configuration can launch directly and settings persist', async () => {
  const js = await readFile(path.join(root, 'js/app.js'), 'utf8');
  for (const behavior of ['startPractice', 'saveSettings', 'resetSettings', 'loadSettings']) {
    assert.match(js, new RegExp(`function\\s+${behavior}|const\\s+${behavior}`));
  }
  assert.match(js, /localStorage\.setItem/);
  assert.match(js, /localStorage\.getItem/);
});

test('new screens have independent previews and updated docs', async () => {
  await access(path.join(root, 'previews/practice.png'));
  await access(path.join(root, 'previews/settings.png'));
  const readme = await readFile(path.join(root, 'README.md'), 'utf8');
  assert.match(readme, /Practice Mode/);
  assert.match(readme, /Settings/);
});

test('create account matches the requested four-field contract', async () => {
  const html = await readFile(path.join(root, 'index.html'), 'utf8');
  for (const field of ['username', 'password', 'repeatPassword', 'displayName']) {
    assert.match(html, new RegExp(`name=["']${field}["']`));
  }
  assert.match(html, /pattern="\[a-z0-9_\]\{3,16\}"/);
});
