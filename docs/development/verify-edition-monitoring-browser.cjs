const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
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
    assert.ok(response.ok(), await response.text());
    return response.json();
  };
  const setTarget = async (id, monitored) => {
    const target = await api('movieedition/' + id);
    const movie = await api('movie/1');
    return api('movieedition/' + id + '/monitoring', { movieId: 1, monitored, expectedMovieMonitored: movie.monitored,
      expectedEditionMonitored: target.monitored, expectedMovieFileId: target.movieFileId ?? null,
      expectedQualityProfileId: target.qualityProfileId, expectedRuleRevision: target.ruleRevision, expectedName: target.name });
  };
  const setMaster = async monitored => api('movie/1', { ...await api('movie/1'), monitored }, 'PUT');
  const table = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Editions$/ }) });
  const row = name => table.getByRole('row').filter({ hasText: name });
  const portal = page.locator('#portal-root');
  const confirm = async (id, label, status = 200) => {
    const pending = page.waitForResponse(r => r.url().endsWith('/movieedition/' + id + '/monitoring') && r.request().method() === 'POST');
    pending.catch(() => {});
    await portal.getByRole('button', { name: label, exact: true }).click();
    const response = await pending;
    assert.equal(response.status(), status);
    return response.json();
  };
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    await row('Synthetic Invalid Inactive').getByRole('button', { name: 'Monitor Edition', exact: true }).waitFor();
    assert.equal(await row('Synthetic Invalid Inactive').getByRole('button', { name: 'Monitor Edition', exact: true }).isDisabled(), true);
    const original = await api('movieedition?movieId=1');
    const activeInvalid = original.find(e => e.name === 'Synthetic Invalid Active');
    await row('Directors Cut 3D').getByRole('button', { name: 'Monitor Edition', exact: true }).click();
    await portal.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal((await api('movieedition/2')).monitored, false);
    // Another operator changes the target after this dialog captures its snapshot.
    await row('Directors Cut 3D').getByRole('button', { name: 'Monitor Edition', exact: true }).click();
    await setTarget(2, true);
    await confirm(2, 'Monitor Edition', 409);
    await table.getByText(/changed.*Reload/i).waitFor();
    await row('Directors Cut 3D').getByRole('button', { name: 'Unmonitor Edition', exact: true }).click();
    await confirm(2, 'Unmonitor Edition');
    await row('Directors Cut 3D').getByRole('button', { name: 'Monitor Edition', exact: true }).waitFor();
    // A concurrent change to the master switch must also invalidate confirmation.
    await row('Directors Cut 3D').getByRole('button', { name: 'Monitor Edition', exact: true }).click();
    await setMaster(false);
    await confirm(2, 'Monitor Edition', 409);
    await table.getByText(/changed.*Reload/i).waitFor();
    await row('Synthetic Monitoring Collector 3D IMAX').getByText(/Movie monitoring is off/).waitFor();
    await row('Directors Cut 3D').getByRole('button', { name: 'Monitor Edition', exact: true }).click();
    await portal.getByText('Movie monitoring is off. This preference takes effect when movie monitoring is enabled.', { exact: true }).waitFor();
    const enabled = await confirm(2, 'Monitor Edition');
    assert.ok(enabled.monitored && !enabled.effectiveMonitored);
    await row('Directors Cut 3D').getByText(/Movie monitoring is off/).waitFor();
    assert.equal((await api('movie/1')).monitored, false);
    await row('Synthetic Invalid Active').getByRole('button', { name: 'Unmonitor Edition', exact: true }).click();
    const disabled = await confirm(activeInvalid.id, 'Unmonitor Edition');
    assert.equal(disabled.monitored, false);
    assert.equal(disabled.state, 'invalid');
    await row('Synthetic Invalid Active').getByRole('button', { name: 'Monitor Edition', exact: true }).waitFor();
    assert.equal(await row('Synthetic Invalid Active').getByRole('button', { name: 'Monitor Edition', exact: true }).isDisabled(), true);
    await setMaster(true);
    await page.reload();
    await row('Directors Cut 3D').getByRole('button', { name: 'Unmonitor Edition', exact: true }).click();
    await confirm(2, 'Unmonitor Edition');
    // Existing primary-selection actions remain functional alongside monitoring.
    for (const name of ['Directors Cut 3D', 'Theatrical IMAX']) {
      await row(name).getByRole('button', { name: 'Make Primary', exact: true }).click();
      await portal.getByRole('button', { name: 'Make Primary', exact: true }).click();
      await row(name).getByText('Primary', { exact: true }).waitFor();
    }
    const final = await api('movieedition?movieId=1');
    assert.deepEqual(final, original.map(e => e.id === activeInvalid.id ? { ...e, monitored: false, effectiveMonitored: false } : e));
    assert.equal((await api('movie/1')).monitored, true);
    assert.deepEqual(errors, []);
    console.log('PASS: actual monitoring UI cancels, rejects stale target/master confirmations, handles inactive masters and damaged targets, and preserves primary actions without file/profile/rule changes');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-monitoring-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
