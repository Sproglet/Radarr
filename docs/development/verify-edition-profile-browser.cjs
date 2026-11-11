const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const secondaryProfile = Number(process.argv[2]);
assert.ok(secondaryProfile > 1 && secondaryProfile < 100);
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const api = async (path, data, method = data ? 'POST' : 'GET') => {
    const response = await page.request.fetch('http://127.0.0.1:17878/api/v3/' + path,
      { method, data, headers: { 'X-Api-Key': '11112222333344445555666677778888' } });
    const body = await response.text();
    assert.ok(response.ok(), body);
    return body ? JSON.parse(body) : undefined;
  };
  const profileBody = async (id, qualityProfileId) => {
    const target = await api('movieedition/' + id);
    const movie = await api('movie/1');
    return { movieId: 1, qualityProfileId, expectedMovieMonitored: movie.monitored,
      expectedEditionMonitored: target.monitored, expectedMovieFileId: target.movieFileId ?? null,
      expectedQualityProfileId: target.qualityProfileId, expectedRuleRevision: target.ruleRevision, expectedName: target.name };
  };
  const table = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Editions$/ }) });
  const files = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Files$/ }) });
  const row = name => table.getByRole('row').filter({ hasText: name });
  const file = name => files.getByRole('row').filter({ hasText: name }).filter({ hasText: '.mkv' });
  const portal = page.locator('#portal-root');
  const select = () => portal.locator('select[name="editionQualityProfile"]');
  const save = () => portal.getByRole('button', { name: 'Save', exact: true });
  const open = async name => row(name).getByRole('button', { name: 'Change Edition Profile', exact: true }).click();
  const apply = async (id, status = 200) => {
    const response = page.waitForResponse(r => r.url().endsWith('/movieedition/' + id + '/qualityprofile') && r.request().method() === 'POST');
    response.catch(() => {});
    await save().click();
    const completed = await response;
    assert.equal(completed.status(), status);
  };
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    await row('Directors Cut 3D').getByRole('button', { name: 'Change Edition Profile', exact: true }).waitFor();
    const original = await api('movieedition?movieId=1');
    const missing = original.find(e => e.name === 'Synthetic Profile Repair');
    const empty = original.find(e => e.name === 'Synthetic Profile Collector 3D IMAX');
    let fail = true;
    await page.route('**/api/v3/qualityprofile', route => {
      if (route.request().method() !== 'GET' || !fail) return route.continue();
      fail = false;
      return route.fulfill({ status: 500, contentType: 'application/json', body: '{"message":"Synthetic profile load failure"}' });
    });
    await open('Directors Cut 3D');
    await portal.getByText('Unable to load quality profiles. Close this dialog and try again.', { exact: true }).waitFor();
    assert.equal(await save().isDisabled(), true);
    await portal.getByRole('button', { name: 'Cancel', exact: true }).click();
    await open('Directors Cut 3D');
    await select().selectOption(String(secondaryProfile));
    await portal.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal((await api('movieedition/2')).qualityProfileId, 1);
    await open('Directors Cut 3D');
    await select().selectOption(String(secondaryProfile));
    await api('movieedition/2/qualityprofile', await profileBody(2, secondaryProfile));
    await apply(2, 409);
    await table.getByText(/changed.*Reload/i).waitFor();
    await file('Directors Cut 3D').getByText('+77', { exact: true }).waitFor();
    await open('Directors Cut 3D');
    await select().selectOption('1');
    await apply(2);
    await file('Directors Cut 3D').getByText('+11', { exact: true }).waitFor();
    await open('Theatrical IMAX');
    await select().selectOption('1');
    await apply(1);
    await file('Theatrical IMAX').getByText('+11', { exact: true }).waitFor();
    assert.equal((await api('movie/1')).qualityProfileId, 1);
    await open('Theatrical IMAX');
    await select().selectOption(String(secondaryProfile));
    await apply(1);
    await file('Theatrical IMAX').getByText('+77', { exact: true }).waitFor();
    for (const id of [1, secondaryProfile]) {
      await open(empty.name);
      await select().selectOption(String(id));
      await apply(empty.id);
      await row(empty.name).getByText((await api('qualityprofile/' + id)).name, { exact: true }).waitFor();
      assert.equal((await api('movieedition/' + empty.id)).state, 'missing');
    }
    await open(missing.name);
    await select().waitFor({ state: 'visible' });
    assert.equal(await save().isDisabled(), true);
    await select().selectOption('1');
    await apply(missing.id);
    await row(missing.name).getByText((await api('qualityprofile/1')).name, { exact: true }).waitFor();
    assert.equal((await api('movieedition/' + missing.id)).state, 'missing');
    const definition = { ...await api('qualityprofile/1'), name: 'Synthetic Picker Removed Profile' };
    delete definition.id;
    const removed = await api('qualityprofile', definition);
    await open('Directors Cut 3D');
    await select().selectOption(String(removed.id));
    await api('qualityprofile/' + removed.id, undefined, 'DELETE');
    if (await save().isDisabled()) await portal.getByRole('button', { name: 'Cancel', exact: true }).click();
    else await apply(2, 404);
    assert.equal((await api('movieedition/2')).qualityProfileId, 1);
    await open('Directors Cut 3D');
    await select().selectOption(String(secondaryProfile));
    await api('movie/1', { ...await api('movie/1'), monitored: true }, 'PUT');
    await apply(2, 409);
    await table.getByText(/changed.*Reload/i).waitFor();
    await api('movie/1', { ...await api('movie/1'), monitored: false }, 'PUT');
    const final = await api('movieedition?movieId=1');
    const definitions = list => list.map(e => [e.id, e.editionKey, e.name, e.monitored, e.movieFileId ?? null, e.attributes, e.matchRules, e.ruleRevision]);
    assert.deepEqual(definitions(final), definitions(original));
    assert.deepEqual(final.map(e => e.qualityProfileId), original.map(e => e.id === missing.id ? 1 : e.qualityProfileId));
    assert.equal((await api('movie/1')).qualityProfileId, 1);
    assert.deepEqual(errors, []);
    console.log('PASS: actual profile picker handles load failure/cancel, stale target/master and deleted choices, updates file scores, repairs missing profiles and preserves definitions/defaults');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-profile-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
