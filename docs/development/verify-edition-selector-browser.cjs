// Invoked by the disposable synthetic-client harness's manual-select scenario.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const editionId = Number(process.argv[2]);
const folder = process.argv[3];
assert.ok([1, 2].includes(editionId) && folder.startsWith('/tmp/radarr-phase3-'));

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(15000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto('http://127.0.0.1:17878/');
    await page.getByRole('button', { name: 'Manual Import', exact: true }).click();
    await page.locator('input[name="folder"]').fill(folder);
    await page.locator('input[name="folder"]').blur();
    const previewResponse = page.waitForResponse(r => r.url().includes('/api/v3/manualimport?'), { timeout: 30000 });
    previewResponse.catch(() => {});
    await page.getByRole('button', { name: 'Interactive Import', exact: true }).click();
    const preview = await (await previewResponse).json();
    assert.equal(preview.length, 1);
    assert.equal(preview[0].editionAssignmentValid, false);
    assert.ok(!preview[0].movieEditionId && !preview[0].movieEditionName);
    assert.equal(preview[0].customFormatScore, 0);
    await page.waitForFunction(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Import')?.disabled === true);
    const choicesResponse = page.waitForResponse(r => r.url().includes('/api/v3/movieedition?'), { timeout: 30000 });
    choicesResponse.catch(() => {});
    await page.getByTitle('Click to change edition', { exact: true }).click();
    const choices = await (await choicesResponse).json();
    const choice = choices.find(e => e.id === editionId);
    assert.ok(choice && choice.state === 'present');
    assert.equal(await page.getByRole('button', { name: 'Synthetic Invalid Target', exact: true }).isDisabled(), true);
    const reprocessResponse = page.waitForResponse(r => r.url().endsWith('/api/v3/manualimport') && r.request().method() === 'POST');
    reprocessResponse.catch(() => {});
    await page.getByRole('button', { name: choice.name, exact: true }).click();
    const reprocess = await reprocessResponse;
    assert.equal(reprocess.request().postDataJSON()[0].movieEditionId, editionId);
    const selected = (await reprocess.json())[0];
    assert.equal(selected.editionAssignmentValid, true);
    assert.equal(selected.movieEditionName, choice.name);
    assert.equal(selected.customFormatScore, editionId === 1 ? 11 : 77);
    await page.locator('select[name="importMode"]').selectOption('copy');
    await page.waitForFunction(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Import')?.disabled === false);
    const commandResponse = page.waitForResponse(r => r.url().endsWith('/api/v3/command') && r.request().method() === 'POST' && r.request().postDataJSON().name === 'ManualImport');
    commandResponse.catch(() => {});
    await page.getByRole('button', { name: 'Import', exact: true }).click();
    const command = await commandResponse;
    const submitted = command.request().postDataJSON().files;
    assert.equal(submitted.length, 1);
    assert.equal(submitted[0].movieEditionId, editionId);
    assert.equal(submitted[0].downloadClientId, undefined);
    const body = await command.json();
    for (let count = 0; count < 90; count++) {
      const response = await page.request.get('http://127.0.0.1:17878/api/v3/command/' + body.id, { headers: { 'X-Api-Key': '11112222333344445555666677778888' } });
      const status = await response.json();
      if (status.status === 'completed') {
        assert.deepEqual(errors, []);
        console.log('PASS: browser requires an explicit valid edition, displays its name/profile and submits only selected edition ' + editionId);
        return;
      }
      assert.ok(!['failed', 'aborted'].includes(status.status), JSON.stringify(status));
      await page.waitForTimeout(500);
    }
    throw new Error('Selected-edition import timed out');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-selector-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
