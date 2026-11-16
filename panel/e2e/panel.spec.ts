import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { demoFile, type Demo } from './global-setup';

/**
 * Aşama 5 kabul kriteri: Playwright ile 3 temel senaryo
 *   1) sipariş listesi filtre  2) stok sapması görme  3) hatalı senkron yeniden deneme
 * + tüm ekranların demo verilerle açılması ve Destek rolünde müşteri maskelemesi.
 */
const demo = (): Demo => JSON.parse(readFileSync(demoFile, 'utf8'));

async function login(page: Page, userName = 'e2e-yonetici') {
  await page.goto('/giris');
  await page.getByLabel('Kullanıcı adı').fill(userName);
  await page.getByLabel('Parola').fill(process.env.E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByTestId('current-user')).toBeVisible();
}

test('1) Sipariş listesi kanal, durum ve sipariş no ile filtrelenir; detayda zaman çizelgesi görünür', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('link', { name: 'Siparişler' }).click();
  await expect(page.getByRole('heading', { name: 'Siparişler' })).toBeVisible();

  const rows = page.getByTestId('order-row');
  await page.getByLabel('Sipariş no').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(6);

  await page.getByLabel('Kanal').selectOption({ label: 'Hepsiburada' });
  await page.getByLabel('Durum').selectOption({ label: 'Yeni' });
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(`DEMO-${d.tag}-S6`);
  await expect(rows.first()).toContainText('Stok yetersiz');

  await page.getByLabel('Kanal').selectOption({ label: 'Trendyol' });
  await page.getByLabel('Durum').selectOption({ label: 'Kargoda' });
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(1);
  await rows.first().click();

  const detail = page.getByTestId('order-detail');
  await expect(detail).toContainText(`DEMO-${d.tag}-S2`);
  await expect(page.getByTestId('order-timeline').locator('li')).toHaveText([/Yeni/, /Onaylandı/, /Hazırlanıyor/, /Kargoda/]);
});

test('2) Stok listesinde kanal sapması kırmızı satırla görünür', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('link', { name: 'Stok Listesi' }).click();
  await page.getByLabel('Ara').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Ara' }).click();

  const rows = page.getByTestId('stock-row');
  await expect(rows).toHaveCount(4);
  await expect(page.locator('tr.row-deviation')).toHaveCount(1);

  const deviating = rows.filter({ hasText: d.deviationSkuCode });
  await expect(deviating).toHaveClass(/row-deviation/);
  await expect(deviating.getByTestId('cell-HEPSIBURADA')).toContainText(String(d.deviationChannelStock));
  await expect(deviating.getByTestId('cell-HEPSIBURADA')).toContainText(`beklenen ${d.deviationExpected}`);
  await expect(deviating.getByTestId('cell-TRENDYOL')).not.toHaveClass(/cell-deviation/);

  await page.getByLabel('Yalnızca sapmalar').check();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(d.deviationSkuCode);
});

test('3) Hatalı senkron sadeleştirilmiş hatayla görünür ve "Yeniden dene" ile kuyruğa alınır', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('link', { name: 'Senkron Merkezi' }).click();
  await page.getByLabel('Durum').selectOption({ label: 'Hata' });

  const row = page.getByTestId('sync-row').filter({ hasText: d.skuCodes[0] });
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('sync-error')).toHaveText('Kanal hata döndü (HTTP 503): Servis geçici olarak kullanılamıyor');
  await expect(row).toHaveClass(/row-danger/);

  await row.getByRole('button', { name: 'Yeniden dene' }).click();
  await expect(page.getByRole('status')).toContainText('yeniden kuyruğa alındı');
  await expect(row).toHaveCount(0); // artık HATA listesinde değil

  await page.getByLabel('Durum').selectOption({ label: 'Bekliyor' });
  const pending = page.getByTestId('sync-row').filter({ hasText: d.skuCodes[0] });
  await expect(pending).toHaveCount(1);
  await expect(pending).toContainText('Bekliyor');
  await expect(pending.getByRole('button', { name: 'Yeniden dene' })).toHaveCount(0);
});

test('Tüm ekranlar demo verilerle açılır (hata yok)', async ({ page }) => {
  await login(page);
  const screens: [string, string][] = [
    ['Genel Bakış', 'Genel Bakış'], ['Stok Listesi', 'Stok Listesi'], ['Stok Hareketleri', 'Stok Hareketleri'],
    ['Siparişler', 'Siparişler'], ['İade / İptal', 'İade / İptal'], ['Eşleştirme', 'Eşleştirme'],
    ['Senkron Merkezi', 'Senkron Merkezi'], ['Ayarlar', 'Ayarlar']
  ];
  for (const [link, heading] of screens) {
    await page.getByRole('navigation').getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
    await expect(page.locator('.loading')).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
  }
  await page.getByRole('navigation').getByRole('link', { name: 'Genel Bakış' }).click();
  await expect(page.getByTestId('stat-today-orders')).not.toContainText(/^0/);
});

test('Destek rolü müşteri bilgisini maskeli görür ve yazma işlemlerini göremez', async ({ page }) => {
  const d = demo();
  await login(page, 'e2e-destek');
  await page.getByRole('link', { name: 'Siparişler' }).click();
  await page.getByLabel('Sipariş no').fill(d.orderNos[0]);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await page.getByTestId('order-row').first().click();

  await expect(page.getByTestId('customer-name')).toHaveText('A*** Y*****');
  await expect(page.getByTestId('order-detail')).not.toContainText('Yılmaz');
  await expect(page.getByTestId('order-detail').getByRole('button', { name: 'Onaylandı' })).toHaveCount(0);

  await page.getByRole('link', { name: 'Senkron Merkezi' }).click();
  await expect(page.getByRole('button', { name: 'Yeniden dene' })).toHaveCount(0);
});
