// Birden çok ekranda kullanılan Excel sütunları (ADR-060).
import type { ChannelStockListRow, Listing, OrderListItem, UnmatchedItem } from './api';
import type { ExportColumn } from './export';
import { channelNames, statusLabels } from './format';

const channel = (code: string) => channelNames[code] ?? code;
const variant = (color: string | null | undefined, size: string | null | undefined) => [color, size].filter(Boolean).join(' / ') || null;

export const orderExportColumns: ExportColumn<OrderListItem>[] = [
  { header: 'Tarih', kind: 'datetime', value: o => o.orderDateUtc },
  { header: 'Kanal', value: o => channel(o.channelCode) },
  { header: 'Sipariş no', value: o => o.channelOrderNo },
  { header: 'Durum', value: o => statusLabels[o.status] ?? o.status },
  { header: 'Tutar', kind: 'money', value: o => o.total },
  { header: 'Satır', kind: 'number', value: o => o.lineCount },
  { header: 'Eşleşmeyen satır', kind: 'number', value: o => o.unmatchedLineCount },
  { header: 'Stok yetersiz', kind: 'bool', value: o => o.insufficientStock }
];

export const unmatchedExportColumns: ExportColumn<UnmatchedItem>[] = [
  { header: 'Kanal', value: u => channel(u.channelCode) },
  { header: 'Kanal barkodu', value: u => u.channelBarcode },
  { header: 'Kanal ürün kodu', value: u => u.channelProductCode },
  { header: 'Kanal stok kodu', value: u => u.channelStockCode },
  { header: 'Model kodu', value: u => u.modelCode },
  { header: 'Ürün', value: u => u.productName },
  { header: 'Marka', value: u => u.brand },
  { header: 'Renk', value: u => u.color },
  { header: 'Beden', value: u => u.size },
  { header: 'Kanal durumu', value: u => u.channelStatus },
  { header: 'Neden', value: u => u.reasonDetail ?? u.reason },
  { header: 'Önerilen SKU', value: u => u.suggestedSkuCode },
  { header: 'Durum', value: u => u.status },
  { header: 'İlk görülme', kind: 'datetime', value: u => u.firstSeenUtc },
  { header: 'Son görülme', kind: 'datetime', value: u => u.lastSeenUtc }
];

export const listingExportColumns: ExportColumn<Listing>[] = [
  { header: 'Kanal', value: l => channel(l.channelCode) },
  { header: 'Kanaldaki ad', value: l => l.channelName },
  { header: 'Kanal barkodu', value: l => l.channelBarcode },
  { header: 'Kanal ürün kodu', value: l => l.channelProductCode },
  { header: 'Kanaldaki varyant', value: l => l.variant },
  { header: 'Kanal durumu', value: l => l.channelStatus },
  { header: 'SKU', value: l => l.skuCode },
  { header: 'Ürün', value: l => l.productName },
  { header: 'SKU renk / beden', value: l => variant(l.skuColor, l.skuSize) },
  { header: 'SKU barkodu', value: l => l.skuBarcode },
  { header: 'Eşleşme', value: l => l.matchBasis },
  { header: 'Uyarılar', value: l => (l.warnings ?? []).map(w => w.message).join(' | ') || null },
  { header: 'Doğru eşleşme onayı', kind: 'bool', value: l => !!l.approval?.valid },
  { header: 'Durum', value: l => l.status },
  { header: 'Son gönderilen stok', kind: 'number', value: l => l.lastSentStock },
  { header: 'Son senkron', kind: 'datetime', value: l => l.lastSyncUtc },
  { header: 'Kanal sayfası', value: l => l.productUrl }
];

export const channelStockExportColumns: ExportColumn<ChannelStockListRow>[] = [
  { header: 'Kanaldaki ad', value: r => r.channel.name },
  { header: 'Kanal barkodu', value: r => r.channel.barcode },
  { header: 'Kanal ürün kodu', value: r => r.channel.productCode },
  { header: 'Kanal stok kodu', value: r => r.channel.stockCode },
  { header: 'Kanaldaki varyant', value: r => r.channel.variant ?? variant(r.channel.color, r.channel.size) },
  { header: 'Kanal durumu', value: r => r.channelStatus },
  { header: 'Kanal stoğu', kind: 'number', value: r => r.channelStock ?? (r.missingInChannel ? 'kanalda yok' : 'çekilmedi') },
  { header: 'Son çekim', kind: 'datetime', value: r => r.fetchedAtUtc },
  { header: 'SKU', value: r => r.skuCode },
  { header: 'Ürün', value: r => r.productName },
  { header: 'SKU renk / beden', value: r => variant(r.color, r.size) },
  { header: 'SKU barkodu', value: r => r.skuBarcode },
  { header: 'Gitmesi gereken', kind: 'number', value: r => r.expected },
  { header: 'Son gönderilen', kind: 'number', value: r => r.lastSent },
  { header: 'Sapma', kind: 'bool', value: r => r.hasDeviation },
  { header: 'Eşleşme', value: r => r.matchBasis },
  { header: 'Uyarılar', value: r => r.warnings.map(w => w.message).join(' | ') || null },
  { header: 'Doğru eşleşme onayı', kind: 'bool', value: r => !!r.approval?.valid },
  { header: 'Kanal fiyatı', kind: 'money', value: r => r.channelSalePrice },
  { header: 'İlan fiyatı (elle)', kind: 'money', value: r => r.priceOverride },
  { header: 'SKU satış fiyatı', kind: 'money', value: r => r.skuSalePrice },
  { header: 'Son gönderilen fiyat', kind: 'money', value: r => r.lastSentSalePrice },
  { header: 'İlan durumu', value: r => r.status },
  { header: 'Kanal sayfası', value: r => r.productUrl }
];
