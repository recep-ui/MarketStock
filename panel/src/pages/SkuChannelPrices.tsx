import { useMemo, useState, type FormEvent } from 'react';
import { ApiError, get, post, put, qs, type Channel, type ChannelPriceInfo, type Paged, type SkuChannelPrice, type SkuPriceOverview } from '../api';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ChannelLink, ErrorBox, Thumb, useLoad } from '../components/ui';
import { DataTable, Pager } from '../components/DataTable';
import type { ColumnDef } from '@tanstack/react-table';
import { channelNames, formatDateTime, formatMoney } from '../format';

/*
  Ürün bazında fiyat ve stok (ADR-049, ADR-050). Aynı SKU'nun her pazaryerindeki fiyatı, durumu ve stoğu yan yana görünür;
  her kanal için AYRI elle fiyat ve ürünün defterdeki satılabilir stoğu tek satırdan düzenlenir. Kaydetmeden önce
  "eski → yeni" özeti gösterilir. Kayıt kanala doğrudan gitmez: fiyatta kanalın Fiyat gönderimi ve eşik onayı, stokta
  kanalın Stok gönderimi (Onaylı ise Senkron Merkezi'nde onay) geçerlidir.
*/

const money = (v: number | null | undefined) => (v == null ? '—' : formatMoney(v));
const statusLabels: Record<string, string> = {
  DEGISECEK: 'gönderilecek', AYNI: 'kanalla aynı', ONAY_GEREKIR: 'onay gerekir', GONDERILEMEZ: 'gönderilemez', REDDEDILDI: 'reddedildi'
};
const statusHints: Record<string, string> = {
  DEGISECEK: 'Fiyat gönderimi açıksa bu fiyat kanala gönderilir.',
  AYNI: 'Kanala son gönderilen fiyatla aynı; tekrar gönderilmez.',
  ONAY_GEREKIR: 'Değişim eşiği aştı veya kanal "Onaylı": Fiyat → Onay bekleyen fiyatlar\'dan onaylanmalı.',
  GONDERILEMEZ: 'Kanala gönderilemez (açıklamaya bakın).',
  REDDEDILDI: 'Bu fiyat daha önce reddedildi; değişmedikçe gönderilmez.'
};
const capabilityLabels: Record<string, string> = { KAPALI: 'gönderim kapalı', DENEME: 'onaylı gönderim', ACIK: 'otomatik gönderim' };

type Drafts = Record<number, string>; // listingId → girilen metin ('' = elle fiyatı kaldır)
type StockDrafts = Record<number, string>; // skuId → hedef satılabilir stok

/** Satılabilir stok: 0 veya pozitif tam sayı. */
function parseStock(text: string): number | 'hata' {
  const t = text.trim();
  if (!/^\d{1,7}$/.test(t)) return 'hata';
  return Number(t);
}

type StockResult = {
  applied: number; unchanged: number; rejected: number; pushQueued: number;
  rows: { skuCode: string | null; outcome: string; message: string | null }[];
};

function StockCell({ sku, draft, editable, onDraft }: {
  sku: SkuPriceOverview; draft: string | undefined; editable: boolean; onDraft: (skuId: number, value: string | undefined) => void;
}) {
  const current = String(sku.available);
  const dirty = draft !== undefined && draft !== current;
  const parsed = draft === undefined ? undefined : parseStock(draft);
  const target = typeof parsed === 'number' ? parsed : null;
  return (
    <div className={`price-cell${dirty ? ' dirty' : ''}`} data-testid="stock-cell">
      <div title="Satılabilir = Fiziksel − Rezerve − Güvenlik stoğu (defter, tüm depolar)">
        <span className="muted small">Satılabilir: </span><strong>{sku.hasBalance ? sku.available : '—'}</strong>
      </div>
      <div className="muted small">Fiziksel {sku.physical} · Rezerve {sku.reserved} · Güvenlik {sku.safetyStock}</div>
      {editable && (
        <input
          className={`price-input${parsed === 'hata' ? ' invalid' : ''}`}
          value={draft ?? current}
          inputMode="numeric"
          aria-label={`Satılabilir stok ${sku.skuCode}`}
          title="Ürünün satılabilir stoğu. Kaydedince defterde sayım olarak düzeltilir ve eşleşen tüm kanallara gider."
          onChange={e => onDraft(sku.skuId, e.target.value === current ? undefined : e.target.value)}
        />
      )}
      {dirty && target != null && <div className="muted small">Yeni fiziksel: {target + sku.reserved + sku.safetyStock}</div>}
    </div>
  );
}

function parsePrice(text: string): number | null | 'hata' {
  const t = text.trim();
  if (t === '') return null;
  // "1.299,90" (binlik nokta + virgül) veya "1299.90" / "1299,90" kabul edilir
  const normalized = t.includes(',') ? t.replace(/\./g, '').replace(',', '.') : t;
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) return 'hata';
  const n = Number(normalized);
  return n > 0 ? n : 'hata';
}

/** Kanal stoğu kutusunun mevcut değeri: SABIT kuralın adedi, yoksa '' (ortak stok). */
const ruleText = (c: SkuChannelPrice) => (c.stockRuleType === 'SABIT' && c.stockRuleValue != null ? String(c.stockRuleValue) : '');

function ChannelCell({ c, draft, ruleDraft, editable, onDraft, onRuleDraft }: {
  c: SkuChannelPrice | undefined; draft: string | undefined; ruleDraft: string | undefined; editable: boolean;
  onDraft: (listingId: number, value: string | undefined) => void; onRuleDraft: (listingId: number, value: string | undefined) => void;
}) {
  if (!c) return <span className="muted small" title="Bu SKU bu kanalda eşleştirilmemiş">eşleşme yok</span>;
  const p = c.preview;
  const current = c.override == null ? '' : String(c.override).replace('.', ',');
  const dirty = draft !== undefined && draft !== current;
  const parsed = draft === undefined ? undefined : parsePrice(draft);
  const currentRule = ruleText(c);
  const ruleDirty = ruleDraft !== undefined && ruleDraft !== currentRule;
  const ruleInvalid = ruleDraft !== undefined && ruleDraft.trim() !== '' && parseStock(ruleDraft) === 'hata';
  const stockDiffers = c.channelStock != null && c.stockTarget != null && c.channelStock !== c.stockTarget;
  const stockTitle = `Kanalda: son mutabakatta pazaryerinde görülen stok${c.channelStockAtUtc ? ` (${formatDateTime(c.channelStockAtUtc)})` : ''}.`
    + ` Gidecek: bu kanala gönderilecek stok. Son gönderilen: ${c.lastSentStock ?? '—'}.`
    + ` Stok gönderimi: ${capabilityLabels[c.stockCapabilityState ?? ''] ?? c.stockCapabilityState ?? '—'}.`;
  const ruleLabel = c.stockRuleType === 'SABIT' ? `kanala özel ${c.stockRuleValue}`
    : c.stockRuleType === 'UST_LIMIT' ? `en fazla ${c.stockRuleValue}` : 'ortak stok';
  return (
    <div className={`price-cell${dirty || ruleDirty ? ' dirty' : ''}${c.listingStatus !== 'AKTIF' ? ' muted' : ''}`} data-testid={`price-cell-${c.channelCode}`}>
      {c.channelStatus && <div className="small" title="Pazaryerinin bildirdiği ürün durumu (son katalog çekimi)" data-testid="channel-status">{c.channelStatus}</div>}
      <div className="cell-block">
        <div className={stockDiffers ? 'warn-text' : undefined} title={stockTitle} data-testid="channel-stock">
          <span className="muted small">Stok: </span>kanalda {c.channelStock ?? '—'} · gidecek <strong>{c.stockTarget ?? '—'}</strong>
        </div>
        <div className="muted small" data-testid="stock-rule"
          title="Ortak stok: defterdeki satılabilir stok gider. Kanala özel: yalnızca bu kanala bu adet gider (defterdeki stoktan fazla olamaz).">
          {ruleLabel} · {capabilityLabels[c.stockCapabilityState ?? ''] ?? '—'}
        </div>
        {editable && (
          <input
            className={`price-input${ruleInvalid ? ' invalid' : ''}`}
            value={ruleDraft ?? currentRule}
            placeholder="ortak"
            inputMode="numeric"
            aria-label={`${channelNames[c.channelCode] ?? c.channelCode} kanal stoğu ${p.skuCode}`}
            title="Yalnızca bu kanala gidecek sabit adet. Boş bırakılırsa ortak stok (defter) gider. Defterdeki satılabilir stoktan fazlası gönderilmez."
            onChange={e => onRuleDraft(c.listingId, e.target.value === currentRule ? undefined : e.target.value)}
          />
        )}
      </div>
      <div className="cell-block">
        <div title="Pazaryerinde şu an görünen fiyat (son katalog/stok çekiminde okunan)">
          <span className="muted small">Fiyat: </span>kanalda {money(p.channelSalePrice)}
          {p.productUrl && <> <ChannelLink code={c.channelCode} url={p.productUrl} /></>}
        </div>
        <div title={p.fromOverride ? 'Elle girilen fiyat (kanal kuralının yerine geçer)' : 'SKU fiyatı × kanal kuralı ile hesaplanan fiyat'}>
          <span className="muted small">Gidecek: </span><strong>{money(p.newSale)}</strong>
          {p.fromOverride && <span className="badge" title="Bu kanal için elle girildi">elle</span>}
          {p.floorApplied && <span className="badge warn" title="Kanal kuralındaki taban fiyat uygulandı">taban</span>}
        </div>
        <div className="muted small" title={statusHints[p.status]}>
          {statusLabels[p.status] ?? p.status}
          {p.changePercent != null && ` · %${p.changePercent}`}
          {' · '}{capabilityLabels[c.capabilityState] ?? c.capabilityState}
          {c.listingStatus !== 'AKTIF' && ' · ilan pasif'}
        </div>
        {p.lastSentSale != null && (
          <div className="muted small" title={c.lastPriceSyncUtc ? `Gönderim: ${formatDateTime(c.lastPriceSyncUtc)}` : undefined}>Son gönderilen: {money(p.lastSentSale)}</div>
        )}
        {p.problem && <div className="warn-text small wrap">{p.problem}</div>}
        {editable && (
          <input
            className={`price-input${parsed === 'hata' ? ' invalid' : ''}`}
            value={draft ?? current}
            placeholder="kural"
            inputMode="decimal"
            aria-label={`${channelNames[c.channelCode] ?? c.channelCode} elle fiyat ${p.skuCode}`}
            title="Bu kanal için elle satış fiyatı. Boş bırakılırsa kanal kuralı uygulanır."
            onChange={e => onDraft(c.listingId, e.target.value === current ? undefined : e.target.value)}
          />
        )}
      </div>
    </div>
  );
}

export function SkuChannelPrices({ channels, refreshKey, onChanged }: { channels: Channel[]; refreshKey: number; onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const editable = can('admin');
  const [input, setInput] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [drafts, setDrafts] = useState<Drafts>({});
  const [stockDrafts, setStockDrafts] = useState<StockDrafts>({});
  const [ruleDrafts, setRuleDrafts] = useState<Drafts>({}); // listingId → kanala özel stok ('' = ortak)
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const data = useLoad(() => get<Paged<SkuPriceOverview>>(`/api/prices/skus${qs({ search, page, pageSize: 25 })}`), [search, page, refreshKey]);
  const infos = useLoad(() => get<ChannelPriceInfo[]>('/api/prices/channels'), [refreshKey]);

  // Sütunlar: eşleşmesi olan kanallar (sıra kanal listesindeki gibi)
  const columnsChannels = useMemo(() => channels.filter(ch => (data.data?.items ?? []).some(i => i.channels.some(c => c.channelId === ch.id))), [channels, data.data]);
  const byListing = useMemo(() => {
    const map = new Map<number, { sku: SkuPriceOverview; c: SkuChannelPrice }>();
    for (const sku of data.data?.items ?? []) for (const c of sku.channels) map.set(c.listingId, { sku, c });
    return map;
  }, [data.data]);
  const pending = Object.entries(drafts).map(([id, text]) => ({ listingId: Number(id), text, ...byListing.get(Number(id))! })).filter(x => x.c);
  const skuById = useMemo(() => new Map((data.data?.items ?? []).map(s => [s.skuId, s])), [data.data]);
  const pendingStock = Object.entries(stockDrafts).map(([id, text]) => ({ sku: skuById.get(Number(id))!, text })).filter(x => x.sku);
  const invalid: unknown[] = [...pending.filter(x => parsePrice(x.text) === 'hata'), ...pendingStock.filter(x => parseStock(x.text) === 'hata')];
  const pendingRules = Object.entries(ruleDrafts).map(([id, text]) => ({ listingId: Number(id), text, ...byListing.get(Number(id))! })).filter(x => x.c);
  invalid.push(...pendingRules.filter(x => x.text.trim() !== '' && parseStock(x.text) === 'hata'));
  const changeCount = pending.length + pendingStock.length + pendingRules.length;

  function setDraft(listingId: number, value: string | undefined) {
    setDrafts(prev => {
      const next = { ...prev };
      if (value === undefined) delete next[listingId]; else next[listingId] = value;
      return next;
    });
  }

  function setStockDraft(skuId: number, value: string | undefined) {
    setStockDrafts(prev => {
      const next = { ...prev };
      if (value === undefined) delete next[skuId]; else next[skuId] = value;
      return next;
    });
  }

  function setRuleDraft(listingId: number, value: string | undefined) {
    setRuleDrafts(prev => {
      const next = { ...prev };
      if (value === undefined) delete next[listingId]; else next[listingId] = value;
      return next;
    });
  }

  function clearDrafts() {
    setDrafts({});
    setStockDrafts({});
    setRuleDrafts({});
  }

  function submitSearch(e: FormEvent) {
    e.preventDefault();
    if (changeCount > 0 && !window.confirm('Kaydedilmemiş değişiklikler var; arama yapılırsa kaybolacak. Devam edilsin mi?')) return;
    clearDrafts();
    setPage(1);
    setSearch(input.trim());
  }

  async function save() {
    if (changeCount === 0 || invalid.length > 0) return;
    const stockLines = pendingStock.map(x => {
      const t = parseStock(x.text) as number;
      const targets = [...new Set(x.sku.channels.filter(c => c.listingStatus === 'AKTIF').map(c => channelNames[c.channelCode] ?? c.channelCode))];
      return `• ${x.sku.skuCode}: satılabilir ${x.sku.available} → ${t} (fiziksel ${x.sku.physical} → ${t + x.sku.reserved + x.sku.safetyStock})`
        + (targets.length ? ` · kanallar: ${targets.join(', ')}` : '');
    });
    const ruleLines = pendingRules.map(x => {
      const name = channelNames[x.c.channelCode] ?? x.c.channelCode;
      const old = x.c.stockRuleType === 'SABIT' ? `kanala özel ${x.c.stockRuleValue}` : x.c.stockRuleType === 'UST_LIMIT' ? `en fazla ${x.c.stockRuleValue}` : 'ortak stok';
      const t = x.text.trim();
      const n = Number(t);
      const next = t === '' ? `ortak stok (defter: ${x.sku.available})`
        : `kanala özel ${n}${n > x.sku.available ? ` (defterde ${x.sku.available} var; en fazla ${x.sku.available} gider)` : ''}`;
      return `• ${x.sku.skuCode} · ${name}: ${old} → ${next}`;
    });
    const priceLines = pending.map(x => {
      const price = parsePrice(x.text) as number | null;
      const old = x.c.override != null ? `elle ${money(x.c.override)}` : `kural ${money(x.c.preview.newSale)}`;
      const next = price == null ? 'kural (elle fiyat kaldırılır)' : `elle ${money(price)}`;
      return `• ${x.sku.skuCode} · ${channelNames[x.c.channelCode] ?? x.c.channelCode}: ${old} → ${next}`;
    });
    const closed = [...new Set(pending.filter(x => x.c.capabilityState === 'KAPALI').map(x => channelNames[x.c.channelCode] ?? x.c.channelCode))];
    const list = (lines: string[]) => `${lines.slice(0, 20).join('\n')}${lines.length > 20 ? `\n… (+${lines.length - 20})` : ''}`;
    let text = '';
    if (stockLines.length) {
      text += `STOK: ${stockLines.length} ürün (defterde sayım düzeltmesi)\n${list(stockLines)}\n`
        + 'Yeni stok eşleşen kanallara, kanalın Stok gönderimi ayarına göre gider ("Onaylı" kanallarda Senkron Merkezi\'nde onayınızı bekler).\n\n';
    }
    if (ruleLines.length) {
      text += `KANAL STOĞU: ${ruleLines.length} ilan (defter değişmez)\n${list(ruleLines)}\n`
        + 'Yalnızca o kanala gidecek stok değişir; kanalın Stok gönderimi ayarına göre gider.\n\n';
    }
    if (priceLines.length) {
      text += `FİYAT: ${priceLines.length} ilan\n${list(priceLines)}\n`
        + 'Fiyatlar kanala, kanalın Fiyat gönderimi ayarına göre gider (eşiği aşan değişiklik onay ister).'
        + (closed.length ? `\nFiyat gönderimi KAPALI olan kanallar: ${closed.join(', ')}; kaydedilir ama gönderilmez.` : '') + '\n\n';
    }
    if (!window.confirm(text + 'Devam edilsin mi?')) return;
    setBusy(true);
    setError(null);
    const messages: string[] = [];
    try {
      if (pending.length) {
        const r = await put<{ changed: number; unchanged: number }>('/api/prices/listings/overrides',
          { changes: pending.map(x => ({ listingId: x.listingId, price: parsePrice(x.text) })) });
        setDrafts({});
        messages.push(`${r.changed} ilan fiyatı kaydedildi.`);
      }
      if (pendingRules.length) {
        for (const x of pendingRules) {
          const t = x.text.trim();
          await put(`/api/stock/listings/${x.listingId}/rule`, t === '' ? { type: null, value: null } : { type: 'SABIT', value: Number(t) });
          setRuleDraft(x.listingId, undefined);
        }
        messages.push(`${pendingRules.length} ilanın kanal stoğu kaydedildi.`);
      }
      if (pendingStock.length) {
        const r = await post<StockResult>('/api/stock/available', {
          readAtUtc: data.data?.items[0]?.readAtUtc ?? null,
          rows: pendingStock.map(x => ({ skuId: x.sku.skuId, targetAvailable: parseStock(x.text), expectedPhysical: x.sku.physical }))
        });
        setStockDrafts({});
        messages.push(`${r.applied} ürünün stoğu yazıldı${r.unchanged ? `, ${r.unchanged} üründe fark yok` : ''}`
          + `${r.pushQueued ? `; ${r.pushQueued} ilan gönderim kuyruğuna alındı` : ''}.`);
        if (r.rejected) {
          messages.push(`${r.rejected} ürün reddedildi: `
            + r.rows.filter(x => x.outcome === 'REDDEDILDI').map(x => `${x.skuCode ?? ''} (${x.message ?? ''})`).join('; '));
        }
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
      if (messages.length) {
        onChanged(messages.join(' '));
        data.reload();
      }
    }
  }

  const columns = useMemo<ColumnDef<SkuPriceOverview>[]>(() => [
    {
      header: 'Ürün',
      cell: ({ row }) => (
        <div className="with-thumb" style={{ whiteSpace: 'normal', minWidth: 200, maxWidth: 280 }}>
          <Thumb url={row.original.imageUrl} alt={row.original.productName} size={44} />
          <div>
            <div>{row.original.productName}</div>
            <div className="muted small">{row.original.skuCode}{row.original.barcode ? ` · ${row.original.barcode}` : ''}</div>
            {(row.original.color || row.original.size) && (
              <div className="muted small">
                {[row.original.color && `Renk: ${row.original.color}`, row.original.size && `Beden: ${row.original.size}`].filter(Boolean).join(' · ')}
              </div>
            )}
          </div>
        </div>
      )
    },
    {
      header: 'Stok (defter)',
      cell: ({ row }) => <StockCell sku={row.original} draft={stockDrafts[row.original.skuId]} editable={editable} onDraft={setStockDraft} />
    },
    {
      header: 'SKU fiyatı',
      cell: ({ row }) => (
        <div title="Ana fiyat (KDV dahil). Kanal kuralı bu fiyattan hesaplanır; aşağıdaki SKU fiyatı formundan değişir.">
          <strong>{money(row.original.salePrice)}</strong>
          {row.original.listPrice != null && row.original.listPrice !== row.original.salePrice && <div className="muted small">liste {money(row.original.listPrice)}</div>}
        </div>
      )
    },
    ...columnsChannels.map<ColumnDef<SkuPriceOverview>>(ch => ({
      id: `ch-${ch.id}`,
      header: () => <ChannelBadge code={ch.code} />,
      cell: ({ row }) => {
        const list = row.original.channels.filter(c => c.channelId === ch.id);
        if (list.length === 0) return <ChannelCell c={undefined} draft={undefined} ruleDraft={undefined} editable={false} onDraft={setDraft} onRuleDraft={setRuleDraft} />;
        return <>{list.map(c => <ChannelCell key={c.listingId} c={c} draft={drafts[c.listingId]} ruleDraft={ruleDrafts[c.listingId]} editable={editable}
          onDraft={setDraft} onRuleDraft={setRuleDraft} />)}</>;
      }
    }))
  ], [columnsChannels, drafts, stockDrafts, ruleDrafts, editable]);

  return (
    <Card title="Ürün bazında fiyat ve stok">
      <div className="notes small" data-testid="price-notes">
        <p><strong>Nasıl çalışır?</strong></p>
        <ul>
          <li><strong>Ortak stok</strong> (Stok sütunu): ürünün depodaki satılabilir stoğu. Değiştirirseniz bütün kanallar bu sayıyı alır.</li>
          <li><strong>Kanal stoğu</strong> (kanal sütunundaki üst kutu): yalnızca o pazaryerine gidecek adet. Boş = ortak stok. Depodaki stoktan fazlası gönderilmez.</li>
          <li><strong>Kanal fiyatı</strong> (kanal sütunundaki alt kutu): yalnızca o pazaryerinin fiyatı. Boş = Fiyat sekmesindeki kanal kuralı.</li>
          <li><strong>kanalda</strong> = pazaryerinde şu an görünen · <strong>gidecek</strong> = gönderilecek değer; stokta ikisi farklıysa turuncu yazılır.</li>
          <li>Kaydetmeden önce tüm değişikliklerin özeti gösterilir. Kayıt hemen göndermez: "Onaylı" kanallarda stok Senkron Merkezi'nde, fiyat
            Fiyat sekmesindeki "Onay bekleyen fiyatlar"da onayınızı bekler; "kapalı" kanala gitmez.</li>
        </ul>
        <p>
          {(infos.data ?? []).filter(i => i.vatIncluded !== true).length > 0 && (
            <> KDV dahil olduğu doğrulanamayan kanallar: {(infos.data ?? []).filter(i => i.vatIncluded !== true).map(i => channelNames[i.channelCode] ?? i.channelCode).join(', ')}.</>
          )}</p>
      </div>
      <form className="filters" onSubmit={submitSearch}>
        <input placeholder="SKU, ürün adı veya barkod" value={input} onChange={e => setInput(e.target.value)} aria-label="Fiyat ürün ara" />
        <button type="submit">Ara</button>
        {editable && (
          <>
            <button type="button" className="primary" disabled={busy || changeCount === 0 || invalid.length > 0} onClick={save} data-testid="save-prices">
              Değişiklikleri kaydet ({changeCount})
            </button>
            <button type="button" disabled={changeCount === 0} onClick={clearDrafts}>Vazgeç</button>
          </>
        )}
      </form>
      {invalid.length > 0 && (
        <div className="alert alert-warn">{invalid.length} kutuda geçersiz değer var (fiyat 0'dan büyük, en fazla 2 ondalık, ör. 499,90; stok 0 veya pozitif tam sayı).</div>
      )}
      <ErrorBox error={error ?? data.error} onRetry={data.reload} />
      {error instanceof ApiError && error.fieldErrors && <div className="muted small">{Object.values(error.fieldErrors).flat().join(' ')}</div>}
      <DataTable columns={columns} data={data.data?.items ?? []} loading={data.loading} rowTestId="sku-price-row" />
      {data.data && <Pager page={page} pageSize={data.data.pageSize} total={data.data.total} onPage={p => {
        if (changeCount > 0 && !window.confirm('Kaydedilmemiş değişiklikler var; sayfa değişirse kaybolacak. Devam edilsin mi?')) return;
        clearDrafts();
        setPage(p);
      }} />}
    </Card>
  );
}
