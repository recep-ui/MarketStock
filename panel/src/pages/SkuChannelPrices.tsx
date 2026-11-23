import { useMemo, useState, type FormEvent } from 'react';
import { ApiError, get, put, qs, type Channel, type ChannelPriceInfo, type Paged, type SkuChannelPrice, type SkuPriceOverview } from '../api';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ChannelLink, ErrorBox, Thumb, useLoad } from '../components/ui';
import { DataTable, Pager } from '../components/DataTable';
import type { ColumnDef } from '@tanstack/react-table';
import { channelNames, formatDateTime, formatMoney } from '../format';

/*
  Ürün bazında kanal fiyatları (ADR-049). Aynı SKU'nun her pazaryerindeki fiyatı yan yana görünür ve her kanal için AYRI
  elle fiyat girilebilir. Kaydetmeden önce SKU/kanal bazında "eski → yeni" özeti gösterilir. Kayıt kanala doğrudan gitmez:
  kanalın Fiyat gönderimi (Kapalı/Onaylı/Açık) ve eşik onayı kuralları geçerlidir.
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

function parsePrice(text: string): number | null | 'hata' {
  const t = text.trim();
  if (t === '') return null;
  // "1.299,90" (binlik nokta + virgül) veya "1299.90" / "1299,90" kabul edilir
  const normalized = t.includes(',') ? t.replace(/\./g, '').replace(',', '.') : t;
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) return 'hata';
  const n = Number(normalized);
  return n > 0 ? n : 'hata';
}

function ChannelCell({ c, draft, editable, onDraft }: {
  c: SkuChannelPrice | undefined; draft: string | undefined; editable: boolean; onDraft: (listingId: number, value: string | undefined) => void;
}) {
  if (!c) return <span className="muted small" title="Bu SKU bu kanalda eşleştirilmemiş">eşleşme yok</span>;
  const p = c.preview;
  const current = c.override == null ? '' : String(c.override).replace('.', ',');
  const dirty = draft !== undefined && draft !== current;
  const parsed = draft === undefined ? undefined : parsePrice(draft);
  return (
    <div className={`price-cell${dirty ? ' dirty' : ''}${c.listingStatus !== 'AKTIF' ? ' muted' : ''}`} data-testid={`price-cell-${c.channelCode}`}>
      <div title="Pazaryerinde şu an görünen fiyat (son katalog/stok çekiminde okunan)">
        <span className="muted small">Kanalda: </span>{money(p.channelSalePrice)}
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
  );
}

export function SkuChannelPrices({ channels, refreshKey, onChanged }: { channels: Channel[]; refreshKey: number; onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const editable = can('admin');
  const [input, setInput] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [drafts, setDrafts] = useState<Drafts>({});
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
  const invalid = pending.filter(x => parsePrice(x.text) === 'hata');

  function setDraft(listingId: number, value: string | undefined) {
    setDrafts(prev => {
      const next = { ...prev };
      if (value === undefined) delete next[listingId]; else next[listingId] = value;
      return next;
    });
  }

  function submitSearch(e: FormEvent) {
    e.preventDefault();
    if (pending.length > 0 && !window.confirm('Kaydedilmemiş fiyat değişiklikleri var; arama yapılırsa kaybolacak. Devam edilsin mi?')) return;
    setDrafts({});
    setPage(1);
    setSearch(input.trim());
  }

  async function save() {
    if (pending.length === 0 || invalid.length > 0) return;
    const lines = pending.map(x => {
      const price = parsePrice(x.text) as number | null;
      const old = x.c.override != null ? `elle ${money(x.c.override)}` : `kural ${money(x.c.preview.newSale)}`;
      const next = price == null ? 'kural (elle fiyat kaldırılır)' : `elle ${money(price)}`;
      return `• ${x.sku.skuCode} · ${channelNames[x.c.channelCode] ?? x.c.channelCode}: ${old} → ${next}`;
    });
    const closed = [...new Set(pending.filter(x => x.c.capabilityState === 'KAPALI').map(x => channelNames[x.c.channelCode] ?? x.c.channelCode))];
    if (!window.confirm(`${pending.length} ilan fiyatı kaydedilecek:\n\n${lines.slice(0, 25).join('\n')}${lines.length > 25 ? '\n…' : ''}\n\n`
      + 'Fiyatlar kanala, kanalın Fiyat gönderimi ayarına göre gider (eşiği aşan değişiklik onay ister).'
      + (closed.length ? `\nFiyat gönderimi KAPALI olan kanallar: ${closed.join(', ')} — kaydedilir ama gönderilmez.` : '')
      + '\nDevam edilsin mi?')) return;
    setBusy(true);
    setError(null);
    try {
      const r = await put<{ changed: number; unchanged: number }>('/api/prices/listings/overrides',
        { changes: pending.map(x => ({ listingId: x.listingId, price: parsePrice(x.text) })) });
      setDrafts({});
      onChanged(`${r.changed} ilan fiyatı kaydedildi.`);
      data.reload();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const columns = useMemo<ColumnDef<SkuPriceOverview>[]>(() => [
    {
      header: 'Ürün',
      cell: ({ row }) => (
        <div className="with-thumb">
          <Thumb url={row.original.imageUrl} alt={row.original.productName} size={44} />
          <div>
            <div>{row.original.productName}</div>
            <div className="muted small">{row.original.skuCode}{row.original.barcode ? ` · ${row.original.barcode}` : ''}</div>
          </div>
        </div>
      )
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
        if (list.length === 0) return <ChannelCell c={undefined} draft={undefined} editable={false} onDraft={setDraft} />;
        return <>{list.map(c => <ChannelCell key={c.listingId} c={c} draft={drafts[c.listingId]} editable={editable} onDraft={setDraft} />)}</>;
      }
    }))
  ], [columnsChannels, drafts, editable]);

  return (
    <Card title="Ürün bazında kanal fiyatları">
      <div className="notes small" data-testid="price-notes">
        <p><strong>Nasıl çalışır?</strong> Her pazaryerinin fiyatı <em>SKU fiyatı × kanal kuralı</em> (çarpan, ek, yuvarlama, taban) ile hesaplanır.
          Bir kanala <strong>elle fiyat</strong> girerseniz o kanalda kural yerine o fiyat kullanılır; diğer kanallar etkilenmez. Kutuyu boşaltmak elle fiyatı kaldırır.</p>
        <p><strong>Kanalda</strong>: pazaryerinde şu an görünen fiyat · <strong>Gidecek</strong>: kanala gönderilecek fiyat ·
          <span className="badge">elle</span> bu kanala elle girildi · <span className="badge warn">taban</span> taban fiyat uygulandı ·
          <strong> Son gönderilen</strong>: en son kanala giden fiyat.</p>
        <p>Kaydetmek fiyatı hemen göndermez: kanalın <strong>Fiyat gönderimi</strong> kapalıysa gitmez; açıksa eşiği aşan değişiklik "Onay bekleyen fiyatlar"a düşer.
          {(infos.data ?? []).filter(i => i.vatIncluded !== true).length > 0 && (
            <> KDV dahil olduğu doğrulanamayan kanallar: {(infos.data ?? []).filter(i => i.vatIncluded !== true).map(i => channelNames[i.channelCode] ?? i.channelCode).join(', ')}.</>
          )}</p>
      </div>
      <form className="filters" onSubmit={submitSearch}>
        <input placeholder="SKU, ürün adı veya barkod" value={input} onChange={e => setInput(e.target.value)} aria-label="Fiyat ürün ara" />
        <button type="submit">Ara</button>
        {editable && (
          <>
            <button type="button" className="primary" disabled={busy || pending.length === 0 || invalid.length > 0} onClick={save} data-testid="save-prices">
              Değişiklikleri kaydet ({pending.length})
            </button>
            <button type="button" disabled={pending.length === 0} onClick={() => setDrafts({})}>Vazgeç</button>
          </>
        )}
      </form>
      {invalid.length > 0 && (
        <div className="alert alert-warn">{invalid.length} kutuda geçersiz fiyat var (0'dan büyük, en fazla 2 ondalık; ör. 499,90).</div>
      )}
      <ErrorBox error={error ?? data.error} onRetry={data.reload} />
      {error instanceof ApiError && error.fieldErrors && <div className="muted small">{Object.values(error.fieldErrors).flat().join(' ')}</div>}
      <DataTable columns={columns} data={data.data?.items ?? []} loading={data.loading} rowTestId="sku-price-row" />
      {data.data && <Pager page={page} pageSize={data.data.pageSize} total={data.data.total} onPage={p => {
        if (pending.length > 0 && !window.confirm('Kaydedilmemiş fiyat değişiklikleri var; sayfa değişirse kaybolacak. Devam edilsin mi?')) return;
        setDrafts({});
        setPage(p);
      }} />}
    </Card>
  );
}
