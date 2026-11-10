const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  const writes = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.url().includes('/api/v3/') && !['GET', 'HEAD'].includes(request.method())) writes.push(request.method() + ' ' + request.url());
  });
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  let fail = true;
  await page.route('**/api/v3/wanted/editionmissing?**', route => {
    if (!fail) return route.continue();
    fail = false;
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'Synthetic wanted read failure' }) });
  });
  const rows = () => page.locator('tbody').getByRole('row');
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    await page.getByRole('link', { name: 'Wanted', exact: true }).click();
    await page.getByRole('link', { name: 'Editions', exact: true }).click();
    await page.getByText('There was an error loading this page', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await page.getByText('No results found', { exact: true }).waitFor();
    const monitoring = page.locator('select[name="editionWantedMonitoring"]');
    const status = page.locator('select[name="editionWantedStatus"]');
    const sort = page.locator('select[name="editionWantedSort"]');
    await monitoring.selectOption('false');
    await page.getByText('Editions: 11. Page 1 of 2', { exact: true }).waitFor();
    assert.equal(await rows().count(), 10);
    await rows().filter({ hasText: 'Synthetic Missing Collector 3D IMAX' }).getByText('cut: theatrical', { exact: true }).waitFor();
    await rows().filter({ hasText: 'Synthetic Missing Collector 3D IMAX' }).getByText('framing: imax', { exact: true }).waitFor();
    await rows().filter({ hasText: 'Synthetic Missing Directors 3D' }).getByText('cut: directors-cut', { exact: true }).waitFor();
    await rows().filter({ hasText: 'Synthetic Wanted Extra 01' }).getByText("The edition's quality profile is unavailable.", { exact: true }).waitFor();
    await rows().filter({ hasText: 'Synthetic Wanted Extra 02' }).getByText("The edition's current file has invalid ownership or is unavailable.", { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Next Page', exact: true }).click();
    await page.getByText('Editions: 11. Page 2 of 2', { exact: true }).waitFor();
    assert.equal(await rows().count(), 1);
    await rows().filter({ hasText: 'Synthetic Wanted Extra 09' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Next Page', exact: true }).isDisabled(), true);
    await sort.selectOption('lastSearchTime');
    await page.getByText('Editions: 11. Page 1 of 2', { exact: true }).waitFor();
    await rows().first().getByText('Synthetic Missing Collector 3D IMAX', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Next Page', exact: true }).click();
    await page.getByText('Editions: 11. Page 2 of 2', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await page.getByText('Editions: 11. Page 1 of 2', { exact: true }).waitFor();
    await status.selectOption('cutoff');
    await page.getByText('Editions: 1. Page 1 of 1', { exact: true }).waitFor();
    assert.equal(await rows().count(), 1);
    await rows().getByText('Directors Cut 3D', { exact: true }).waitFor();
    await rows().getByText('Synthetic Secondary Queue Profile', { exact: true }).waitFor();
    await page.getByText('Cutoff results compare quality only; custom-format cutoff is not included.', { exact: true }).waitFor();
    await monitoring.selectOption('true');
    await page.getByText('No results found', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: /^Search (All|Selected)/ }).count(), 0);
    assert.deepEqual(errors, []);
    assert.deepEqual(writes, []);
    console.log('PASS: actual Wanted Editions sidebar/page recovers from read failure, pages/sorts combined targets, displays diagnostics and filters effective monitoring/quality cutoff without writes');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-wanted-browser-failure.png', fullPage: true });
    console.error(errors, writes, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
