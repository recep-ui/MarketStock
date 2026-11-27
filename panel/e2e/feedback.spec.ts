import { expect, test, type Page } from '@playwright/test';

/** ADR-064: sol menüden Öneri ve İstekler; not yazanın adıyla kaydedilir, durumu yalnızca Yönetici değiştirir. */
async function login(page: Page, userName: string) {
  await page.goto('/giris');
  await page.getByLabel('Kullanıcı adı').fill(userName);
  await page.getByLabel('Parola').fill(process.env.E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByTestId('current-user')).toBeVisible();
}

test('Öneri ve İstekler: Destek not yazar, adıyla listelenir; Yönetici tamamlandı yapar (ADR-064)', async ({ page }) => {
  const text = `Stok listesine renk filtresi eklensin ${Date.now()}`;

  await login(page, 'e2e-destek');
  await page.getByRole('link', { name: 'Öneri ve İstekler' }).click();
  await page.getByTestId('feedback-text').fill(text);
  await page.getByTestId('feedback-submit').click();
  const row = page.getByTestId('feedback-row').filter({ hasText: text });
  await expect(row).toBeVisible();
  await expect(row.getByTestId('feedback-author')).toHaveText('E2E Destek');
  await expect(row).toContainText('(e2e-destek)');
  await expect(row.getByRole('button', { name: 'Tamamlandı' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Çıkış' }).click();
  await login(page, 'e2e-yonetici');
  await page.getByRole('link', { name: 'Öneri ve İstekler' }).click();
  const adminRow = page.getByTestId('feedback-row').filter({ hasText: text });
  await adminRow.getByRole('button', { name: 'Tamamlandı' }).click();
  await expect(adminRow.locator('.badge.ok')).toHaveText('Tamamlandı');
  await expect(adminRow).toContainText('e2e-yonetici');
});
