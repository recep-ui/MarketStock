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
  await page.getByLabel('Sipariş no', { exact: true }).fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(6);

  await page.getByLabel('Kanal', { exact: true }).selectOption({ label: 'Hepsiburada' });
  await page.getByLabel('Durum', { exact: true }).selectOption({ label: 'Yeni' });
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(`DEMO-${d.tag}-S6`);
  await expect(rows.first()).toContainText('Stok yetersiz');

  await page.getByLabel('Kanal', { exact: true }).selectOption({ label: 'Trendyol' });
  await page.getByLabel('Durum', { exact: true }).selectOption({ label: 'Kargoda' });
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

  // Çoklu ilan (ADR-023): aynı SKU'nun Trendyol'daki iki ilanı tek hücrede, ikisi de aynı (ortak havuz) değerle
  const multi = rows.filter({ hasText: d.multiListingSkuCode });
  await expect(multi.getByTestId('cell-TRENDYOL')).toContainText('(2 ilan)');
  await expect(multi.getByTestId('cell-TRENDYOL')).not.toHaveClass(/cell-deviation/);

  await page.getByLabel('Yalnızca sapmalar').check();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(d.deviationSkuCode);
});

test('3) Hatalı senkron sadeleştirilmiş hatayla görünür ve "Yeniden dene" ile kuyruğa alınır', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('link', { name: 'Senkron Merkezi' }).click();
  await page.getByLabel('Durum', { exact: true }).selectOption({ label: 'Hata' });

  const row = page.getByTestId('sync-row').filter({ hasText: d.skuCodes[0] });
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('sync-error')).toHaveText('Kanal hata döndü (HTTP 503): Servis geçici olarak kullanılamıyor');
  await expect(row).toHaveClass(/row-danger/);

  await row.getByRole('button', { name: 'Yeniden dene' }).click();
  await expect(page.getByRole('status')).toContainText('yeniden kuyruğa alındı');
  await expect(row).toHaveCount(0); // artık HATA listesinde değil

  await page.getByLabel('Durum', { exact: true }).selectOption({ label: 'Bekliyor' });
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
    ['Senkron Merkezi', 'Senkron Merkezi'], ['Kanal Stoğu → Defter', 'Kanal Stoğu → Defter'], ['Ayarlar', 'Ayarlar']
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
  await page.getByLabel('Sipariş no', { exact: true }).fill(d.orderNos[0]);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await page.getByTestId('order-row').first().click();

  await expect(page.getByTestId('customer-name')).toHaveText('A*** Y*****');
  await expect(page.getByTestId('order-detail')).not.toContainText('Yılmaz');
  await expect(page.getByTestId('order-detail').getByRole('button', { name: 'Onaylandı' })).toHaveCount(0);

  await page.getByRole('link', { name: 'Senkron Merkezi' }).click();
  await expect(page.getByRole('button', { name: 'Yeniden dene' })).toHaveCount(0);
});

test('Sapma satırı "Kanala gönder" ile defter değeriyle yeniden gönderilir (gönderilemiyorsa gerekçe görünür)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('link', { name: 'Senkron Merkezi' }).click();
  await page.getByLabel('Sapma ara').fill(d.deviationSkuCode);
  const row = page.getByTestId('deviation-row').filter({ hasText: d.deviationSkuCode });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Kanala gönder' }).click();
  // Demo ortamında kanal pasif olabilir: ilan ya kuyruğa alınır ya da gerekçesiyle atlanır; ikisi de görünür olmalı
  await expect(page.getByText(/ilan defterdeki değerle kuyruğa alındı/)).toBeVisible();
});

test('Kanal stoğu deftere alınır: önizle, hedefi düzenle, onayla; hareket KANAL_ALIM kaynağıyla görünür', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Kanal Stoğu → Defter' }).click();
  const source = page.getByLabel('Kaynak kanal');
  await source.selectOption((await source.locator('option', { hasText: 'Hepsiburada' }).getAttribute('value'))!);
  await page.getByRole('button', { name: 'Önizle' }).click();

  const row = page.getByTestId('intake-row').filter({ hasText: d.deviationSkuCode });
  await expect(row).toHaveCount(1);
  // Kanal beklenenden 1 fazla gösteriyor → önerilen hedef kanal değeri
  await expect(row.getByLabel(`Hedef ${d.deviationSkuCode}`)).toHaveValue(String(d.deviationChannelStock));
  const checkbox = row.getByLabel(`Seç ${d.deviationSkuCode}`);
  if (!(await checkbox.isChecked())) await checkbox.check();
  // Yalnızca bu satır uygulansın
  for (const other of await page.getByTestId('intake-row').filter({ hasNotText: d.deviationSkuCode }).all()) {
    const cb = other.getByRole('checkbox');
    if (await cb.isChecked()) await cb.uncheck();
  }

  page.once('dialog', dialog => dialog.accept());
  await page.getByTestId('intake-apply').click();
  await expect(page.getByTestId('intake-result')).toContainText('Uygulandı');

  await page.getByRole('navigation').getByRole('link', { name: 'Stok Hareketleri' }).click();
  await page.getByLabel('SKU', { exact: true }).fill(d.deviationSkuCode);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(page.locator('table').getByText(/KANAL_ALIM/).first()).toBeVisible();
});

test('Sipariş durumu kanala bildirilir: deneme yeteneğinde Yönetici onayıyla tek kayıt kuyruğa girer (ADR-026)', async ({ page }) => {
  const d = demo();
  const orderNo = d.orderNos[2]; // Hepsiburada, Onaylandı
  await login(page);
  const channels: { id: number; code: string }[] = await (await page.request.get('/api/channels')).json();
  const hb = channels.find(c => c.code === 'HEPSIBURADA')!;
  try {
    // Ayarlar: sipariş durumu yeteneği → Deneme
    await page.getByRole('navigation').getByRole('link', { name: 'Ayarlar' }).click();
    await page.getByLabel('Yetenek kanalı').selectOption(String(hb.id));
    page.once('dialog', dialog => dialog.accept());
    await page.getByLabel('Yetenek SIPARIS_DURUM').selectOption('DENEME');
    await expect(page.getByLabel('Yetenek SIPARIS_DURUM')).toHaveValue('DENEME');

    // Siparişler: panelde Hazırlanıyor yap
    await page.getByRole('navigation').getByRole('link', { name: 'Siparişler' }).click();
    await page.getByLabel('Sipariş no', { exact: true }).fill(orderNo);
    await page.getByRole('button', { name: 'Filtrele' }).click();
    await page.getByTestId('order-row').filter({ hasText: orderNo }).click();
    await page.getByTestId('order-detail').getByRole('button', { name: 'Hazırlanıyor' }).click();
    await expect(page.getByTestId('order-detail').getByText('Hazırlanıyor').first()).toBeVisible();

    // Kanala bildir (bildirilmeyenler listesinde)
    await page.getByLabel('Bildirim sipariş no').fill(orderNo);
    const row = page.getByTestId('notification-row').filter({ hasText: orderNo });
    await expect(row).toHaveCount(1);
    await row.getByLabel(`Bildir ${orderNo}`).check();
    await page.getByTestId('notify-form').getByText('kanala bildirilecek').click();
    await page.getByTestId('notify-submit').click();
    await expect(page.getByTestId('notify-result')).toContainText('Yönetici onayı bekliyor');

    // Senkron Merkezi: onay bekliyor → onayla → kuyrukta
    await page.getByRole('navigation').getByRole('link', { name: 'Senkron Merkezi' }).click();
    await page.getByLabel('İşlem durumu').selectOption('ONAY_BEKLIYOR');
    const cmd = page.getByTestId('command-row').filter({ hasText: 'ORDER:' }).first();
    await expect(cmd).toContainText('Onay bekliyor');
    page.once('dialog', dialog => dialog.accept());
    await cmd.getByRole('button', { name: 'Onayla' }).click();
    await expect(page.getByText(/kanala gönderimi onayla yapıldı/)).toBeVisible();
  } finally {
    await page.request.put(`/api/channels/${hb.id}/capabilities/SIPARIS_DURUM`, { data: { state: 'KAPALI' } });
  }
});

test('Kanal iade talebi: Depo depoya girer, Yönetici onaylar; karar denetim izine ve kanal kuyruğuna yazılır (ADR-027)', async ({ page }) => {
  const d = demo();
  await login(page);
  const channels: { id: number; code: string }[] = await (await page.request.get('/api/channels')).json();
  const ty = channels.find(c => c.code === 'TRENDYOL')!;
  try {
    await page.request.put(`/api/channels/${ty.id}/capabilities/IADE_KARAR`, { data: { state: 'DENEME' } });
    await page.getByRole('navigation').getByRole('link', { name: 'İade / İptal' }).click();
    await page.getByRole('button', { name: 'Kanal iade talepleri' }).click();
    await page.getByLabel('İade no').fill(d.returnNo);
    const row = page.getByTestId('channel-return-row').filter({ hasText: d.returnNo });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('0/1');
    await row.click();

    const detail = page.getByTestId('return-detail');
    await detail.getByRole('button', { name: 'Depoya ulaştı' }).click();
    await expect(detail.getByText('Depoya girdi')).toBeVisible();

    page.once('dialog', dialog => dialog.accept());
    await detail.getByRole('button', { name: 'Onayla' }).click();
    await expect(page.getByTestId('return-decisions')).toContainText('Onay');
    await expect(page.getByTestId('return-decisions')).toContainText('Onay bekliyor');
    await expect(row).toContainText('1/1');
  } finally {
    await page.request.put(`/api/channels/${ty.id}/capabilities/IADE_KARAR`, { data: { state: 'KAPALI' } });
  }
});

test('Fiyat: önizleme "N ilanın fiyatı değişecek", eşik aşan fiyat Yönetici onayıyla kapanır, gönderim önizleme sonrası açılır (ADR-028)', async ({ page }) => {
  const d = demo();
  await login(page);
  const channels: { id: number; code: string }[] = await (await page.request.get('/api/channels')).json();
  const ty = channels.find(c => c.code === 'TRENDYOL')!;
  try {
    await page.getByRole('navigation').getByRole('link', { name: 'Fiyat' }).click();
    await expect(page.getByRole('heading', { name: 'Fiyat', exact: true })).toBeVisible();

    // 1) Onay bekleyen: Hepsiburada ilanı 300 → 499,90 (%66,63 > %30)
    const approval = page.getByTestId('price-approval-row').filter({ hasText: d.priceSkuCode });
    await expect(approval).toHaveCount(1);
    await expect(approval).toContainText('%66.63');
    await expect(approval).toContainText('Eşik aşıldı');
    page.once('dialog', dialog => dialog.accept());
    await approval.getByRole('button', { name: 'Onayla' }).click();
    await expect(page.getByTestId('price-approval-row').filter({ hasText: d.priceSkuCode })).toHaveCount(0);
    await page.getByLabel('Onay durumu').selectOption('ONAYLANDI');
    await expect(page.getByTestId('price-approval-row').filter({ hasText: d.priceSkuCode })).toContainText('e2e-yonetici');

    // 2) Önizleme (Trendyol, gönderim kapalı): demo SKU değişecekler arasında, kanala hiçbir şey gitmedi
    await page.getByLabel('Fiyat kanalı').selectOption(String(ty.id));
    await expect(page.getByTestId('price-capability')).toHaveText('Kapalı');
    await expect(page.getByTestId('price-preview-summary')).toContainText('ilanın fiyatı değişecek');
    const row = page.getByTestId('price-preview-row').filter({ hasText: d.priceSkuCode }).first();
    await expect(row).toContainText('Değişecek');
    await expect(row).toContainText('499,90');

    // 3) Önizlemeden sonra aç (onay penceresi özetle gelir)
    page.once('dialog', dialog => {
      expect(dialog.message()).toContain('ilanın fiyatı değişecek');
      void dialog.accept();
    });
    await page.getByRole('button', { name: 'Fiyat gönderimini aç' }).click();
    await expect(page.getByTestId('price-capability')).toHaveText('Açık');
  } finally {
    await page.request.put(`/api/channels/${ty.id}/capabilities/FIYAT`, { data: { state: 'KAPALI' } });
  }
});
