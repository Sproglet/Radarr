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
  try {
    await page.goto('http://127.0.0.1:17878/movie/-1000001');
    const files = page.locator('fieldset').filter({ has: page.locator('legend', { hasText: /^Files$/ }) });
    const primary = files.getByRole('row').filter({ hasText: 'Theatrical IMAX' }).filter({ hasText: '.mkv' });
    const secondary = files.getByRole('row').filter({ hasText: 'Directors Cut 3D' }).filter({ hasText: '.mkv' });
    await primary.getByText('+11', { exact: true }).waitFor();
    await secondary.getByText(invalid ? 'Unknown' : '+77', { exact: true }).waitFor();
    assert.equal(await secondary.getByText('+11', { exact: true }).count(), 0);
    if (invalid) {
      await secondary.getByText('Unknown', { exact: true }).hover();
      await page.getByText(/QualityProfile with ID 999999 was not found|QualityProfile.*999999|model.*999999/i).first().waitFor();
    }
    assert.deepEqual(errors, []);
    console.log('PASS: actual Files table shows primary +11 and secondary ' + (invalid ? 'Unknown with a context error' : '+77 using its independent profile'));
  } catch (error) {
    await page.screenshot({ path: '/src/_temp/edition-test-results/edition-file-profile-browser-failure.png', fullPage: true });
    console.error(errors, (await page.locator('body').innerText()).slice(0, 6000));
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
