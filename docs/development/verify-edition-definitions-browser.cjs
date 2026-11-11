// Only run against the disposable, synthetic application in the Python harness.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const emptyId = Number(process.argv[2]);
const moviePath = process.argv[3];
assert.ok(emptyId > 2 && moviePath.startsWith('/tmp/radarr-phase3-') && moviePath.includes('/movies/'));
const root = 'http://127.0.0.1:17878';
const headers = { 'X-Api-Key': '11112222333344445555666677778888' };

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const api = async (route, data, method) => {
    const response = await page.request.fetch(root + '/api/v3/' + route, { headers, data, method: method ?? (data ? 'POST' : 'GET') });
    assert.ok(response.ok(), `${response.status()}: ${await response.text()}`);
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  };
  const definition = async (eid, changes = {}) => {
    const edition = await api('movieedition/' + eid);
    const movie = await api('movie/1');
    return { movieId: 1, name: edition.name, attributes: edition.attributes, matchRules: edition.matchRules,
      expectedMovieMonitored: movie.monitored, expectedEditionMonitored: edition.monitored,
      expectedMovieFileId: edition.movieFileId ?? null, expectedQualityProfileId: edition.qualityProfileId,
      expectedRuleRevision: edition.ruleRevision, expectedName: edition.name, ...changes };
  };
  const responseFor = (eid, preview) => {
    const pending = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith(`/api/v3/movieedition/${eid}/definition${preview ? '/preview' : ''}`));
    pending.catch(() => {});
    return pending;
  };
  try {
    const before = await api('movieedition?movieId=1');
    const beforeMovie = await api('movie/1');
    const beforeFiles = (await api('moviefile?movieId=1')).map(f => [f.id, f.movieEditionId, f.relativePath, f.size]);
    await page.goto(root + '/movie/-1000001');
    const editions = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Editions$/ }) });
    const modal = page.locator('#portal-root');
    const save = () => modal.getByRole('button', { name: 'Save', exact: true });
    const input = () => modal.locator('input[name="editionDefinitionName"]');
    const rules = () => modal.locator('textarea[name="editionDefinitionRules"]');
    const open = async eid => {
      const edition = await api('movieedition/' + eid);
      const row = editions.getByRole('row').filter({ hasText: edition.name });
      await row.getByRole('button', { name: 'Edit Definition', exact: true }).click();
      await input().waitFor();
      assert.equal(await input().inputValue(), edition.name);
      assert.equal(await save().isDisabled(), true);
    };
    const preview = async (eid, status = 200) => {
      const pending = responseFor(eid, true);
      await modal.getByRole('button', { name: 'Preview Definition', exact: true }).click();
      const response = await pending;
      assert.equal(response.status(), status, await response.text());
      return response.json();
    };
    const submit = async (eid, status = 200) => {
      const pending = responseFor(eid, false);
      await save().click();
      const response = await pending;
      assert.equal(response.status(), status, await response.text());
      await input().waitFor({ state: 'hidden' });
      return response.json();
    };
    const attribute = async (key, value) => {
      const index = await modal.locator('input[name="key"]').count() - 1;
      await modal.locator('input[name="key"]').nth(index).fill(key);
      await modal.locator('input[name="value"]').nth(index).fill(value);
    };

    await open(2);
    assert.ok((await modal.innerText()).includes('Use Rename separately'));
    const sample = () => modal.locator('input[name="editionSampleTitle"]');
    await sample().fill('Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup');
    let classification = (await preview(2)).sampleClassification;
    assert.equal(classification.status, 'matched');
    assert.deepEqual(classification.candidateEditionIds, [1]);
    assert.ok((await modal.innerText()).includes('Theatrical IMAX (1)'));
    await sample().fill('Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup');
    assert.equal(await save().isDisabled(), true);
    classification = (await preview(2)).sampleClassification;
    assert.equal(classification.status, 'unconfigured');
    await sample().fill('???');
    classification = (await preview(2)).sampleClassification;
    assert.equal(classification.status, 'invalid');
    await rules().fill('SyntheticOverlap');
    await sample().fill('Synthetic.Lunar.Voyage.2026.SyntheticOverlap.1080p.BluRay-SyntheticGroup');
    classification = (await preview(2)).sampleClassification;
    assert.equal(classification.status, 'ambiguous');
    assert.deepEqual(classification.candidateEditionIds, [2, emptyId]);
    assert.ok((await modal.innerText()).includes('Make the matching rules exclusive'));
    await sample().fill('');
    await input().fill('Synthetic Cancelled Definition');
    await modal.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.deepEqual(await api('movieedition?movieId=1'), before);

    await open(2);
    await rules().fill('[');
    await preview(2, 400);
    assert.equal(await save().isDisabled(), true);
    await rules().fill('');
    await input().fill('Theatrical IMAX');
    assert.equal((await preview(2)).editingEnabled, false);
    assert.equal(await save().isDisabled(), true);
    await input().fill('Synthetic Browser Directors 3D');
    await attribute('framing', 'imax');
    await attribute('branding', 'Synthetic Browser Collector');
    let proposed = await preview(2);
    assert.equal(proposed.editingEnabled, true);
    assert.ok((await modal.innerText()).includes('stereoscopic: 3d'));
    assert.ok((await modal.innerText()).includes('framing: imax'));
    await input().fill('Synthetic Browser Directors 3D Collector');
    assert.equal(await save().isDisabled(), true);
    await preview(2);
    const updated = await submit(2);
    assert.equal(updated.ruleRevision, before.find(e => e.id === 2).ruleRevision + 1);
    assert.equal(updated.movieFileId, before.find(e => e.id === 2).movieFileId);
    assert.deepEqual(updated.attributes, { cut: 'directors-cut', stereoscopic: '3d', framing: 'imax', branding: 'Synthetic Browser Collector' });

    // A stale target cannot be overwritten by an already approved browser draft.
    await open(2);
    await input().fill('Synthetic Stale Definition');
    await preview(2);
    await api('movieedition/2/definition', await definition(2, { matchRules: ['SyntheticConcurrentRule$'] }));
    await submit(2, 409);
    assert.equal((await api('movieedition/2')).name, updated.name);

    // A newly reserved disk basename invalidates an earlier human preview.
    await open(2);
    await input().fill('Synthetic Browser Collision');
    proposed = await preview(2);
    const relative = proposed.samples.find(s => s.movieEditionId === 2).relativePath;
    const destination = path.resolve(moviePath, relative.replace(/\.[^.]+$/, '.mp4'));
    assert.ok(destination.startsWith(moviePath + path.sep));
    fs.writeFileSync(destination, 'Synthetic reserved video', { flag: 'wx' });
    try {
      await submit(2, 409);
      assert.equal((await api('movieedition/2')).name, updated.name);
    } finally {
      fs.unlinkSync(destination);
    }

    // A failed preview must clear approval without issuing a save.
    await open(2);
    await page.route('**/api/v3/movieedition/2/definition/preview', route => route.fulfill({ status: 500,
      contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic preview failure' }) }), { times: 1 });
    await preview(2, 500);
    assert.equal(await save().isDisabled(), true);
    await modal.getByRole('button', { name: 'Cancel', exact: true }).click();

    await open(emptyId);
    await input().fill('Synthetic Empty Browser 3D IMAX');
    await attribute('stereoscopic', '3d');
    await attribute('framing', 'imax');
    await rules().fill('SyntheticEmptyBrowser$');
    assert.equal((await preview(emptyId)).editingEnabled, true);
    const empty = await submit(emptyId);
    assert.equal(empty.state, 'missing');
    assert.deepEqual(empty.attributes, { cut: 'theatrical', branding: 'Synthetic Definition Collector', stereoscopic: '3d', framing: 'imax' });

    await open(1);
    await input().fill('Synthetic Theatrical IMAX Browser');
    await preview(1);
    const primary = await submit(1);
    assert.equal(primary.ruleRevision, before.find(e => e.id === 1).ruleRevision);
    assert.equal(primary.isPrimary, true);

    await open(1);
    await input().fill('Synthetic Stale Master');
    await preview(1);
    let movie = await api('movie/1');
    await api('movie/1', { ...movie, monitored: !movie.monitored }, 'PUT');
    await submit(1, 409);
    movie = await api('movie/1');
    await api('movie/1', { ...movie, monitored: beforeMovie.monitored }, 'PUT');

    // Restore definitions through the guarded API so the Python lifecycle checks
    // can repeat their initial attribute assertions. Revision increases remain.
    for (const edition of before) {
      await api('movieedition/' + edition.id + '/definition', await definition(edition.id,
        { name: edition.name, attributes: edition.attributes, matchRules: edition.matchRules }));
    }
    const restored = await api('movieedition?movieId=1');
    for (const edition of restored) {
      const original = before.find(e => e.id === edition.id);
      for (const key of ['id', 'movieId', 'editionKey', 'name', 'attributes', 'matchRules', 'qualityProfileId', 'monitored', 'movieFileId', 'isPrimary']) {
        assert.deepEqual(edition[key], original[key], key);
      }
    }
    assert.deepEqual((await api('moviefile?movieId=1')).map(f => [f.id, f.movieEditionId, f.relativePath, f.size]), beforeFiles);
    const finalMovie = await api('movie/1');
    assert.equal(finalMovie.qualityProfileId, beforeMovie.qualityProfileId);
    assert.equal(finalMovie.movieFileId, beforeMovie.movieFileId);
    assert.deepEqual(errors, []);
    console.log('PASS: actual definition editor shows read-only sample matching/unconfigured/ambiguity and handles cancel, invalid rules/duplicate labels, preview invalidation/failure, stale target/master, post-preview disk collisions, combined attributes and independent primary/secondary/empty edits without moving files');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
