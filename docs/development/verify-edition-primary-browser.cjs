const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const emptyId = Number(process.argv[2]);
assert.ok(emptyId > 2 && emptyId < 900);
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const base = 'http://127.0.0.1:17878/api/v3/';
  const api = async (path, data) => {
    const response = data ? await page.request.post(base + path, { headers: { 'X-Api-Key': '11112222333344445555666677778888' }, data })
      : await page.request.get(base + path, { headers: { 'X-Api-Key': '11112222333344445555666677778888' } });
    assert.ok(response.ok(), await response.text());
    return response.json();
  };
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    const table = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Editions$/ }) });
    const row = name => table.getByRole('row').filter({ hasText: name });
    const portal = page.locator('#portal-root');
    const confirm = () => portal.getByRole('button', { name: 'Make Primary', exact: true });
    await row('Synthetic Invalid Primary').getByRole('button', { name: 'Make Primary', exact: true }).waitFor();
    assert.equal(await row('Synthetic Invalid Primary').getByRole('button', { name: 'Make Primary', exact: true }).isDisabled(), true);
    await row('Directors Cut 3D').getByRole('button', { name: 'Make Primary', exact: true }).click();
    await portal.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal((await api('movieedition?movieId=1')).find(e => e.isPrimary).id, 1);
    await row('Directors Cut 3D').getByRole('button', { name: 'Make Primary', exact: true }).click();
    const targets = await api('movieedition?movieId=1');
    const empty = targets.find(e => e.id === emptyId);
    await api('movieedition/' + emptyId + '/primary', { movieId: 1, expectedPrimaryEditionId: 1, expectedMovieFileId: null, expectedQualityProfileId: empty.qualityProfileId });
    const rejected = page.waitForResponse(r => r.url().endsWith('/movieedition/2/primary') && r.request().method() === 'POST');
    rejected.catch(() => {});
    await confirm().click();
    assert.equal((await rejected).status(), 409);
    await table.getByText(/changed.*Reload/i).waitFor();
    assert.equal((await api('movie/1')).hasFile, false);
    assert.equal((await api('movie/1')).hasAnyEditionFile, true);
    for (const id of [2, emptyId, 1]) {
      const choices = await api('movieedition?movieId=1');
      const target = choices.find(e => e.id === id);
      await row(target.name).getByRole('button', { name: 'Make Primary', exact: true }).click();
      const pending = page.waitForResponse(r => r.url().endsWith('/movieedition/' + id + '/primary') && r.request().method() === 'POST');
      pending.catch(() => {});
      await confirm().click();
      const response = await pending;
      assert.equal(response.status(), 200);
      assert.ok((await response.json()).isPrimary);
      await row(target.name).getByText('Primary', { exact: true }).waitFor();
      const movie = await api('movie/1');
      assert.equal(movie.qualityProfileId, 1);
      assert.equal(movie.movieFileId, target.movieFileId ?? 0);
      assert.equal(movie.hasFile, !!target.movieFileId);
      assert.equal(movie.lastSearchTime ?? null, target.lastSearchTime ?? null);
      if (target.movieFileId) assert.equal(movie.movieFile.qualityProfileId, target.qualityProfileId);
    }
    assert.deepEqual(errors, []);
    console.log('PASS: actual primary selector confirms/cancels, rejects stale confirmation, recovers and selects populated/empty targets without reloading');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-primary-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
