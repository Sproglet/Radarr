// Run only against the disposable application and synthetic library in the harness.
const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const root = 'http://127.0.0.1:17878';
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  try {
    await page.goto(root + '/settings/customformats');
    await page.getByText('Test Parsing', { exact: true }).click();
    const modal = page.locator('#portal-root');
    const details = modal.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Details$/ }) });
    const row = label => details.locator('[class*="ParseResultItem-item"]').filter({ has: page.getByText(label, { exact: true }) });
    const parse = async (title, score, name) => {
      const pending = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/api/v3/parse') && new URL(r.url()).searchParams.get('title') === title);
      await modal.locator('input[name="title"]').fill(title);
      const response = await pending;
      assert.equal(response.status(), 200);
      const result = await response.json();
      assert.equal(result.customFormatScore ?? null, score);
      await row('Custom Format Score').locator(':scope > div').nth(1).getByText(score === null ? 'Unknown' : String(score), { exact: true }).waitFor();
      await row('Edition').locator(':scope > div').nth(1).getByText(name ?? 'Unknown', { exact: true }).waitFor();
      return result;
    };
    const directors = 'Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup';
    const imax = 'Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup';
    const choice = modal.locator('select[name="movieEditionId"]');
    const choose = async (id, score, name) => {
      const pending = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/api/v3/parse') &&
        (new URL(r.url()).searchParams.get('movieEditionId') ?? '0') === String(id));
      await choice.selectOption(String(id));
      const response = await pending;
      assert.equal(response.status(), 200);
      const result = await response.json();
      assert.equal(result.customFormatScore, score);
      assert.equal(result.editionSelectionExplicit, id !== 0);
      await row('Custom Format Score').locator(':scope > div').nth(1).getByText(String(score), { exact: true }).waitFor();
      await row('Edition').locator(':scope > div').nth(1).getByText(name, { exact: true }).waitFor();
      return result;
    };
    await parse(directors, 77, 'Directors Cut 3D');
    assert.equal((await choose(1, 11, 'Theatrical IMAX')).movie.qualityProfileId, 1);
    await choose(2, 77, 'Directors Cut 3D');
    await choose(0, 77, 'Directors Cut 3D');
    await parse('Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup', 11, 'Theatrical IMAX');
    await parse('Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup', null);
    assert.equal(await row('Quality Profile').count(), 0);
    await choose(2, 77, 'Directors Cut 3D');
    await parse(imax, 11, 'Theatrical IMAX');
    assert.equal(await choice.inputValue(), '0');
    await parse('Synthetic.Unmapped.Comet.2044.1080p.BluRay-SyntheticGroup', null);
    assert.equal(await row('Quality Profile').count(), 0);
    assert.equal(await choice.count(), 0);

    // Hold a real disposable API response after the server has answered. A newer
    // input, clear or unmount must cancel it and prevent stale UI restoration.
    let release;
    let captured;
    const delayed = 'Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticDelayedGroup';
    const delayedPattern = '**/api/v3/parse?*SyntheticDelayedGroup*';
    const hold = () => {
      const seen = new Promise(resolve => { captured = resolve; });
      const waiting = new Promise(resolve => { release = resolve; });
      page.route(delayedPattern, async route => {
        const response = await route.fetch();
        captured();
        await waiting;
        await route.fulfill({ response }).catch(() => {});
      });
      return seen;
    };
    let held = hold();
    await modal.locator('input[name="title"]').fill(delayed);
    await held;
    await parse(imax, 11, 'Theatrical IMAX');
    release();
    await page.waitForTimeout(300);
    assert.equal(await choice.inputValue(), '0');
    assert.equal(await row('Custom Format Score').locator(':scope > div').nth(1).innerText(), '11');
    await page.unroute(delayedPattern);

    held = hold();
    await modal.locator('input[name="title"]').fill(delayed);
    await held;
    await modal.locator('button[class*="clearButton"]').click();
    release();
    await page.waitForTimeout(400);
    assert.equal(await modal.locator('input[name="title"]').inputValue(), '');
    assert.equal(await details.count(), 0);
    assert.equal(await choice.count(), 0);
    await page.unroute(delayedPattern);

    let parseRequests = 0;
    const countParse = request => { if (new URL(request.url()).pathname.endsWith('/api/v3/parse')) parseRequests++; };
    page.on('request', countParse);
    await modal.locator('input[name="title"]').fill(directors);
    await modal.locator('button[class*="clearButton"]').click();
    await page.waitForTimeout(600);
    assert.equal(parseRequests, 0, 'clear cancels the debounce timer');
    page.off('request', countParse);

    held = hold();
    await modal.locator('input[name="title"]').fill(delayed);
    await held;
    await modal.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByText('Test Parsing', { exact: true }).click();
    release();
    await page.waitForTimeout(400);
    assert.equal(await modal.locator('input[name="title"]').inputValue(), '');
    assert.equal(await details.count(), 0);
    await page.unroute(delayedPattern);

    const errorTitle = 'Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticErrorGroup';
    await page.route('**/api/v3/parse?*SyntheticErrorGroup*', route => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic parser failure' }) }));
    await modal.locator('input[name="title"]').fill(errorTitle);
    await modal.getByText('Synthetic parser failure', { exact: true }).waitFor();
    assert.equal(await details.count(), 0);
    await parse(imax, 11, 'Theatrical IMAX');
    assert.equal(await modal.getByText('Synthetic parser failure', { exact: true }).count(), 0);
    assert.deepEqual(errors, []);
    console.log('PASS: real parser modal supports auto/explicit edition/profile comparison, resets selection on new titles, shows Unknown for unresolved input and cancels stale, cleared or unmounted requests with recovery from API errors');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
