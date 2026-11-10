// Run only against the disposable synthetic application used by the recovery harness.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const profileId = Number(process.argv[2]);
const sources = process.argv.slice(3);
assert.ok(profileId > 0 && sources.length === 2 && sources.every(p => p.startsWith('/tmp/radarr-phase3-')));
const root = 'http://127.0.0.1:17878';
const headers = { 'X-Api-Key': '11112222333344445555666677778888' };

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const api = async (path, options = {}) => {
    const response = await page.request.fetch(root + '/api/v3/' + path, { headers, ...options });
    assert.ok(response.ok(), `${response.status()}: ${await response.text()}`);
    return response.status() === 204 ? null : response.json();
  };
  const responseFor = path => {
    const result = page.waitForResponse(r => r.url().endsWith('/api/v3/' + path) && r.request().method() === 'POST');
    result.catch(() => {});
    return result;
  };
  try {
    const profile = await api('qualityprofile/' + profileId);
    const primaryProfile = await api('qualityprofile/1');
    await page.goto(root + '/movie/-1000001');
    const editions = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Editions$/ }) });
    await editions.getByRole('row').filter({ hasText: 'Theatrical IMAX' }).waitFor();
    await editions.getByText('Directors Cut 3D', { exact: true }).waitFor();
    assert.ok((await editions.innerText()).includes('Primary'));
    const modal = page.locator('#portal-root');
    const add = () => modal.getByRole('button', { name: 'Add Edition', exact: true });
    const preview = async () => {
      const pending = responseFor('movieedition/preview');
      await modal.getByRole('button', { name: 'Preview Filenames', exact: true }).click();
      return pending;
    };
    const attribute = async (key, value) => {
      const index = await modal.locator('input[name="key"]').count() - 1;
      await modal.locator('input[name="key"]').nth(index).fill(key);
      await modal.locator('input[name="value"]').nth(index).fill(value);
    };
    const chooseProfile = async () => {
      await modal.getByRole('button', { name: primaryProfile.name, exact: true }).click();
      await page.locator('[class*="EnhancedSelectInput-optionsContainer"]:visible').getByText(profile.name, { exact: true }).click();
    };
    const created = [];
    for (let number = 0; number < 2; number++) {
      await editions.getByRole('button', { name: 'Add Edition', exact: true }).click();
      await modal.locator('input[name="editionName"]').waitFor();
      assert.equal(await add().isDisabled(), true);
      await chooseProfile();
      const name = number === 0 ? 'Synthetic Browser Collector 3D IMAX' : 'Synthetic Browser Directors Cut 3D';
      await modal.locator('input[name="editionName"]').fill(number === 0 ? 'Theatrical IMAX' : name);
      if (number === 0) {
        const collision = await preview();
        assert.equal(collision.status(), 200);
        assert.equal((await collision.json()).namingValid, false);
        assert.equal(await add().isDisabled(), true);
        await modal.locator('input[name="editionName"]').fill(name);
        await attribute('stereoscopic', '3d');
        await attribute('framing', 'imax');
        await attribute('Cut', 'Synthetic Duplicate');
        assert.equal(await modal.getByRole('button', { name: 'Preview Filenames', exact: true }).isDisabled(), true);
        await modal.locator('input[name="key"]').nth(3).fill('branding');
        await modal.locator('input[name="value"]').nth(3).fill('Synthetic Browser Collector');
        await modal.locator('input[name="key"]').first().locator('../..').getByRole('button').click();
        await modal.locator('textarea[name="editionRules"]').fill('[');
        const invalid = await preview();
        assert.equal(invalid.status(), 400);
        assert.equal(await add().isDisabled(), true);
        await modal.locator('textarea[name="editionRules"]').fill('SyntheticBrowserGroup$');
      } else {
        await modal.locator('input[name="value"]').first().fill('directors-cut');
        await attribute('stereoscopic', '3d');
      }
      const valid = await preview();
      assert.equal(valid.status(), 200);
      const body = await valid.json();
      assert.ok(body.namingValid && body.creationEnabled);
      assert.equal(body.proposedEdition.qualityProfileId, profileId);
      assert.equal(body.proposedEdition.attributes.cut, number === 0 ? 'theatrical' : 'directors-cut');
      await add().waitFor({ state: 'visible' });
      if (number === 0) {
        await modal.locator('input[name="editionName"]').fill(name + ' Changed');
        assert.equal(await add().isDisabled(), true);
        await modal.locator('input[name="editionName"]').fill(name);
        assert.equal(await add().isDisabled(), true);
        await preview();
        const naming = await api('config/naming');
        await api('config/naming/' + naming.id, { method: 'PUT', data: { ...naming, renameMovies: false } });
        const pending = responseFor('movieedition');
        await add().click();
        assert.equal((await pending).status(), 409);
        await page.waitForFunction(() => [...document.querySelectorAll('#portal-root button')].find(b => b.textContent.trim() === 'Add Edition')?.disabled === true);
        assert.ok((await modal.innerText()).includes('{Managed Edition}'));
        await api('config/naming/' + naming.id, { method: 'PUT', data: naming });
        await preview();
      }
      const pending = responseFor('movieedition');
      await add().click();
      const response = await pending;
      assert.equal(response.status(), 201);
      const edition = await response.json();
      assert.ok(!edition.monitored && !edition.effectiveMonitored && !edition.isPrimary);
      assert.equal(edition.state, 'missing');
      assert.ok(edition.editionKey.startsWith('custom-'));
      created.push(edition);
      await modal.locator('input[name="editionName"]').waitFor({ state: 'detached' });
      const row = editions.getByRole('row').filter({ hasText: edition.name });
      await row.getByText(profile.name, { exact: true }).waitFor();
      await row.getByText('No file', { exact: true }).waitFor();
      const command = await api('command', { method: 'POST', data: { name: 'ManualImport', importMode: 'copy', files: [{
        movieId: 1, movieEditionId: edition.id, path: sources[number],
        quality: { quality: { id: 7, name: 'Bluray-1080p', source: 'bluray', resolution: 1080 }, revision: { version: 1, real: 0, isRepack: false } },
        languages: [{ id: 1, name: 'English' }], releaseGroup: 'SyntheticBrowserGroup', indexerFlags: 0,
      }] } });
      for (let count = 0; count < 90; count++) {
        const status = await api('command/' + command.id);
        assert.ok(!['failed', 'aborted'].includes(status.status), JSON.stringify(status));
        if (status.status === 'completed') break;
        assert.ok(count < 89, 'Import timed out');
        await page.waitForTimeout(500);
      }
      // SignalR/file-statistic updates refresh the edition row without navigating or reloading.
      await row.getByText(/\.mkv$/).waitFor({ timeout: 30000 });
    }
    assert.deepEqual(errors, []);
    console.log('PASS: movie Editions table, extensible labels/profile/rules and fresh preview/create guards work in the actual browser');
    console.log('PASS: API-created combined editions appear as missing, then update to their imported files without a page reload');
    console.log('CREATED_EDITIONS=' + JSON.stringify(created));
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-management-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 7000));
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
