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
  // Önceki filtrenin tek satırı (S6) da 1 sayılır: liste yenilenmeden tıklamamak için doğru satırı bekle
  await expect(rows.first()).toContainText(`DEMO-${d.tag}-S2`);
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
  // ADR-033: ham metin yerine "ne oldu + ne yapmalı"; ham metin "Teknik ayrıntı"da
  const explain = row.getByTestId('error-explain');
  await expect(explain).toHaveAttribute('data-kind', 'KANAL_ERISILEMIYOR');
  await expect(explain).toContainText('Kanala şu an ulaşılamıyor');
  await expect(explain).toContainText('sistem yeniden dener');
  await explain.getByText('Teknik ayrıntı').click();
  await expect(explain).toContainText('Kanal hata döndü (HTTP 503): Servis geçici olarak kullanılamıyor');
  await expect(row).toHaveClass(/row-danger/);

  await row.getByRole('button', { name: 'Yeniden dene' }).click();
  await expect(page.getByRole('status')).toContainText('yeniden kuyruğa alındı');
  await expect(row).toHaveCount(0); // artık HATA listesinde değil

  await page.getByLabel('Durum', { exact: true }).selectOption({ label: 'Bekliyor' });
  // Aynı SKU'nun Hepsiburada kaydı onay bekliyor (ADR-048); burada yeniden denenen Trendyol kaydına bakılır
  const pending = page.getByTestId('sync-row').filter({ hasText: d.skuCodes[0] }).filter({ hasText: 'Trendyol' });
  await expect(pending).toHaveCount(1);
  await expect(pending).toContainText('Bekliyor');
  await expect(pending.getByRole('button', { name: 'Yeniden dene' })).toHaveCount(0);
});

test('Stok Listesi kanal sekmeleri: her pazaryerinin listesi ayrı; olası yanlış eşleşme ve sapma ayrı süzülür (ADR-035)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Stok Listesi', exact: true }).click();
  await expect(page.getByRole('tab', { name: 'Defter (tüm kanallar)' })).toHaveAttribute('aria-selected', 'true');

  // Trendyol: demo SKU'ların 4 ilanı + çoklanan ilan; çoklanan ilanın barkodu farklı ve kodla doğrulanamıyor → Kontrol et
  await page.getByRole('tab', { name: 'Trendyol' }).click();
  await expect(page).toHaveURL(/kanal=TRENDYOL/);
  const rows = page.getByTestId('channel-stock-row');
  await page.getByLabel('Kanal ilanlarında bul').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(5);
  // ADR-043: kanaldaki durum sütunu ve filtresi. Demo ilanlarda kanal durumu yok → "Bilinmiyor"; "Satışta" süzgeci boş döner
  await expect(page.getByTestId('channel-stock-table').locator('th', { hasText: 'Kanaldaki durum' })).toBeVisible();
  await expect(rows.first().getByTestId('channel-sale-state')).toContainText('Bilinmiyor');
  await page.getByLabel('Kanaldaki durum').selectOption('SATISTA');
  await expect(rows).toHaveCount(0);
  await page.getByLabel('Kanaldaki durum').selectOption('BILINMIYOR');
  await expect(rows).toHaveCount(5);
  await page.getByLabel('Kanaldaki durum').selectOption('');
  await page.getByLabel('Yalnızca kontrol gerekenler').check();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(d.multiListingSkuCode);
  await expect(rows.first()).toContainText('Barkod farklı');
  await expect(rows.first()).toHaveClass(/row-review/);

  // Hepsiburada: sapmalı ilan, kanalın bildirdiği stokla
  await page.getByRole('tab', { name: 'Hepsiburada' }).click();
  await expect(page).toHaveURL(/kanal=HEPSIBURADA/);
  await page.getByLabel('Kanal ilanlarında bul').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await expect(rows).toHaveCount(4);
  // Yalnızca okur; demo sapmasını kapatan senaryolardan (Kanala gönder / deftere al) önce çalışır. Süzülen her satır sapmalı olmalı
  await page.getByLabel('Yalnızca stok sapmaları').check();
  const deviating = rows.filter({ hasText: d.deviationSkuCode });
  await expect(deviating).toHaveCount(1);
  await expect(deviating).toContainText(`sapma: kanalda ${d.deviationChannelStock}`);
  await expect(deviating.getByText('✓ Barkod')).toBeVisible();
  await expect(rows.filter({ hasNotText: 'sapma: kanalda' })).toHaveCount(0);

  // Bağlı SKU'dan detaya
  await deviating.getByRole('link', { name: d.deviationSkuCode }).click();
  await expect(page.getByTestId('sku-detail').getByRole('heading', { level: 1 })).toContainText(d.deviationSkuCode);

  // Defter sekmesi önceki gibi
  await page.goBack();
  await page.getByRole('tab', { name: 'Defter (tüm kanallar)' }).click();
  await expect(page).not.toHaveURL(/kanal=/);
  await expect(page.getByRole('button', { name: 'Ara' })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Eşleştirme listesi kanaldaki ürünü bağlı SKU ile yan yana gösterir; uyarılı eşleşmeden kanal sekmesine gidilir (ADR-044)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Eşleştirme', exact: true }).click();
  const card = page.locator('section, .card').filter({ has: page.getByRole('heading', { name: 'Eşleştirmeler' }) }).first();
  await card.getByLabel('Kanal').selectOption({ label: 'Trendyol' });
  await card.getByLabel('Ara').fill(`DEMO-${d.tag}`);
  await card.getByRole('button', { name: 'Ara' }).click();
  const rows = page.getByTestId('listing-row');
  await expect(rows).toHaveCount(5);
  // Her satırda iki taraf ve eşleşme dayanağı
  await expect(rows.first().getByTestId('listing-channel-side')).toBeVisible();
  await expect(rows.first().getByTestId('listing-sku-side')).toContainText(`DEMO-${d.tag}`);
  await expect(rows.first().getByTestId('listing-match').locator('.badge')).toBeVisible();
  // Çoklanan ilan: barkod farklı → uyarı bağlantısı kanal sekmesine götürür
  const multi = rows.filter({ hasText: d.multiListingSkuCode }).filter({ has: page.getByText(/uyarı, kontrol et/) });
  await expect(multi).toHaveCount(1);
  await multi.getByText(/uyarı, kontrol et/).click();
  await expect(page).toHaveURL(/\/stok\?kanal=TRENDYOL/);
  await expect(page.getByTestId('channel-stock-table')).toBeVisible();
});

test('Doğru eşleşme onayı: uyarı Kontrol et listesinden çıkar, onaylayan görünür, geri alınınca döner; Destek onaylayamaz (ADR-036)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.goto('/stok?kanal=TRENDYOL');
  const rows = page.getByTestId('channel-stock-row');
  await page.getByLabel('Kanal ilanlarında bul').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await page.getByLabel('Yalnızca kontrol gerekenler').check();
  await expect(rows).toHaveCount(1);
  const suspect = rows.first();
  await expect(suspect).toContainText(d.multiListingSkuCode);

  page.once('dialog', dialog => dialog.accept());
  await suspect.getByRole('button', { name: 'Doğru eşleşme, onayla' }).click();
  await expect(rows).toHaveCount(0);

  await page.getByLabel('Yalnızca kontrol gerekenler').uncheck();
  const approved = rows.filter({ has: page.getByTestId('match-approved') });
  await expect(approved).toHaveCount(1);
  await expect(approved).toContainText(d.multiListingSkuCode);
  await expect(approved.getByTestId('match-approved')).toContainText('e2e-yonetici');
  await expect(approved).toContainText('Barkod farklı');
  await expect(approved).not.toHaveClass(/row-review/);

  page.once('dialog', dialog => dialog.accept());
  await approved.getByRole('button', { name: 'Onayı geri al' }).click();
  await expect(page.getByTestId('match-approved')).toHaveCount(0);
  await page.getByLabel('Yalnızca kontrol gerekenler').check();
  await expect(rows).toHaveCount(1);
  await expect(rows.first().getByRole('button', { name: 'Doğru eşleşme, onayla' })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);

  // Destek: uyarıyı görür, onaylayamaz
  await page.getByRole('button', { name: 'Çıkış' }).click();
  await login(page, 'e2e-destek');
  await page.goto('/stok?kanal=TRENDYOL');
  await page.getByLabel('Kanal ilanlarında bul').fill(`DEMO-${d.tag}`);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  await page.getByLabel('Yalnızca kontrol gerekenler').check();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText('Barkod farklı');
  await expect(page.getByRole('button', { name: 'Doğru eşleşme, onayla' })).toHaveCount(0);
});

test('Kanal sekmesinde tek yerden stok kuralı ve ilan fiyatı kaydedilir; görsel alanı ve fiyat yeteneği görünür (ADR-042)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.goto('/stok?kanal=TRENDYOL');
  const rows = page.getByTestId('channel-stock-row');
  await page.getByLabel('Kanal ilanlarında bul').fill(d.skuCodes[0]);
  await page.getByRole('button', { name: 'Filtrele' }).click();
  const row = rows.first();
  await expect(row).toBeVisible();
  await expect(page.getByTestId('price-capability')).toContainText('fiyat gönderimi');
  // Demo ilanlarda kanal görseli yok: yer tutucu gösterilir (gerçek kanalda kanalın https görseli)
  await expect(row.getByLabel('görsel yok')).toBeVisible();

  // Üst limit 1 + ilana özel fiyat → tek "Kaydet"
  await row.getByLabel('Stok kuralı').selectOption('UST_LIMIT');
  await row.getByLabel('Stok adedi').fill('1');
  await row.getByLabel('İlan satış fiyatı').fill('123,45');
  await expect(page.getByTestId('save-bar')).toContainText('1 ilanda kaydedilmemiş değişiklik');
  await page.getByTestId('save-all').click();
  await expect(page.getByTestId('save-result')).toContainText('1 ilan kaydedildi');
  await expect(row.getByTestId('listing-target')).toHaveText('1');
  await expect(row.getByLabel('Stok kuralı')).toHaveValue('UST_LIMIT');
  await expect(row.getByLabel('İlan satış fiyatı')).toHaveValue('123.45');

  // Geçersiz değer kaydedilemez
  await row.getByLabel('Stok adedi').fill('-3');
  await expect(page.getByTestId('save-all')).toBeDisabled();

  // Geri al: ortak havuz + fiyat boş (diğer senaryoları etkilemesin)
  await row.getByLabel('Stok kuralı').selectOption('');
  await row.getByLabel('İlan satış fiyatı').fill('');
  await page.getByTestId('save-all').click();
  await expect(page.getByTestId('save-result')).toContainText('1 ilan kaydedildi');
  await expect(row.getByLabel('Stok kuralı')).toHaveValue('');
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Kanal test ortamında ve eşleşmiş ilanı yoksa Kanal Stoğu ve Eşleştirme nedenini söyler (ADR-038)', async ({ page }) => {
  await login(page);
  const channels: { id: number; code: string }[] = await (await page.request.get('/api/channels')).json();
  const hb = channels.find(c => c.code === 'HEPSIBURADA')!;

  // Sunucu durumu belirli olsun: Hepsiburada gerçek modda, TEST ortamında; 0 eşleşmiş ilan, 3 eşleşmeyen ürün
  await page.route('**/api/channels/status', async route => {
    const res = await route.fetch();
    const list = (await res.json()) as { id: number; mode: string; environment: string | null }[];
    await route.fulfill({ response: res, json: list.map(s => (s.id === hb.id ? { ...s, mode: 'GERCEK', environment: 'Test' } : s)) });
  });
  await page.route(`**/api/stock-overview/channels/${hb.id}?*`, route => route.fulfill({
    json: {
      channelId: hb.id, channelCode: 'HEPSIBURADA', rows: { items: [], total: 0, page: 1, pageSize: 1 },
      totalListings: 0, deviationCount: 0, reviewCount: 0, notFetchedCount: 0, unmatchedCount: 3, lastFetchedUtc: null, approvedCount: 0
    }
  }));
  await page.route('**/api/stock/channel-intake/preview?*', async route => {
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), rows: [] } });
  });
  await page.route(`**/api/channels/${hb.id}/catalog/fetch-status`, route => route.fulfill({
    json: {
      channelId: hb.id, state: 'TAMAMLANDI', lastAttemptUtc: new Date().toISOString(), lastSuccessUtc: new Date().toISOString(), lastError: null,
      report: {
        channelCode: 'HEPSIBURADA', format: null, source: 'API', validateOnly: false, createMissing: true, totalRows: 2, validRows: 2,
        matchedExisting: 0, matchedByBarcode: 0, matchedByStockCode: 0, matchedByModelColorSize: 0,
        listingsCreated: 0, listingsUpdated: 0, listingsUnchanged: 0, productsCreated: 0, skusCreated: 0,
        unmatched: 2, ambiguous: 0, conflicts: 0, withSuggestion: 0, passive: 0, pushDeferred: 0, rowsTruncated: false,
        errors: [], fileErrors: [], warnings: [],
        rows: [1, 2].map(n => ({
          row: n, barcode: null, channelProductCode: `HBV0000TEST${n}`, stockCode: `HBV0000STK${n}`, name: null, color: null, size: null,
          outcome: 'ESLESME_YOK', skuCode: null, step: null, listingChange: null, detail: 'SKU bulunamadı.', suggestedSkuCode: null,
          suggestionScore: null, passive: false
        }))
      }
    }
  }));

  // Kanal Stoğu → Defter: nedeni ve doğru adımı söyler; boş tablo mutabakata yönlendirmez
  await page.getByRole('navigation').getByRole('link', { name: 'Kanal Stoğu → Defter' }).click();
  const source = page.getByLabel('Kaynak kanal');
  await source.selectOption(String(hb.id));
  await expect(page.getByTestId('channel-source-test')).toContainText('TEST ortamına bağlı');
  const noListings = page.getByTestId('intake-no-listings');
  await expect(noListings).toContainText('eşleşmiş ilan yok');
  await expect(noListings).toContainText('3 kanal ürünü eşleşme bekliyor');
  await page.getByRole('button', { name: 'Önizle' }).click();
  await expect(page.getByTestId('intake-empty')).toContainText('bu kanalda eşleşmiş ilan yok');
  await expect(page.getByTestId('intake-empty')).not.toContainText('mutabakat çalıştırın');

  // Eşleştirme → Kanaldan ürün çek: test ortamı uyarısı ve "neden eşleşmedi"
  await noListings.getByRole('link', { name: 'Eşleştirme' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Eşleştirme' })).toBeVisible();
  await page.getByLabel('Kanal (API)').selectOption(String(hb.id));
  await expect(page.getByTestId('channel-source-test')).toBeVisible();
  const help = page.getByTestId('catalog-no-match');
  await expect(help).toContainText("Hiçbir ürün defterdeki bir SKU'ya bağlanmadı");
  await expect(help).toContainText('2 ürünün stok kodu');
  await expect(help).toContainText('2 üründe barkod yok');
  await expect(help).toContainText("barkodsuz üründen SKU oluşturulmaz");
});

test('Tüm ekranlar demo verilerle açılır (hata yok)', async ({ page }) => {
  await login(page);
  const screens: [string, string][] = [
    ['Genel Bakış', 'Genel Bakış'], ['Stok Listesi', 'Stok Listesi'], ['Stok Hareketleri', 'Stok Hareketleri'],
    ['Siparişler', 'Siparişler'], ['Raporlar', 'Raporlar'], ['İade / İptal', 'İade / İptal'], ['Eşleştirme', 'Eşleştirme'],
    ['Senkron Merkezi', 'Senkron Merkezi'], ['Kanal Stoğu → Defter', 'Kanal Stoğu → Defter'], ['Fiyat', 'Fiyat'], ['Soru-Cevap', 'Soru-Cevap'],
    ['Ayarlar', 'Ayarlar']
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

test('Kanal durumu: kartlar mod ve durumu metinle gösterir, kart seçilince kanal ayarları açılır (ADR-033)', async ({ page }) => {
  await login(page);
  const statuses: { code: string; mode: string; health: string }[] = await (await page.request.get('/api/channels/status')).json();
  expect(statuses.map(s => s.code).sort()).toEqual(['BEYMEN', 'HEPSIBURADA', 'SOFTCOTTON', 'TRENDYOL']);
  expect(statuses.every(s => s.mode === 'MOCK')).toBe(true); // e2e'de tüm kanallar mock

  await page.getByRole('navigation').getByRole('link', { name: 'Ayarlar' }).click();
  const softcotton = page.getByTestId('channel-status-SOFTCOTTON');
  await expect(softcotton).toContainText('Sahte (mock) — gerçek entegrasyon yok');
  await expect(softcotton.getByTestId('health-badge')).not.toHaveText('');
  for (const code of ['TRENDYOL', 'HEPSIBURADA', 'BEYMEN']) {
    await expect(page.getByTestId(`channel-status-${code}`).getByTestId('health-badge')).toBeVisible();
  }
  await expect(page.getByTestId('capability-matrix')).toContainText('Stok gönderimi');

  await page.getByTestId('channel-status-TRENDYOL').getByRole('button', { name: 'Ayarlar' }).click();
  const detail = page.getByTestId('channel-detail');
  await expect(detail).toContainText('sahte (mock) modda');
  await expect(detail.getByTestId('channel-save')).toBeDisabled(); // değişiklik yokken
  await detail.getByLabel(/pay$/).fill('99');
  await expect(detail).toContainText('Kaydedilmemiş değişiklik var');
  await page.getByRole('button', { name: 'Kapat', exact: true }).click();
  await expect(page.getByTestId('channel-detail')).toHaveCount(0);

  await page.getByRole('tab', { name: 'Stok kuralları' }).click();
  await expect(page.getByRole('heading', { name: 'Uyarı eşikleri' })).toBeVisible();

  await page.getByRole('navigation').getByRole('link', { name: 'Genel Bakış' }).click();
  // e2e'de kanallar pasif ve API bilgisi yok: boş durum açıklaması görünür
  await expect(page.getByRole('heading', { name: 'Kanal durumu' })).toBeVisible();
  await expect(page.getByTestId('no-active-channel').or(page.getByTestId('channel-status-grid'))).toBeVisible();
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

test('Stok gönderim onayı: kanal kutucuğu, ürün ve kanalda/gönderilecek değer görünür; özetli onayla gönderilir (ADR-048)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Senkron Merkezi' }).click();

  const approvals = page.getByTestId('stock-approvals');
  const row = approvals.getByTestId('approval-row').filter({ hasText: d.skuCodes[0] }).filter({ hasText: 'Hepsiburada' });
  await expect(row).toHaveCount(1);
  await expect(row.getByRole('checkbox')).toBeChecked();

  // Kanal kutucuğu kaldırılınca o kanalın satırları listeden çıkar, gönderilecek sayı düşer
  const hb = page.getByTestId('approval-channels').getByLabel('Hepsiburada kanalı');
  await hb.uncheck();
  await expect(row).toHaveCount(0);
  await hb.check();
  await expect(row).toHaveCount(1);

  // Yalnızca bu satır gönderilsin
  await page.getByRole('button', { name: 'Seçimi kaldır' }).click();
  await row.getByRole('checkbox').check();
  await expect(page.getByTestId('approve-stock')).toHaveText('Seçilenleri kanala gönder (1)');

  let message = '';
  page.once('dialog', dialog => { message = dialog.message(); void dialog.accept(); });
  await page.getByTestId('approve-stock').click();
  await expect(page.getByRole('status')).toContainText('1 stok gönderimi onaylandı');
  expect(message).toContain('Hepsiburada: 1 ürün');
  await expect(row).toHaveCount(0);
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
  // Küçük resim alanı (demo ilanlarında görsel yok → yer tutucu)
  await expect(row.locator('.thumb')).toHaveCount(1);
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
    await page.getByTestId('channel-status-HEPSIBURADA').getByRole('button', { name: 'Ayarlar' }).click();
    await expect(page.getByTestId('channel-detail')).toBeVisible();
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
    await page.getByRole('navigation').getByRole('link', { name: 'Fiyat', exact: true }).click();
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

test('Fiyat: aynı SKU\'nun kanal fiyatları yan yana; kanal bazında ayrı elle fiyat özetli onayla kaydedilir (ADR-049)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Fiyat', exact: true }).click();
  await expect(page.getByTestId('price-notes')).toContainText('Nasıl çalışır?');

  await page.getByLabel('Fiyat ürün ara').fill(d.priceSkuCode);
  await page.getByRole('button', { name: 'Ara', exact: true }).click();
  const row = page.getByTestId('sku-price-row').filter({ hasText: d.priceSkuCode });
  await expect(row).toHaveCount(1);
  const ty = row.getByTestId('price-cell-TRENDYOL');
  const hb = row.getByTestId('price-cell-HEPSIBURADA');
  await expect(ty).toContainText('Gidecek');
  await expect(hb).toContainText('Gidecek');

  // Yalnızca Hepsiburada'ya farklı fiyat; geçersiz değer kaydı engeller
  const hbInput = row.getByLabel(`Hepsiburada elle fiyat ${d.priceSkuCode}`);
  await hbInput.fill('12,345');
  await expect(page.getByTestId('save-prices')).toBeDisabled();
  await hbInput.fill('549,90');
  await expect(page.getByTestId('save-prices')).toHaveText('Değişiklikleri kaydet (1)');

  let message = '';
  page.once('dialog', dialog => { message = dialog.message(); void dialog.accept(); });
  await page.getByTestId('save-prices').click();
  await expect(page.getByRole('status')).toContainText('1 ilan fiyatı kaydedildi');
  expect(message).toContain(`${d.priceSkuCode} · Hepsiburada`);
  expect(message).toContain('elle ₺549,90');

  await expect(hb).toContainText('elle');
  await expect(hb).toContainText('549,90');
  await expect(ty).not.toContainText('elle');

  // Temizle: elle fiyatı kaldır
  await row.getByLabel(`Hepsiburada elle fiyat ${d.priceSkuCode}`).fill('');
  page.once('dialog', dialog => void dialog.accept());
  await page.getByTestId('save-prices').click();
  await expect(hb).not.toContainText('elle');
});

test('Fiyat & Stok Düzenle: tek satırda kanal durumu/stoğu görünür; satılabilir stok özetli onayla deftere yazılır (ADR-050)', async ({ page }) => {
  const d = demo();
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Fiyat & Stok Düzenle' }).click();
  await expect(page.getByRole('heading', { name: 'Fiyat & Stok Düzenle' })).toBeVisible();

  await page.getByLabel('Fiyat ürün ara').fill(d.priceSkuCode);
  await page.getByRole('button', { name: 'Ara', exact: true }).click();
  const row = page.getByTestId('sku-price-row').filter({ hasText: d.priceSkuCode });
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('price-cell-TRENDYOL').getByTestId('channel-stock')).toContainText('gidecek');

  const input = row.getByLabel(`Satılabilir stok ${d.priceSkuCode}`);
  const before = Number(await input.inputValue());
  await input.fill('-1');
  await expect(page.getByTestId('save-prices')).toBeDisabled();
  await input.fill(String(before + 3));
  await expect(row.getByTestId('stock-cell')).toContainText('Yeni fiziksel');
  await expect(page.getByTestId('save-prices')).toHaveText('Değişiklikleri kaydet (1)');

  let message = '';
  page.once('dialog', dialog => { message = dialog.message(); void dialog.accept(); });
  await page.getByTestId('save-prices').click();
  await expect(page.getByRole('status')).toContainText('1 ürünün stoğu yazıldı');
  expect(message).toContain(`${d.priceSkuCode}: satılabilir ${before} → ${before + 3}`);
  await expect(row.getByTestId('stock-cell')).toContainText(`Satılabilir: ${before + 3}`);

  // Geri al
  await row.getByLabel(`Satılabilir stok ${d.priceSkuCode}`).fill(String(before));
  page.once('dialog', dialog => void dialog.accept());
  await page.getByTestId('save-prices').click();
  await expect(row.getByTestId('stock-cell')).toContainText(`Satılabilir: ${before}`);
});

test('Soru-Cevap: Destek onay kutusunu işaretleyip cevap gönderir; cevap kullanıcı adıyla kaydedilir ve kanal kuyruğuna girer (ADR-029)', async ({ page }) => {
  const d = demo();
  await login(page);
  const channels: { id: number; code: string }[] = await (await page.request.get('/api/channels')).json();
  const ty = channels.find(c => c.code === 'TRENDYOL')!;
  await page.request.put(`/api/channels/${ty.id}/capabilities/SORU_CEVAP`, { data: { state: 'ACIK' } });
  let questionId: number | undefined;
  try {
    await page.context().clearCookies();
    await login(page, 'e2e-destek');
    await page.getByRole('navigation').getByRole('link', { name: 'Soru-Cevap' }).click();
    await page.getByLabel('Soru ara').fill(d.questionNo);
    const row = page.getByTestId('question-row');
    await expect(row).toHaveCount(1);
    await row.click();

    const detail = page.getByTestId('question-detail');
    await expect(detail.getByTestId('question-text')).toContainText('pamuk');
    const answer = 'Merhaba, ürünümüz %100 pamuktur. İyi günlerde kullanın.';
    await detail.getByLabel('Cevap').fill(answer);
    const send = detail.getByRole('button', { name: 'Cevabı gönder' });
    await expect(send).toBeDisabled();                       // onay kutusu işaretlenmeden gönderilmez
    await expect(detail.getByTestId('answer-preview')).toContainText(answer);
    await detail.getByRole('checkbox').check();
    await send.click();

    const answers = detail.getByTestId('question-answers');
    await expect(answers).toContainText(answer);
    await expect(answers).toContainText('e2e-destek');
    await expect(answers).toContainText('Kuyrukta');
    await expect(detail.getByTestId('cannot-answer')).toContainText('zaten bir cevap var');

    const list = await (await page.request.get(`/api/questions?search=${encodeURIComponent(d.questionNo)}`)).json();
    questionId = list.items[0].id;
  } finally {
    // Temizlik (Yönetici): bekleyen komut iptal, yetenek kapalı — e2e'de komut job'ı çalışmaz, kanala bir şey gitmez
    await page.context().clearCookies();
    await login(page);
    if (questionId) {
      const detail = await (await page.request.get(`/api/questions/${questionId}`)).json();
      for (const a of detail.answers) if (a.commandId) await page.request.post(`/api/channel-commands/${a.commandId}/cancel`);
    }
    await page.request.put(`/api/channels/${ty.id}/capabilities/SORU_CEVAP`, { data: { state: 'KAPALI' } });
  }
});

test('Genel arama: SKU kodu ile SKU detayı açılır, detaydan siparişe; sipariş no ile sipariş detayı (ADR-034)', async ({ page }) => {
  const d = demo();
  await login(page);

  // "/" kısayolu arama kutusunu odaklar
  await page.keyboard.press('/');
  const search = page.getByRole('combobox', { name: 'Hızlı bul' });
  await expect(search).toBeFocused();
  await search.fill(d.skuCodes[0].toLowerCase());
  const results = page.getByTestId('search-results');
  await expect(results.getByRole('option').first()).toContainText(d.skuCodes[0]);
  await search.press('Enter');

  await expect(page).toHaveURL(/\/stok\/\d+$/);
  const detail = page.getByTestId('sku-detail');
  await expect(detail.getByRole('heading', { level: 1 })).toContainText(d.skuCodes[0]);
  await expect(page.getByTestId('sku-available')).toBeVisible();
  await expect(detail.getByText('Son siparişler')).toBeVisible();
  const orders = page.getByTestId('sku-order');
  await expect(orders.first()).toBeVisible();
  const orderNo = (await orders.first().getByRole('link').textContent())!.trim();
  await orders.first().getByRole('link').click();
  await expect(page.getByRole('heading', { name: 'Siparişler' })).toBeVisible();
  await expect(page.getByTestId('order-detail')).toContainText(orderNo);

  // Sipariş no ile arama → sipariş detayı
  await search.fill(`DEMO-${d.tag}-S2`);
  await expect(results.getByRole('option').first()).toContainText(`DEMO-${d.tag}-S2`);
  await results.getByRole('option').first().click();
  await expect(page).toHaveURL(/\/siparisler\?order=\d+/);
  await expect(page.getByTestId('order-detail')).toContainText(`DEMO-${d.tag}-S2`);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Raporlar: bugünün satışları, kanal karşılaştırması, en çok satandan SKU detayı ve Excel indirme (ADR-034)', async ({ page }) => {
  await login(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Raporlar', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Raporlar' })).toBeVisible();

  await page.getByRole('button', { name: 'Bugün', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Bugün', exact: true })).toHaveAttribute('aria-pressed', 'true');
  const report = page.getByTestId('sales-report');
  await expect(report).toBeVisible();
  await expect(page.getByTestId('report-net-orders')).not.toContainText(/^0$/);
  await expect(page.getByTestId('daily-chart').locator('path.chart-bar')).toHaveCount(1);
  await expect(page.getByTestId('report-channels').locator('tbody tr')).toHaveCount(4);

  // Excel: seçili dönemin siparişleri indirilir
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: "Siparişleri Excel'e aktar" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/^siparisler-\d{8}-\d{4}\.xlsx$/);

  // En çok satan → SKU detayı
  const top = page.getByTestId('report-top-skus').locator('tbody tr').first().getByRole('link');
  const code = (await top.textContent())!.trim();
  await top.click();
  await expect(page.getByTestId('sku-detail').getByRole('heading', { level: 1 })).toContainText(code);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Kullanıcı yönetimi: Yönetici kullanıcı ekler, siler; silinenin açık oturumu kapanır; İşlem Geçmişi kaydı gösterir (ADR-045)', async ({ page, browser }) => {
  const userName = 'e2e-gecici-' + Date.now().toString(36);
  const password = process.env.E2E_PASSWORD!;
  await login(page);
  await page.goto('/ayarlar?sekme=kullanicilar');
  const form = page.locator('form', { has: page.getByRole('button', { name: 'Oluştur' }) });
  await form.getByLabel('Kullanıcı adı').fill(userName);
  await form.getByLabel('Ad soyad').fill('E2E Geçici');
  await form.getByLabel('Parola').fill(password);
  await form.getByRole('button', { name: 'Oluştur' }).click();
  const row = page.locator('tr', { hasText: userName });
  await expect(row).toBeVisible();
  // Kendi satırında Sil devre dışı
  await expect(page.locator('tr', { hasText: 'e2e-yonetici' }).getByRole('button', { name: 'Sil' })).toBeDisabled();

  // Yeni kullanıcı başka bir tarayıcıda oturum açar
  const other = await browser.newContext();
  const otherPage = await other.newPage();
  await login(otherPage, userName);
  await expect(otherPage.locator('aside nav').getByRole('link', { name: 'İşlem Geçmişi' })).toHaveCount(0); // Depo rolü görmez

  page.once('dialog', d => d.accept());
  await row.getByRole('button', { name: 'Sil' }).click();
  await expect(page.locator('tr', { hasText: userName })).toHaveCount(0);

  // Silinen kullanıcının oturumu hemen kapanır: bir sonraki istekte giriş ekranı
  await otherPage.goto('/siparisler');
  await expect(otherPage).toHaveURL(/\/giris/);
  await other.close();

  await page.locator('aside nav').getByRole('link', { name: 'İşlem Geçmişi' }).click();
  await page.getByLabel('Yol').fill('users');
  await page.getByRole('button', { name: 'Filtrele' }).click();
  const deleted = page.getByTestId('audit-row').filter({ hasText: 'Kullanıcılar · silme' }).first();
  await expect(deleted).toBeVisible();
  await expect(deleted).toContainText('e2e-yonetici');
  await expect(deleted).toContainText('Başarılı (204)');
});
