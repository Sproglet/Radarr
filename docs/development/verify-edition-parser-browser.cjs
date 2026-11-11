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
    await parse('Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup', 77, 'Directors Cut 3D');
    await parse('Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup', 11, 'Theatrical IMAX');
    await parse('Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup', null);
    assert.equal(await row('Quality Profile').count(), 0);
    await parse('Synthetic.Unmapped.Comet.2044.1080p.BluRay-SyntheticGroup', null);
    assert.equal(await row('Quality Profile').count(), 0);
    assert.deepEqual(errors, []);
    console.log('PASS: real parser modal displays independent edition/profile scores and replaces prior scores/targets with Unknown for unresolved or unmapped titles');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
