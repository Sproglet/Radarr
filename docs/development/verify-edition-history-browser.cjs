const assert = require('node:assert/strict');
const { chromium } = require('../../_temp/edition-browser-tools/node_modules/playwright-core');
const invalid = process.argv[2] === 'invalid';
assert.ok(['valid', 'invalid'].includes(process.argv[2]));
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const checkRows = async table => {
    const first = table.getByRole('row').filter({ hasText: 'Synthetic.Lunar.Voyage.2026.Theatrical.IMAX.1080p.BluRay-SyntheticGroup' });
    const second = table.getByRole('row').filter({ hasText: 'Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup' });
    await first.getByText('+11', { exact: true }).waitFor();
    await second.getByText(invalid ? 'Unknown' : '+77', { exact: true }).waitFor();
    const unknown = table.getByRole('row').filter({ hasText: 'SyntheticHistoryUnknownUnassigned' });
    await unknown.getByText('Unknown', { exact: true }).hover();
    await page.getByText(/no valid recorded edition assignment/i).first().waitFor();
    if (invalid) {
      await second.getByText('Unknown', { exact: true }).hover();
      await page.getByText(/QualityProfile.*999999/i).first().waitFor();
    }
  };
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    await page.getByRole('button', { name: 'History', exact: true }).click();
    await checkRows(page.locator('#portal-root'));
    await page.goto('http://127.0.0.1:17878/activity/history');
    await page.getByRole('button', { name: 'Options', exact: true }).click();
    const options = page.locator('#portal-root');
    await options.locator('input[name="sourceTitle"]').check({ force: true });
    await options.locator('input[name="customFormatScore"]').check({ force: true });
    await options.getByRole('button', { name: 'Close', exact: true }).click();
    await checkRows(page);
    assert.deepEqual(errors, []);
    console.log('PASS: actual movie and activity history tables show independent scores and Unknown with assignment/profile reasons (' + (invalid ? 'invalid' : 'valid') + ')');
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-history-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
