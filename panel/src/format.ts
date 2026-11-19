import type { OrderStatus } from './api';

const tz = 'Europe/Istanbul';
const dateTime = new Intl.DateTimeFormat('tr-TR', { timeZone: tz, dateStyle: 'short', timeStyle: 'short' });
const money = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY' });

export const formatDateTime = (utc: string | null | undefined) => (utc ? dateTime.format(new Date(utc)) : '—');
export const formatMoney = (value: number) => money.format(value);

/** yyyy-mm-dd (Türkiye günü) → o günün başlangıcı UTC ISO. Türkiye 2016'dan beri sabit UTC+3. */
export const dayStartUtc = (day: string) => (day ? new Date(`${day}T00:00:00+03:00`).toISOString() : undefined);
export const dayEndUtc = (day: string) => (day ? new Date(new Date(`${day}T00:00:00+03:00`).getTime() + 86_400_000).toISOString() : undefined);

export const statusLabels: Record<OrderStatus, string> = {
  Yeni: 'Yeni',
  Onaylandi: 'Onaylandı',
  Hazirlaniyor: 'Hazırlanıyor',
  Kargoda: 'Kargoda',
  TeslimEdildi: 'Teslim edildi',
  Iptal: 'İptal',
  Iade: 'İade'
};

/** Panelden izinli tek adımlık geçişler (sunucudaki durum makinesiyle aynı; asıl kontrol API'de). */
export const nextStatuses: Record<OrderStatus, OrderStatus[]> = {
  Yeni: ['Onaylandi', 'Iptal'],
  Onaylandi: ['Hazirlaniyor', 'Iptal'],
  Hazirlaniyor: ['Kargoda', 'Iptal'],
  Kargoda: ['TeslimEdildi', 'Iade'],
  TeslimEdildi: ['Iade'],
  Iptal: [],
  Iade: []
};

export const movementLabels: Record<string, string> = {
  GIRIS: 'Giriş',
  REZERVE: 'Rezerve',
  SEVK: 'Sevk',
  REZERVE_IPTAL: 'Rezerve iptal',
  IADE_GIRIS: 'İade girişi',
  SAYIM: 'Sayım'
};

export const syncStatusLabels: Record<string, string> = { BEKLIYOR: 'Bekliyor', GONDERILDI: 'Gönderildi', HATA: 'Hata' };

export const channelNames: Record<string, string> = {
  TRENDYOL: 'Trendyol',
  HEPSIBURADA: 'Hepsiburada',
  BEYMEN: 'Beymen',
  SOFTCOTTON: 'softcotton'
};

/** Kanal iş türlerinin Türkçe adları (sunucudaki ChannelHealth.JobLabel ile aynı). */
export const jobLabels: Record<string, string> = {
  SIPARIS_CEK: 'Sipariş çekme',
  IADE_CEK: 'İade çekme',
  SORU_CEK: 'Soru çekme',
  KATALOG_CEK: 'Ürün çekme',
  MUTABAKAT: 'Stok mutabakatı',
  REFERANS_CEK: 'Satıcı bilgileri'
};

const relative = new Intl.RelativeTimeFormat('tr-TR', { numeric: 'auto' });

/** "3 dakika önce" biçimi; 7 günden eskiyse tarih. */
export function formatRelative(utc: string | null | undefined, now: number = Date.now()): string {
  if (!utc) return '—';
  const diff = (new Date(utc).getTime() - now) / 1000;
  const abs = Math.abs(diff);
  if (abs < 60) return 'az önce';
  if (abs < 3600) return relative.format(Math.round(diff / 60), 'minute');
  if (abs < 86_400) return relative.format(Math.round(diff / 3600), 'hour');
  if (abs < 7 * 86_400) return relative.format(Math.round(diff / 86_400), 'day');
  return formatDateTime(utc);
}

/** API bilgisindeki ortam değeri → panel etiketi. */
export const environmentLabel = (env: string | null) => (env == null ? null : env === 'Prod' ? 'Canlı' : 'Test');
