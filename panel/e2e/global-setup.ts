import { request, type FullConfig } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

export type Demo = {
  tag: string; skuCodes: string[]; orderNos: string[]; failedSyncId: number;
  deviationSkuCode: string; deviationExpected: number; deviationChannelStock: number; multiListingSkuCode: string; returnNo: string;
};

export const demoFile = join(process.cwd(), 'test-results', 'demo.json');

/** Test veritabanına etiketli demo veri yükler (POST /api/dev/demo-data; yalnızca Testing/Development). */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0].use.baseURL!;
  const api = await request.newContext({ baseURL });
  const login = await api.post('/api/auth/login', { data: { userName: 'e2e-yonetici', password: process.env.E2E_PASSWORD } });
  if (!login.ok()) throw new Error(`e2e girişi başarısız: ${login.status()}`);
  const seeded = await api.post('/api/dev/demo-data');
  if (!seeded.ok()) throw new Error(`Demo veri yüklenemedi: ${seeded.status()} ${await seeded.text()}`);
  const demo = (await seeded.json()) as Demo;
  const { mkdirSync } = await import('node:fs');
  mkdirSync(join(process.cwd(), 'test-results'), { recursive: true });
  writeFileSync(demoFile, JSON.stringify(demo));
  await api.dispose();
}
