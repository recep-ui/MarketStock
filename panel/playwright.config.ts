import { defineConfig } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { tmpdir } from 'node:os';

/**
 * Playwright: panel + API uçtan uca. API, TEST veritabanına (PazaryeriHub_Test) karşı "Testing" ortamında ayrı bir
 * portta başlatılır ve derlenmiş paneli (dist) servis eder. Tarayıcı indirilmez: kurulu Chrome kullanılır.
 * Gizli bilgi repoya yazılmaz: bağlantı dizesi ortam değişkeninden veya test projesinin user-secrets dosyasından okunur;
 * e2e kullanıcılarının parolası her çalıştırmada rastgele üretilir.
 */
function testConnectionString(): string {
  if (process.env.PAZARYERIHUB_TEST_DB) return process.env.PAZARYERIHUB_TEST_DB;
  const file = join(process.env.APPDATA ?? '', 'Microsoft', 'UserSecrets', 'pazaryerihub-tests-integration-4b1e9a2c', 'secrets.json');
  if (existsSync(file)) {
    // dotnet user-secrets dosyası UTF-8 BOM ile yazılır
    const value = JSON.parse(readFileSync(file, 'utf8').replace(/^﻿/, ''))['ConnectionStrings:PazaryeriHubTest'];
    if (value) return value;
  }
  throw new Error('Test veritabanı bağlantısı bulunamadı: PAZARYERIHUB_TEST_DB veya test projesinin user-secrets (ConnectionStrings:PazaryeriHubTest).');
}

// Ana süreçte bir kez üretilir; worker süreçleri ortamı devralır.
process.env.E2E_PASSWORD ??= 'E2e!' + randomBytes(18).toString('base64url');
const port = 5091;

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  // Soğuk başlangıçta (ilk istekler JIT) 5 sn'lik varsayılan bekleme bazen yetmiyor
  expect: { timeout: 10_000 },
  workers: 1,
  retries: 0,
  reporter: [['list']],
  globalSetup: './e2e/global-setup.ts',
  use: {
    baseURL: `http://localhost:${port}`,
    // Kurulu tarayıcı kullanılır (indirme yok). Chrome yoksa: E2E_BROWSER_CHANNEL=msedge
    channel: process.env.E2E_BROWSER_CHANNEL ?? 'chrome',
    headless: true,
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure'
  },
  webServer: {
    command: 'dotnet run --project ../src/PazaryeriHub.Api --no-launch-profile',
    url: `http://localhost:${port}/health/live`,
    timeout: 240_000,
    reuseExistingServer: false,
    stdout: 'ignore',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: `http://localhost:${port}`,
      ConnectionStrings__PazaryeriHub: testConnectionString(),
      Database__MigrateOnStartup: 'true',
      Panel__DistPath: join(process.cwd(), 'dist'),
      Kimlik__SeedUsers__0__UserName: 'e2e-yonetici',
      Kimlik__SeedUsers__0__FullName: 'E2E Yönetici',
      Kimlik__SeedUsers__0__Role: 'Yonetici',
      Kimlik__SeedUsers__0__Password: process.env.E2E_PASSWORD,
      Kimlik__SeedUsers__1__UserName: 'e2e-destek',
      Kimlik__SeedUsers__1__FullName: 'E2E Destek',
      Kimlik__SeedUsers__1__Role: 'Destek',
      Kimlik__SeedUsers__1__Password: process.env.E2E_PASSWORD,
      // Arka plan job'ları kapalı: senaryolar deterministik olsun (ör. "yeniden dene" sonrası kayıt hemen gönderilmesin)
      Hangfire__Queues__0: 'e2e-bos',
      Senkron__OutboxCron: '',
      Senkron__PushCron: '',
      Senkron__PricePushCron: '',
      Senkron__BatchCheckCron: '',
      Senkron__ReconciliationCron: '',
      Siparis__FetchCron: '',
      Serilog__WriteTo__1__Args__path: join(tmpdir(), 'pazaryerihub-e2e', 'log-.json')
    }
  }
});
