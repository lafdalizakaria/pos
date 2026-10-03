import { chromium } from 'playwright';

const base = process.env.BACKOFFICE_URL ?? 'http://127.0.0.1:5090';
const shots = process.argv[2] ?? '.';
const results = [];
const browser = await chromium.launch();

async function login(userName, roles) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 950 }, locale: 'fr-FR' });
  const page = await context.newPage();
  page.on('pageerror', e => results.push(`PAGEERROR ${e.message}`));
  await page.goto(`${base}/`);
  await page.waitForURL(/dev-login/);
  await page.fill('input[name=userName]', userName);
  for (const box of await page.$$('input[name=roles]')) {
    const value = await box.getAttribute('value');
    if (roles.includes(value) !== await box.isChecked()) await box.click();
  }
  await page.click('button[type=submit]');
  await page.waitForSelector('h1');
  return page;
}

async function visit(page, path, expectText, shot) {
  await page.goto(`${base}/${path}`);
  await page.waitForSelector('h1');
  await page.waitForTimeout(1200);
  const body = await page.textContent('main');
  const errorUi = await page.isVisible('#blazor-error-ui');
  const notice = await page.$('.notice.error');
  const ok = body.includes(expectText) && !errorUi && !notice;
  results.push(`${ok ? 'OK  ' : 'FAIL'} ${path} (expects "${expectText}")${notice ? ' notice=' + await notice.textContent() : ''}${errorUi ? ' blazor-error' : ''}`);
  if (shot) await page.screenshot({ path: `${shots}/${shot}.png`, fullPage: true });
  return page;
}

const admin = await login('admin@newrest.ma', ['Pos.Admin']);
await visit(admin, '', 'Administrateur global', 'accueil');
await visit(admin, 'organisation', 'NFMS', 'organisation');
await visit(admin, 'points-de-vente', 'CAS-SELF', null);
await visit(admin, 'operateurs', 'CAIS01', null);
await visit(admin, 'articles', 'CSC-VND', 'articles');
await visit(admin, 'categories', 'Plats chauds', null);
await visit(admin, 'tarifs', 'TNG-SNACK', null);
await visit(admin, 'menus', 'Midi', 'menus');
await visit(admin, 'clients', 'Atlas Automotive', null);
await visit(admin, 'convives', 'Benali', null);
await visit(admin, 'comptes', 'Benali', null);
await visit(admin, 'audit', 'Journal', null);
await visit(admin, 'tickets', 'Tickets', null);
await visit(admin, 'clotures', 'intégrité', null);
await visit(admin, 'droits', 'Attribuer', null);

async function step(name, fn) { try { await fn(); } catch (e) { results.push(`FAIL ${name}: ${e.message.split('\n')[0]}`); await admin.screenshot({ path: `${shots}/fail-${name}.png`, fullPage: true }).catch(() => {}); } }
// Issue a device key: shown once.
await step('device-key', async () => {
await admin.goto(`${base}/points-de-vente`); await admin.waitForTimeout(1000);
await admin.click('button:has-text("Caisses") >> nth=1'); await admin.waitForTimeout(800);
await admin.click('text=Émettre une clé >> nth=0'); await admin.waitForTimeout(1000);
const key = await admin.textContent('.notice.key code').catch(() => null);
await admin.screenshot({ path: `${shots}/points-de-vente.png`, fullPage: true });
results.push(`${key?.startsWith('nrpos_') ? 'OK  ' : 'FAIL'} device key shown once (${key?.slice(0, 10)}…)`);
});
await step('topup', async () => {

// Top-up an account then check balance and audit.
await admin.goto(`${base}/comptes`); await admin.waitForTimeout(1200);
await admin.click('tr:has-text("Chraibi") >> text=Ouvrir'); await admin.waitForTimeout(1000);
await admin.fill('section.card:nth-of-type(2) input[type=number] >> nth=1', '50');
await admin.click('button:has-text("Valider")'); await admin.waitForTimeout(1500);
const success = await admin.textContent('.notice.success').catch(() => '');
results.push(`${success.includes('70,00') ? 'OK  ' : 'FAIL'} top-up 50 MAD -> "${success.trim()}"`);
await admin.screenshot({ path: `${shots}/comptes.png`, fullPage: true });
});
await step('menu', async () => {

// Create a menu for tomorrow's cell and add an article.
await admin.goto(`${base}/menus`); await admin.waitForTimeout(1200);
const addButtons = await admin.$$('.day button.link');
await addButtons[addButtons.length - 1].click(); await admin.waitForTimeout(1200);
await admin.click('button:has-text("Ajouter")'); await admin.waitForTimeout(1200);
const menuText = await admin.textContent('main');
results.push(`${menuText.includes('Publier vers les caisses') ? 'OK  ' : 'FAIL'} menu created and item added`);
await admin.screenshot({ path: `${shots}/menu-edition.png`, fullPage: true });
});
await step('badge', async () => {

// Lost badge flow.
await admin.goto(`${base}/convives`); await admin.waitForTimeout(1200);
await admin.fill('input[placeholder*="badge"]', 'BDG-ATL0002'); await admin.waitForTimeout(1200);
await admin.click('tr:has-text("El Amrani") >> text=Ouvrir'); await admin.waitForTimeout(800);
await admin.click('text=Déclarer perdu'); await admin.waitForTimeout(500);
await admin.fill('.panel input', 'BDG-ATL0002-B');
await admin.click('text=Bloquer et remplacer'); await admin.waitForTimeout(1200);
const badges = await admin.textContent('main');
results.push(`${badges.includes('BDG-ATL0002-B') && badges.includes('Perdu') ? 'OK  ' : 'FAIL'} lost badge replaced`);
await admin.screenshot({ path: `${shots}/convives.png`, fullPage: true });
});

await visit(admin, 'audit', 'AccountTopUp', 'audit');

// Grant a site scope then log in as that manager.
await admin.goto(`${base}/droits`); await admin.waitForTimeout(1000);
await admin.fill('input[placeholder*="prenom"]', 'manager.casa@newrest.ma');
await admin.selectOption('label:has-text("Site") select', { label: 'Casablanca Sidi Maârouf' });
await admin.click('button:has-text("Attribuer")'); await admin.waitForTimeout(1000);

const manager = await login('manager.casa@newrest.ma', ['Pos.Manager']);
await visit(manager, 'organisation', 'Casablanca', null);
const sites = await manager.textContent('main');
results.push(`${!sites.includes('Tanger Free Zone') ? 'OK  ' : 'FAIL'} manager does not see Tanger`);
await visit(manager, 'clients', 'Atlas', null);
results.push(`${!(await manager.textContent('main')).includes('Sahara') ? 'OK  ' : 'FAIL'} manager does not see NMS client`);
await manager.goto(`${base}/audit`); await manager.waitForTimeout(1000);
results.push(`${(await manager.textContent('main')).includes('Accès refusé') ? 'OK  ' : 'FAIL'} manager denied on audit`);

const nobody = await login('nobody@newrest.ma', ['Pos.Viewer']);
await visit(nobody, '', 'Aucun périmètre', null);

await browser.close();
console.log(results.join('\n'));
process.exitCode = results.some(r => r.startsWith('FAIL')) ? 1 : 0;
