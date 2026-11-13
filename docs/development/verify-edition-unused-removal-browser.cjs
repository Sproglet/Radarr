// Run only inside the disposable unused-edition removal harness.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const id = Number(process.argv[2]);
assert.ok(process.argv.length === 3 && Number.isInteger(id) && id > 0);
const root = 'http://127.0.0.1:17878';
const headers = { 'X-Api-Key': '11112222333344445555666677778888' };

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(25000);
  const writes = [];
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/api/v3/')) writes.push(request.url());
  });
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const api = async (path, data) => {
    const response = data === undefined
      ? await page.request.get(root + '/api/v3/' + path, { headers })
      : await page.request.post(root + '/api/v3/' + path, { headers, data });
    assert.ok(response.ok(), await response.text());
    return response.json();
  };
  const monitoring = async (target, monitored) => {
    const movie = await api('movie/1');
    return api('movieedition/' + id + '/monitoring', {
      movieId: 1, monitored, expectedMovieMonitored: movie.monitored, expectedEditionMonitored: target.monitored,
      expectedMovieFileId: target.movieFileId ?? null, expectedQualityProfileId: target.qualityProfileId,
      expectedRuleRevision: target.ruleRevision, expectedName: target.name,
    });
  };
  try {
    const initial = await api('movieedition/' + id);
    const movie = await api('movie/1');
    const primary = (await api('movieedition?movieId=1')).find(edition => edition.isPrimary);
    assert.ok(primary);
    await page.goto(root + '/movie/-1000001');
    await page.waitForLoadState('networkidle');
    const remove = page.getByRole('button', { name: 'Remove unused edition', exact: true });
    const modal = page.locator('#portal-root');
    await remove.click();
    await modal.getByRole('button', { name: 'Remove unused edition', exact: true }).waitFor();
    assert.ok((await modal.innerText()).includes(initial.name));
    await modal.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.deepEqual(writes, []);
    assert.equal((await api('movieedition/' + id)).id, id);

    let changed = false;
    await page.route('**/api/v3/movieedition/' + id + '/remove', async route => {
      if (!changed) {
        changed = true;
        // A real concurrent actor changes the target after the confirmation snapshot.
        await monitoring(await api('movieedition/' + id), true);
      }
      return route.continue();
    });
    await remove.click();
    let pending = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/remove'));
    await modal.getByRole('button', { name: 'Remove unused edition', exact: true }).click();
    assert.equal((await pending).status(), 409);
    await page.getByText('The movie or edition settings changed. Reload before removing an edition.', { exact: true }).waitFor();
    await modal.getByRole('button', { name: 'Remove unused edition', exact: true }).waitFor({ state: 'hidden' });
    await remove.waitFor({ state: 'hidden' });
    assert.equal((await api('movieedition/' + id)).monitored, true);

    await monitoring(await api('movieedition/' + id), false);
    await page.reload();
    await page.waitForLoadState('networkidle');
    await remove.click();
    pending = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/remove'));
    await modal.getByRole('button', { name: 'Remove unused edition', exact: true }).click();
    const response = await pending;
    assert.equal(response.status(), 200, await response.text());
    assert.deepEqual(response.request().postDataJSON(), {
      movieId: 1, expectedPrimaryEditionId: primary.id, expectedMovieMonitored: movie.monitored,
      expectedEditionMonitored: false, expectedMovieFileId: null, expectedQualityProfileId: initial.qualityProfileId,
      expectedRuleRevision: initial.ruleRevision, expectedName: initial.name,
    });
    await page.getByText(initial.name, { exact: true }).waitFor({ state: 'hidden' });
    assert.equal(writes.length, 2);
    assert.ok(writes.every(url => url.endsWith('/movieedition/' + id + '/remove')));
    assert.deepEqual(errors, []);
    console.log('PASS: unused-edition browser cancellation sends no write; a real concurrent monitoring change returns 409 and refreshes state; explicit fresh confirmation removes only the selected target');
  } catch (error) {
    require('node:fs').writeFileSync('/src/_temp/edition-test-results/edition-unused-removal-browser.html', await page.content());
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
