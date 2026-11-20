import { api, type ChannelStockListRow, type ListingStockRule, type StockRuleType } from '../api';
import { formatMoney } from '../format';

/**
 * Tek yerden stok + fiyat düzenleme (ADR-042). Bir ilan için taslak: stok kuralı (ortak havuz / üst limit / sabit) ve
 * ilana özel satış fiyatı. Kaydetmek kanala doğrudan yazmaz: değişiklik outbox → kuyruk → gönderim hattına girer ve
 * kanalın STOK/FIYAT yetenek durumuna (KAPALI / DENEME / ACIK) ve yazma kilidine tabidir.
 */
export type ListingDraft = { ruleType: StockRuleType | ''; ruleValue: string; price: string };

export const draftOf = (r: ChannelStockListRow): ListingDraft => ({
  ruleType: r.stockRuleType ?? '',
  ruleValue: r.stockRuleValue != null ? String(r.stockRuleValue) : '',
  price: r.priceOverride != null ? String(r.priceOverride) : ''
});

const sameDraft = (a: ListingDraft, b: ListingDraft) =>
  a.ruleType === b.ruleType && (a.ruleType === '' || a.ruleValue.trim() === b.ruleValue.trim()) && a.price.trim() === b.price.trim();

export const isDirty = (r: ChannelStockListRow, d: ListingDraft | undefined) => d !== undefined && !sameDraft(draftOf(r), d);

/** Taslak geçerli mi; geçersizse kullanıcıya gösterilecek açıklama. */
export function draftProblem(d: ListingDraft): string | null {
  if (d.ruleType !== '') {
    const v = d.ruleValue.trim();
    if (!/^\d+$/.test(v) || Number(v) > 100000) return 'Stok adedi 0 ile 100000 arasında tam sayı olmalı.';
  }
  const p = d.price.trim().replace(',', '.');
  if (p !== '' && (!/^\d+(\.\d{1,2})?$/.test(p) || Number(p) <= 0)) return 'Fiyat 0\'dan büyük, en fazla 2 ondalıklı olmalı.';
  return null;
}

/** Değişen alanları API'ye yazar (önce stok kuralı, sonra fiyat). */
export async function saveDraft(r: ChannelStockListRow, d: ListingDraft): Promise<void> {
  const before = draftOf(r);
  const ruleChanged = before.ruleType !== d.ruleType || (d.ruleType !== '' && before.ruleValue.trim() !== d.ruleValue.trim());
  if (ruleChanged) {
    await api<ListingStockRule>('PUT', `/api/stock/listings/${r.listingId}/rule`,
      d.ruleType === '' ? { type: null, value: null } : { type: d.ruleType, value: Number(d.ruleValue.trim()) });
  }
  if (before.price.trim() !== d.price.trim()) {
    const p = d.price.trim().replace(',', '.');
    await api('PUT', `/api/prices/listings/${r.listingId}/override`, { price: p === '' ? null : Number(p) });
  }
}

const ruleHelp: Record<StockRuleType | '', string> = {
  '': 'Ortak havuz: defterdeki satılabilir stok kanal payı ile gider.',
  UST_LIMIT: 'Üst limit: kanala en fazla bu kadar gider; stok azsa azı gider.',
  SABIT: 'Sabit: kanala bu adet gider (kanal payı yerine); defterde daha az varsa azı gider.'
};

export function ListingEditor({ row, draft, editable, onChange }: {
  row: ChannelStockListRow; draft: ListingDraft; editable: boolean; onChange: (d: ListingDraft) => void;
}) {
  const dirty = !sameDraft(draftOf(row), draft);
  const problem = draftProblem(draft);
  const effectivePrice = row.priceOverride ?? row.skuSalePrice;
  return (
    <div className={`edit-cell${dirty ? ' dirty' : ''}`} data-testid="listing-editor">
      <div className="edit-line">
        <label className="small muted" htmlFor={`rule-${row.listingId}`}>Stok</label>
        <select
          id={`rule-${row.listingId}`}
          aria-label="Stok kuralı"
          value={draft.ruleType}
          disabled={!editable}
          title={ruleHelp[draft.ruleType]}
          onChange={e => onChange({ ...draft, ruleType: e.target.value as ListingDraft['ruleType'] })}
        >
          <option value="">Ortak havuz</option>
          <option value="UST_LIMIT">Üst limit</option>
          <option value="SABIT">Sabit</option>
        </select>
        {draft.ruleType !== '' && (
          <input
            type="number" min={0} max={100000} step={1} aria-label="Stok adedi" value={draft.ruleValue} disabled={!editable}
            onChange={e => onChange({ ...draft, ruleValue: e.target.value })}
          />
        )}
      </div>
      <div className="small muted">
        Gidecek: <strong data-testid="listing-target">{row.expected}</strong>
        {row.lastSent != null && <> · son gönderilen {row.lastSent}</>}
        {dirty && draft.ruleType !== (row.stockRuleType ?? '') && <> · kaydedince yeniden hesaplanır</>}
      </div>
      <div className="edit-line" style={{ marginTop: 6 }}>
        <label className="small muted" htmlFor={`price-${row.listingId}`}>Fiyat</label>
        <input
          id={`price-${row.listingId}`}
          inputMode="decimal" aria-label="İlan satış fiyatı" style={{ width: 96 }}
          placeholder={row.skuSalePrice != null ? String(row.skuSalePrice) : 'SKU fiyatı yok'}
          value={draft.price} disabled={!editable}
          title="Boş: SKU fiyatı + kanal fiyat kuralı. Dolu: bu ilana özel satış fiyatı (KDV dahil)."
          onChange={e => onChange({ ...draft, price: e.target.value })}
        />
        <span className="small muted">₺</span>
      </div>
      <div className="small muted">
        {row.channelSalePrice != null && <>Kanalda {formatMoney(row.channelSalePrice)}</>}
        {row.lastSentSalePrice != null && <> · son gönderilen {formatMoney(row.lastSentSalePrice)}</>}
        {row.channelSalePrice == null && row.lastSentSalePrice == null && effectivePrice != null && <>Hedef {formatMoney(effectivePrice)}</>}
      </div>
      {problem && <div className="small cell-deviation" role="alert">{problem}</div>}
    </div>
  );
}
