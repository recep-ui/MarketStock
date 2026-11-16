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
