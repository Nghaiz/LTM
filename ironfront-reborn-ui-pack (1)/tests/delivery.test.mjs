import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');

test('JavaScript parses without syntax errors', () => {
  execFileSync(process.execPath, ['--check', path.join(root, 'js/app.js')], {stdio:'pipe'});
});

test('three backgrounds and eight previews are valid large images', async () => {
  const backgrounds = (await readdir(path.join(root, 'assets/backgrounds'))).filter(f => f.endsWith('.png'));
  const previews = (await readdir(path.join(root, 'previews'))).filter(f => f.endsWith('.png'));
  assert.equal(backgrounds.length, 3);
  assert.equal(previews.length, 8);
  for (const file of [...backgrounds.map(f => `assets/backgrounds/${f}`), ...previews.map(f => `previews/${f}`)]) {
    const output = execFileSync('identify', ['-format', '%w %h', path.join(root, file)], {encoding:'utf8'});
    const [width,height] = output.split(' ').map(Number);
    assert.ok(width >= 1600 && height >= 900, `${file} is too small`);
  }
});

test('asset library contains independently reusable SVG files', async () => {
  const categories = ['branding','icons','panels','buttons','inputs','badges','decorative'];
  let count = 0;
  for (const category of categories) count += (await readdir(path.join(root, 'assets', category))).filter(f => f.endsWith('.svg')).length;
  assert.ok(count >= 20, `expected at least 20 SVG assets, found ${count}`);
});

test('responsive and accessibility states are present', async () => {
  const css = await readFile(path.join(root, 'css/styles.css'), 'utf8');
  const html = await readFile(path.join(root, 'index.html'), 'utf8');
  assert.match(css, /@media\(max-width:800px\)/);
  assert.match(css, /prefers-reduced-motion/);
  assert.match(css, /:focus-visible/);
  assert.match(html, /aria-live="polite"/);
});

test('all shipped files are non-empty', async () => {
  for (const relative of ['index.html','css/tokens.css','css/styles.css','js/app.js','README.md','ASSET_MANIFEST.md']) {
    assert.ok((await stat(path.join(root, relative))).size > 100);
  }
});
