// Invoked by verify-edition-client-recovery.py manual-ui inside its isolated test container.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const clientId = Number(process.argv[2]);
const editionId = clientId - 40;
assert.ok(clientId === 41 || clientId === 42);

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  console.log("Browser version: " + browser.version());
  const page = await browser.newPage();
  page.setDefaultTimeout(15000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    const queueResponse = page.waitForResponse(r => r.url().includes('/api/v3/queue?') && r.request().method() === 'GET');
    await page.goto('http://127.0.0.1:17878/activity/queue');
    const records = (await (await queueResponse).json()).records;
    const index = records.findIndex(r => r.downloadClientId === clientId);
    assert.ok(index >= 0, JSON.stringify(records));
    const previewResponse = page.waitForResponse(r => r.url().includes('/api/v3/manualimport?') && r.request().method() === 'GET');
    previewResponse.catch(() => {});
    await page.locator('tbody tr').nth(index).locator('button:has(svg[data-icon="user"])').click();
    const preview = await previewResponse;
    assert.equal(new URL(preview.url()).searchParams.get('downloadClientId'), String(clientId));
    const files = await preview.json();
    assert.equal(files.length, 1);
    assert.equal(files[0].movieEditionId, editionId);
    assert.equal(files[0].downloadClientId, clientId);
    assert.equal(files[0].customFormatScore, clientId === 41 ? 11 : 77);
    await page.getByTitle('Click to change release group', { exact: true }).click();
    await page.locator('input[name="releaseGroup"]').fill('SyntheticBrowserGroup');
    const reprocessResponse = page.waitForResponse(r => r.url().endsWith('/api/v3/manualimport') && r.request().method() === 'POST');
    reprocessResponse.catch(() => {});
    await page.getByRole('button', { name: 'Set Release Group', exact: true }).click();
    const reprocess = await reprocessResponse;
    const requestFiles = reprocess.request().postDataJSON();
    assert.equal(requestFiles[0].downloadClientId, clientId);
    assert.equal(requestFiles[0].movieEditionId, editionId);
    assert.equal((await reprocess.json())[0].movieEditionId, editionId);
    const commandResponse = page.waitForResponse(r => r.url().endsWith('/api/v3/command') && r.request().method() === 'POST' && r.request().postDataJSON().name === 'ManualImport');
    commandResponse.catch(() => {});
    await page.getByRole('button', { name: 'Import', exact: true }).click();
    const command = await commandResponse;
    const submitted = command.request().postDataJSON().files;
    assert.equal(submitted.length, 1);
    assert.equal(submitted[0].downloadClientId, clientId);
    assert.equal(submitted[0].movieEditionId, editionId);
    assert.equal(submitted[0].downloadId, 'synthetic-sab-shared-id');
    const body = await command.json();
    for (let count = 0; count < 90; count++) {
      const response = await page.request.get('http://127.0.0.1:17878/api/v3/command/' + body.id, { headers: { 'X-Api-Key': '11112222333344445555666677778888' } });
      const status = await response.json();
      if (status.status === 'completed') {
        assert.deepEqual(errors, []);
        console.log('PASS: browser client ' + clientId + ' retains edition ' + editionId + ' through queue preview, release-group reprocessing and real import command');
        return;
      }
      assert.ok(!['failed', 'aborted'].includes(status.status), JSON.stringify(status));
      await page.waitForTimeout(500);
    }
    throw new Error('Browser import command timed out');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-browser-failure.png', fullPage: true });
    console.error('Page errors:', errors);
    console.error((await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
