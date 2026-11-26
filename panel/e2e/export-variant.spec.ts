import { expect, test, type Page, type Request } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { demoFile, type Demo } from './global-setup';

/**
 * ADR-060: her liste ekranında "Excel'e aktar" (filtreye uyan BÜTÜN kayıtlar, rol yetkisiyle).
 * ADR-061: Stok Listesi → kanal sekmesinde aynı ürünün varyantlarına toplu fiyat (yetenek kuralları sunucuda; burada akış).
 * ADR-062: sol menü sayfa kaydırılınca yerinde kalır; dar ekranda üstte yatay ve sabitlenmez.
 * Kanala yazma yok: e2e ortamında kanallar mock, arka plan job'ları kapalı.
 */
const demo = (): Demo => JSON.parse(readFileSync(demoFile, 'utf8'));

async function login(page: Page, userName = 'e2e-yonetici') {
  await page.goto('/giris');
  await page.getByLabel('Kullanıcı adı').fill(userName);
  await page.getByLabel('Parola').fill(process.env.E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByTestId('current-user')).toBeVisible();
}

type ExportBody = { fileName: string; columns: { header: string; kind?: string }[]; rows: unknown[][] };

/** Kartın "Excel'e aktar" düğmesine basar; sunucuya giden satırları ve indirilen dosyayı döndürür. */
async function exportFrom(page: Page, scope: ReturnType<Page['locator']>) {
  const request = page.waitForRequest((r: Request) => r.url().endsWith('/api/export/xlsx') && r.method() === 'POST');
  const download = page.waitForEvent('download');
  await scope.getByTestId('export-excel').first().click();
  const body = (await request).postDataJSON() as ExportBody;
  const file = await download;
  return { body, fileName: file.suggestedFilename() };
}

test('Excel: Stok Hareketleri filtreye uyan bütün sayfaları aktarır, yalnızca görünen 50 satırı değil (ADR-060)', async ({ page }) => {
  await login(page);
  await page.getByRole('link', { name: 'Stok Hareketleri' }).click();
  await expect(page.getByTestId('movement-row').first()).toBeVisible();
  const total = Number((await page.locator('.pager span').first().innerText()).replace(/\D/g, ''));
  const card = page.locator('.card').filter({ has: page.getByTestId('movement-row') });

  const all = await exportFrom(page, card);
  expect(all.fileName).toMatch(/^stok-hareketleri-\d{8}-\d{4}\.xlsx$/);
  expect(all.body.columns.map(c => c.header)).toEqual(['Tarih', 'SKU', 'Depo', 'Tip', 'Adet', 'Kaynak tipi', 'Kaynak', 'Kullanıcı']);
  expect(all.body.rows.length).toBe(total);
  expect(await page.getByTestId('movement-row').count()).toBeLessThanOrEqual(50);

  // Filtre uygulanınca yalnızca filtreye uyanlar
  const d = demo();
  await page.getByLabel('SKU', { exact: true }).fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(page.getByTestId('movement-row').first()).toContainText(`DEMO-${d.tag}`);
  const filtered = await exportFrom(page, card);
  expect(filtered.body.rows.length).toBeGreaterThan(0);
  expect(filtered.body.rows.length).toBeLessThan(total);
  expect(filtered.body.rows.every(r => String(r[1]).startsWith(`DEMO-${d.tag}`))).toBe(true);
});

test('Excel: ekrandaki tablo (Genel Bakış) ve kanal sekmesi listesi aktarılır; Destek rolü de aktarabilir (ADR-060)', async ({ page }) => {
  const d = demo();
  await login(page, 'e2e-destek');
  // Sayfalanmamış tablo: kartın tablosu olduğu gibi (başlıklar + hücre metinleri)
  const critical = page.locator('.card').filter({ hasText: 'Kritik stok' });
  await expect(critical.locator('tbody tr').first()).toBeVisible();
  const overview = await exportFrom(page, critical);
  expect(overview.body.columns.map(c => c.header)).toEqual(['SKU', 'Ürün', 'Fiziksel', 'Satılabilir']);
  expect(overview.body.columns[2].kind).toBe('number');
  expect(overview.body.rows.length).toBe(await critical.locator('tbody tr').count());

  // Kanal sekmesi (çekilen stoklar): aramaya uyan bütün ilanlar
  await page.goto('/stok?kanal=TRENDYOL');
  await page.getByLabel('Kanal ilanlarında bul').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(page.getByTestId('channel-stock-row').first()).toBeVisible();
  const shown = await page.getByTestId('channel-stock-row').count();
  const tab = await exportFrom(page, page.locator('.card').filter({ has: page.getByTestId('channel-stock-table') }));
  expect(tab.fileName).toMatch(/^trendyol-kanal-stogu-/);
  expect(tab.body.rows.length).toBe(shown);
  const skuIndex = tab.body.columns.findIndex(c => c.header === 'SKU');
  expect(tab.body.rows.map(r => r[skuIndex])).toContain(d.skuCodes[0]);
  // Destek yazma göremez: toplu fiyat düğmesi yok
  await expect(page.getByTestId('variant-price-open')).toHaveCount(0);
});

test('Varyantlara toplu fiyat: aynı ürünün bütün bedenleri seçilir, tek fiyat yazılır, yetenek durumu söylenir (ADR-061)', async ({ page }) => {
  const d = demo();
  await login(page);
  const prices = await (await page.request.get('/api/prices/channels')).json() as { channelCode: string; capabilityState: string }[];
  const state = prices.find(p => p.channelCode === 'HEPSIBURADA')!.capabilityState;

  await page.goto('/stok?kanal=HEPSIBURADA');
  await page.getByLabel('Kanal ilanlarında bul').fill(d.skuCodes[0]);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(page.getByTestId('channel-stock-row')).toHaveCount(1);
  await page.getByTestId('channel-stock-row').first().getByTestId('variant-price-open').click();

  // Aramada tek satır görünse de ürünün 4 bedeni (S/M/L/XL) listelenir ve seçili gelir
  const panel = page.getByTestId('variant-price-panel');
  await expect(panel.getByTestId('variant-row')).toHaveCount(4);
  for (const code of d.skuCodes) await expect(panel).toContainText(code);
  await expect(panel.getByTestId('variant-capability')).toContainText(
    state === 'KAPALI' ? 'kanala hiçbir şey gönderilmez' : state === 'DENEME' ? 'Yönetici onayına düşer' : 'kanala gönderilir');

  // Bir bedeni çıkar, geçersiz fiyat engellenir, geçerli fiyat 3 varyanta yazılır
  await panel.getByLabel(`${d.skuCodes[3]} seç`).uncheck();
  await panel.getByTestId('variant-price-input').fill('12,345');
  await expect(panel.getByTestId('variant-price-save')).toBeDisabled();
  await panel.getByTestId('variant-price-input').fill('1.249,90');
  await expect(panel.getByTestId('variant-price-save')).toHaveText('Seçilen 3 varyanta uygula');
  let message = '';
  page.once('dialog', dialog => { message = dialog.message(); void dialog.accept(); });
  await panel.getByTestId('variant-price-save').click();
  await expect(page.getByRole('status')).toContainText('3 varyantın fiyatı kaydedildi');
  expect(message).toContain('3 varyanta');
  expect(message).toContain('1.249,90');

  // Sunucu: üç bedenin Hepsiburada ilan fiyatı 1249,90, dördüncü ve diğer kanal değişmedi
  const overview = await (await page.request.get(`/api/prices/skus?search=DEMO-${d.tag}&pageSize=50`)).json() as
    { items: { skuCode: string; channels: { channelCode: string; override: number | null }[] }[] };
  const hb = (code: string) => overview.items.find(i => i.skuCode === code)!.channels.find(c => c.channelCode === 'HEPSIBURADA')!.override;
  const ty = (code: string) => overview.items.find(i => i.skuCode === code)!.channels.find(c => c.channelCode === 'TRENDYOL')!.override;
  for (const code of d.skuCodes.slice(0, 3)) expect(hb(code)).toBe(1249.9);
  expect(hb(d.skuCodes[3])).toBeNull();
  expect(ty(d.skuCodes[0])).toBeNull();
  await expect(panel.getByTestId('variant-row').filter({ hasText: d.skuCodes[0] })).toContainText('elle');

  // Temizle: elle fiyatı kaldır (tüm bedenler)
  await panel.getByLabel('Tüm varyantları seç').check();
  page.once('dialog', dialog => void dialog.accept());
  await panel.getByRole('button', { name: 'Elle fiyatı kaldır' }).click();
  await expect(page.getByRole('status')).toContainText('fiyatı kaydedildi');
  await expect(panel.getByTestId('variant-row').filter({ hasText: 'elle' })).toHaveCount(0);
});

test('Sol menü sayfa kaydırılınca yerinde kalır; dar ekranda üstte ve sabitlenmez (ADR-062)', async ({ page }) => {
  await login(page);
  await page.getByRole('link', { name: 'Stok Hareketleri' }).click();
  await expect(page.getByTestId('movement-row').first()).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 600 });
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(100);
  const box = await page.locator('aside.sidebar').boundingBox();
  expect(box!.y).toBe(0);
  await expect(page.getByRole('navigation').getByRole('link', { name: 'Ayarlar' })).toBeInViewport();

  await page.setViewportSize({ width: 375, height: 700 });
  expect(await page.locator('aside.sidebar').evaluate(el => getComputedStyle(el).position)).toBe('static');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
});

test('Fiyat kutuları: tek tıklamayla art arda yazılan rakamların hepsi girilir, odak kaybolmaz', async ({ page }) => {
  const d = demo();
  await login(page);
  // Fiyat & Stok Düzenle: satır içi elle fiyat ve kanal stoğu kutuları (TanStack tablo hücresi)
  await page.goto(`/urun-duzenle?ara=${encodeURIComponent(d.priceSkuCode)}`);
  const row = page.getByTestId('sku-price-row').filter({ hasText: d.priceSkuCode });
  await expect(row).toHaveCount(1);
  for (const [label, text] of [[`Hepsiburada elle fiyat ${d.priceSkuCode}`, '1249,90'], [`Hepsiburada kanal stoğu ${d.priceSkuCode}`, '12']]) {
    const input = row.getByLabel(label);
    await input.click();
    await page.keyboard.type(text, { delay: 30 });
    await expect(input).toBeFocused();
    await expect(input).toHaveValue(text);
  }
  await page.getByRole('button', { name: 'Vazgeç' }).click();

  // Varyantlara toplu fiyat kutusu
  await page.goto('/stok?kanal=HEPSIBURADA');
  await page.getByLabel('Kanal ilanlarında bul').fill(d.skuCodes[0]);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await page.getByTestId('channel-stock-row').first().getByTestId('variant-price-open').click();
  const variant = page.getByTestId('variant-price-input');
  await variant.click();
  await page.keyboard.type('1.249,90', { delay: 30 });
  await expect(variant).toBeFocused();
  await expect(variant).toHaveValue('1.249,90');
});
