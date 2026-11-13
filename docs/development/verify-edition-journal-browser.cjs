// Run only inside the disposable synthetic journal harness.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const mode = process.argv[2];
assert.ok(['prepared', 'accepted'].includes(mode));
const finishedHistory = process.argv[3] === '--finished-history';
assert.ok(process.argv.length === 3 || (process.argv.length === 4 && finishedHistory));
const root = 'http://127.0.0.1:17878';

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(25000);
  const errors = [];
  const writes = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/api/v3/')) writes.push(request.url());
  });
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  try {
    await page.goto(root + '/movie/-1000001');
    await page.getByRole('heading', { name: 'Submission review', exact: true }).waitFor();
    if (finishedHistory) assert.equal(await page.getByText('Settled', { exact: true }).count(), 100);
    const repair = page.getByRole('button', { name: 'Repair saved history', exact: true });
    if (mode === 'prepared') {
      await page.getByText('Outcome unknown', { exact: true }).waitFor();
      assert.equal(await repair.count(), 0);
      assert.deepEqual(writes, []);
    } else {
      await repair.click();
      const modal = page.locator('#portal-root');
      await modal.getByRole('button', { name: 'Cancel', exact: true }).click();
      assert.deepEqual(writes, []);

      let rejectOnce = true;
      await page.route('**/api/v3/movieedition/submission/*/reconcile', async route => {
        if (rejectOnce) {
          rejectOnce = false;
          return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic stale submission; reload.' }) });
        }
        return route.continue();
      });
      await repair.click();
      await modal.getByRole('button', { name: 'Repair saved history', exact: true }).click();
      await page.getByText('Synthetic stale submission; reload.', { exact: true }).waitFor();
      await modal.getByRole('button', { name: 'Repair saved history', exact: true }).waitFor({ state: 'hidden' });

      await repair.click();
      assert.ok((await modal.innerText()).includes('No download request will be sent.'));
      const pending = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/reconcile'));
      await modal.getByRole('button', { name: 'Repair saved history', exact: true }).click();
      const response = await pending;
      assert.equal(response.status(), 200, await response.text());
      assert.deepEqual(response.request().postDataJSON(), { movieId: 1, movieEditionId: 2, expectedState: 'accepted', expectedDownloadId: 'synthetic-download' });
      await page.getByText('History recorded', { exact: true }).waitFor();
      assert.equal(writes.length, 2);
      assert.ok(writes.every(url => url.includes('/movieedition/submission/') && url.endsWith('/reconcile')));
    }
    assert.deepEqual(errors, []);
    console.log('PASS: journal UI ' + mode + ' review/confirmation/conflict/reload flow sends only explicit history repair requests');
  } catch (error) {
    require('node:fs').writeFileSync('/src/_temp/edition-test-results/edition-journal-browser-' + mode + '.html', await page.content());
    console.error('Browser body:', (await page.locator('body').innerText()).slice(0, 7000), 'Errors:', errors);
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
