import { useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, get, put, type Channel, type ChannelStockList, type ChannelStockListRow } from '../api';
import { channelNames, formatMoney } from '../format';
import { Card, ErrorBox, useLoad } from './ui';

export type VariantPriceResult = { changed: number; unchanged: number; capabilityState: 'KAPALI' | 'DENEME' | 'ACIK' | string };

/** Yeteneğe göre kaydetmeden önce ve sonra gösterilen açıklama. */
export const variantCapabilityText: Record<string, { before: string; after: (n: number) => string; tone: string }> = {
  KAPALI: {
    before: 'Fiyat gönderimi KAPALI: fiyat kaydedilir ama kanala hiçbir şey gönderilmez.',
    after: n => `${n} varyantın fiyatı kaydedildi. Fiyat gönderimi kapalı olduğu için kanala gönderilmedi.`,
    tone: 'alert-warn'
  },
  DENEME: {
    before: 'Fiyat gönderimi DENEME: her varyantın fiyatı önce Yönetici onayına düşer (Fiyat Kuralları → Onay bekleyenler); onaylanmadan gitmez.',
    after: n => `${n} varyantın fiyatı kaydedildi. Her biri Yönetici onayına düşecek; onaylananlar kanala gönderilir.`,
    tone: 'alert-warn'
  },
  ACIK: {
    before: 'Fiyat gönderimi AÇIK: kaydedilen fiyat kısa süre içinde kanala gönderilir (eşik aşan değişiklikler onaya düşer).',
    after: n => `${n} varyantın fiyatı kaydedildi; fiyat kuyruğundan kanala gönderilecek.`,
    tone: 'alert-ok'
  }
};

/** Türkçe fiyat girişi: "1.299,90" / "1299.90" / "1299,9". Geçersizse null. */
export function parsePrice(text: string): number | null {
  const t = text.trim().replace(/\s|₺/g, '');
  if (!t) return null;
  const normalized = t.includes(',') ? t.replace(/\./g, '').replace(',', '.') : t;
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) return null;
  const value = Number(normalized);
  return value > 0 ? value : null;
}

/**
 * ADR-061: aynı ürünün (Product) bu kanaldaki bütün varyantları (beden/renk) — sayfadan bağımsız, sunucudan ürünle süzülerek gelir.
 * Seçilen varyantlara tek fiyat yazılır; mevcut ilan fiyatı yolu kullanılır (PriceHistory + FiyatDegisti → FIYAT kuyruğu).
 * Kanala gidip gitmeyeceğine FIYAT yeteneği karar verir: KAPALI → gitmez, DENEME → her kayıt onaya düşer.
 */
export function VariantPricePanel({ channel, productId, capabilityState, onClose, onSaved }: {
  channel: Channel; productId: number; capabilityState: string | undefined; onClose: () => void; onSaved: (message: string) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const name = channelNames[channel.code] ?? channel.name;
  const list = useLoad(() => get<ChannelStockList>(`/api/stock-overview/channels/${channel.id}?productId=${productId}&pageSize=500`), [channel.id, productId]);
  const rows = useMemo(() => list.data?.rows.items ?? [], [list.data]);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [priceText, setPriceText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  // Açılınca pasif olmayan bütün varyantlar seçili gelir
  useEffect(() => {
    setSelected(new Set(rows.filter(r => r.status !== 'PASIF').map(r => r.listingId)));
  }, [rows]);
  useEffect(() => { ref.current?.scrollIntoView({ block: 'start', behavior: 'smooth' }); }, []);

  const price = parsePrice(priceText);
  const cap = variantCapabilityText[capabilityState ?? 'KAPALI'] ?? variantCapabilityText.KAPALI;
  const toggle = (id: number) => setSelected(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const allSelected = rows.length > 0 && rows.every(r => selected.has(r.listingId));

  const save = async (clear: boolean) => {
    if (selected.size === 0) return;
    const value = clear ? null : price;
    if (!clear && value == null) {
      setError(new ApiError(400, 'Geçerli bir fiyat girin (ör. 1.299,90).'));
      return;
    }
    const question = clear
      ? `${selected.size} varyantın ${name} için elle girilmiş fiyatı kaldırılsın mı? Fiyat SKU fiyatı + kanal kuralından hesaplanır.\n\n${cap.before}`
      : `${selected.size} varyanta ${name} için ${formatMoney(value!)} fiyatı yazılsın mı?\n\n${cap.before}`;
    if (!window.confirm(question)) return;
    setBusy(true);
    setError(null);
    try {
      const result = await put<VariantPriceResult>(`/api/prices/products/${productId}/channels/${channel.id}/variant-price`,
        { listingIds: [...selected], price: value });
      const text = variantCapabilityText[result.capabilityState] ?? cap;
      onSaved(result.changed === 0
        ? `Değişiklik yok: seçilen ${result.unchanged} varyantın fiyatı zaten bu.`
        : text.after(result.changed) + (result.unchanged > 0 ? ` (${result.unchanged} varyant zaten bu fiyattaydı.)` : ''));
      list.reload();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const first = rows[0];
  return (
    <div ref={ref} data-testid="variant-price-panel">
      <Card title={`Varyantlara toplu fiyat · ${first?.productName ?? 'ürün'} (${name})`}
        actions={<button className="link" onClick={onClose}>Kapat ✕</button>}>
        <div className={`alert ${cap.tone}`} data-testid="variant-capability">{cap.before}</div>
        <ErrorBox error={list.error ?? error} onRetry={list.reload} />
        {list.loading && !list.data && <div className="loading">Yükleniyor…</div>}
        {list.data && (
          <>
            <div className="table-wrap">
              <table className="table" data-testid="variant-table">
                <thead>
                  <tr>
                    <th data-export="skip">
                      <input type="checkbox" aria-label="Tüm varyantları seç" checked={allSelected}
                        onChange={e => setSelected(e.target.checked ? new Set(rows.map(r => r.listingId)) : new Set())} />
                    </th>
                    <th>SKU</th><th>Renk / beden</th><th>Kanal barkodu</th><th className="num">Şu anki fiyat</th>
                    <th className="num">Son gönderilen</th><th className="num">Kanalda görünen</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r: ChannelStockListRow) => {
                    const current = r.priceOverride ?? r.skuSalePrice;
                    return (
                      <tr key={r.listingId} data-testid="variant-row" className={selected.has(r.listingId) ? 'selected' : undefined}>
                        <td><input type="checkbox" aria-label={`${r.skuCode} seç`} checked={selected.has(r.listingId)} onChange={() => toggle(r.listingId)} /></td>
                        <td>{r.skuCode}{r.status === 'PASIF' && <span className="badge">Pasif</span>}</td>
                        <td>{[r.color, r.size].filter(Boolean).join(' / ') || r.channel.variant || '—'}</td>
                        <td>{r.channel.barcode ?? '—'}</td>
                        <td className="num">
                          {current != null ? formatMoney(current) : <span className="muted">tanımlı değil</span>}
                          {r.priceOverride != null && <span className="badge" title="Bu kanal için elle girildi">elle</span>}
                        </td>
                        <td className="num">{r.lastSentSalePrice != null ? formatMoney(r.lastSentSalePrice) : '—'}</td>
                        <td className="num">{r.channelSalePrice != null ? formatMoney(r.channelSalePrice) : '—'}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <form className="filters" onSubmit={e => { e.preventDefault(); save(false); }}>
              <label className="field">
                <span>Seçilen {selected.size} varyantın {name} satış fiyatı (₺)</span>
                <input inputMode="decimal" placeholder="ör. 1.299,90" value={priceText} onChange={e => setPriceText(e.target.value)}
                  aria-label="Varyant fiyatı" className={priceText && price == null ? 'price-input invalid' : 'price-input'} data-testid="variant-price-input" />
              </label>
              <button className="primary" type="submit" disabled={busy || selected.size === 0 || price == null} data-testid="variant-price-save">
                {busy ? 'Kaydediliyor…' : `Seçilen ${selected.size} varyanta uygula`}
              </button>
              <button type="button" disabled={busy || selected.size === 0} onClick={() => save(true)}
                title="Elle girilmiş fiyatı kaldır; fiyat SKU fiyatı + kanal kuralıyla hesaplanır">
                Elle fiyatı kaldır
              </button>
            </form>
            <p className="muted small">
              Fiyat yalnızca {name} ilanlarına yazılır (ilan fiyatı); SKU'nun temel fiyatı ve diğer kanallar değişmez. Her değişiklik fiyat geçmişine yazılır.
            </p>
          </>
        )}
      </Card>
    </div>
  );
}
