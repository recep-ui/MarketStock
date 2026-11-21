import { useState, type FormEvent } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { download, get, put, qs, upload, type Channel, type ImportReport, type Listing, type Paged } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Link } from 'react-router-dom';
import { Card, ChannelBadge, ChannelLink, ErrorBox, Field, Notice, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { resolveSku } from './MovementsPage';
import { ChannelCatalogFetch, ChannelCatalogImport, UnmatchedChannelItems } from './ChannelCatalog';
import { basisLabels, variantText } from './ChannelStockTab';

// ADR-044: her satırda kanalın bildirdiği ürün (görsel, ad, kodlar, varyant) ↔ bağlı SKU (ürün adı, renk/beden, barkod) yan yana
const columns: ColumnDef<Listing>[] = [
  { header: 'Kanal', cell: ({ row }) => <ChannelBadge code={row.original.channelCode} /> },
  {
    header: 'Kanaldaki ürün',
    cell: ({ row }) => {
      const r = row.original;
      return (
        <div className="with-thumb" data-testid="listing-channel-side"><Thumb url={r.imageUrl} alt={r.channelName ?? r.skuCode} size={44} /><div>
          <div><strong>{r.channelName ?? <span className="muted">(kanal adı bilinmiyor)</span>}</strong></div>
          <div className="small muted">
            {[r.channelBarcode && `Barkod ${r.channelBarcode}`, r.channelProductCode && `Kod ${r.channelProductCode}`].filter(Boolean).join(' · ') || '—'}
          </div>
          {r.variant && <div className="small">{r.variant}</div>}
          <div className="small">
            {r.channelStatus && <span className="muted">{r.channelStatus} </span>}
            <ChannelLink code={r.channelCode} url={r.productUrl} />
          </div>
        </div></div>
      );
    }
  },
  { id: 'arrow', header: () => <span aria-label="bağlı">→</span>, cell: () => <span className="muted" aria-hidden="true">→</span> },
  {
    header: 'Bağlı SKU',
    cell: ({ row }) => {
      const r = row.original;
      return (
        <div data-testid="listing-sku-side">
          <div><Link to={`/stok/${r.skuId}`}>{r.skuCode}</Link></div>
          {r.productName && <div className="small">{r.productName}</div>}
          <div className="small muted">
            {[variantText(r.skuColor ?? null, r.skuSize ?? null), r.skuBarcode && `Barkod ${r.skuBarcode}`].filter(Boolean).join(' · ')}
          </div>
        </div>
      );
    }
  },
  {
    header: 'Eşleşme',
    cell: ({ row }) => {
      const r = row.original;
      const basis = r.matchBasis ? basisLabels[r.matchBasis] : null;
      return (
        <div data-testid="listing-match">
          {basis && <span className={`badge${basis.tone ? ' ' + basis.tone : ''}`} title={basis.title}>{basis.text}</span>}
          {(r.warningCount ?? 0) > 0 && (
            <div className="small">
              <Link to={`/stok?kanal=${r.channelCode}`} title="Stok Listesi → kanal sekmesinde uyarıları ve onay durumunu görün">
                ⚠ {r.warningCount} uyarı, kontrol et
              </Link>
            </div>
          )}
        </div>
      );
    }
  },
  { header: 'Durum', accessorKey: 'status' },
  { header: 'Son gönderilen', cell: ({ row }) => row.original.lastSentStock ?? '—' },
  { header: 'Son senkron', cell: ({ row }) => formatDateTime(row.original.lastSyncUtc) }
];

const kinds = [
  { kind: 'products', label: 'Ürün / SKU', policy: 'catalogWrite' as const },
  { kind: 'stock', label: 'Stok (giriş / sayım)', policy: 'stockWrite' as const },
  { kind: 'listings', label: 'Eşleştirme', policy: 'catalogWrite' as const }
];

/** Ekran 6: Eşleştirme (SKU ↔ kanal ürün kodu) + Excel ile toplu yükleme / dışa aktarma. */
export function MappingPage() {
  const { can } = useAuth();
  const [channelId, setChannelId] = useState('');
  const [search, setSearch] = useState('');
  const [applied, setApplied] = useState({ channelId: '', search: '' });
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const listings = useLoad(() => get<Paged<Listing>>(`/api/listings${qs({ ...applied, page, pageSize: 50 })}`), [applied, page]);

  return (
    <>
      <h1>Eşleştirme</h1>
      {can('admin') && (
        <>
          <ChannelCatalogImport channels={channels.data ?? []} onApplied={() => { listings.reload(); setRefreshKey(k => k + 1); }} />
          <ChannelCatalogFetch channels={channels.data ?? []} onApplied={() => { listings.reload(); setRefreshKey(k => k + 1); }} />
        </>
      )}
      <UnmatchedChannelItems channels={channels.data ?? []} canWrite={can('catalogWrite')} refreshKey={refreshKey} onLinked={listings.reload} />
      {can('catalogWrite') && <ListingForm channels={channels.data ?? []} onDone={listings.reload} />}
      <Card title="Eşleştirmeler">
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied({ channelId, search }); }}>
          <select aria-label="Kanal" value={channelId} onChange={e => setChannelId(e.target.value)}>
            <option value="">Tüm kanallar</option>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
          <input placeholder="SKU, kanal kodu, barkod, ürün adı veya kanaldaki ad" value={search} onChange={e => setSearch(e.target.value)} aria-label="Ara" />
          <button type="submit">Ara</button>
        </form>
        <ErrorBox error={listings.error} />
        <DataTable columns={columns} data={listings.data?.items ?? []} loading={listings.loading} rowTestId="listing-row" />
        {listings.data && <Pager page={page} pageSize={listings.data.pageSize} total={listings.data.total} onPage={setPage} />}
      </Card>
      <Card title="Excel ile toplu işlem">
        <div className="excel-grid">
          {kinds.map(k => <ExcelBox key={k.kind} kind={k.kind} label={k.label} canImport={can(k.policy)} onImported={listings.reload} />)}
        </div>
      </Card>
    </>
  );
}

function ListingForm({ channels, onDone }: { channels: Channel[]; onDone: () => void }) {
  const [form, setForm] = useState({ sku: '', channelId: '', code: '', barcode: '', status: 'AKTIF' });
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const sku = await resolveSku(form.sku);
      if (!sku) throw new Error(`SKU bulunamadı: ${form.sku}`);
      await put('/api/listings', {
        skuId: sku.id, channelId: Number(form.channelId || channels[0]?.id),
        channelProductCode: form.code || null, channelBarcode: form.barcode || null, status: form.status
      });
      setNotice(`${sku.skuCode} eşleştirmesi kaydedildi; kanal stoğu senkron kuyruğuna alındı.`);
      setForm({ ...form, sku: '', code: '', barcode: '' });
      onDone();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Eşleştirme ekle / güncelle">
      <Notice text={notice} onClose={() => setNotice(null)} />
      {error ? <div className="alert alert-error">{(error as Error).message}</div> : null}
      <form className="filters" onSubmit={submit}>
        <Field label="SKU kodu"><input value={form.sku} onChange={e => setForm({ ...form, sku: e.target.value })} required /></Field>
        <Field label="Kanal">
          <select value={form.channelId} onChange={e => setForm({ ...form, channelId: e.target.value })}>
            {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
        </Field>
        <Field label="Kanal ürün kodu"><input value={form.code} onChange={e => setForm({ ...form, code: e.target.value })} /></Field>
        <Field label="Kanal barkod"><input value={form.barcode} onChange={e => setForm({ ...form, barcode: e.target.value })} /></Field>
        <Field label="Durum">
          <select value={form.status} onChange={e => setForm({ ...form, status: e.target.value })}>
            <option value="AKTIF">Aktif</option><option value="PASIF">Pasif</option>
          </select>
        </Field>
        <button className="primary" type="submit">Kaydet</button>
      </form>
    </Card>
  );
}

export function ExcelBox({ kind, label, canImport, onImported }: { kind: string; label: string; canImport: boolean; onImported: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [validateOnly, setValidateOnly] = useState(true);
  const [report, setReport] = useState<ImportReport | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function run() {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const r = await upload<ImportReport>(`/api/excel/${kind}/import${qs({ validateOnly })}`, file);
      setReport(r);
      if (!validateOnly) onImported();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="excel-box">
      <h3>{label}</h3>
      <div className="actions">
        <button onClick={() => download(`/api/excel/${kind}/template`, `sablon-${kind}.xlsx`).catch(setError)}>Şablon indir</button>
        <button onClick={() => download(`/api/excel/${kind}/export`, `${kind}.xlsx`).catch(setError)}>Dışa aktar</button>
      </div>
      {canImport && (
        <div className="import">
          <input type="file" accept=".xlsx" onChange={e => setFile(e.target.files?.[0] ?? null)} aria-label={`${label} dosyası`} />
          <label className="check"><input type="checkbox" checked={validateOnly} onChange={e => setValidateOnly(e.target.checked)} /> Yalnızca doğrula</label>
          <button className="primary" disabled={!file || busy} onClick={run}>{busy ? 'İşleniyor…' : 'İçe aktar'}</button>
        </div>
      )}
      <ErrorBox error={error} />
      {report && (
        <div className="report" data-testid={`import-report-${kind}`}>
          {report.fileErrors.map(e => <div key={e} className="alert alert-error">{e}</div>)}
          {report.fileErrors.length === 0 && (
            <p>
              {report.validateOnly ? 'Doğrulama: ' : ''}{report.totalRows} satır · {report.created} yeni · {report.updated} güncellenen ·{' '}
              {report.unchanged} değişmeyen · <strong>{report.errors.length > 0 ? `${new Set(report.errors.map(e => e.row)).size} hatalı satır` : 'hata yok'}</strong>
            </p>
          )}
          {report.errors.length > 0 && (
            <table className="table">
              <thead><tr><th>Satır</th><th>Sütun</th><th>Hata</th></tr></thead>
              <tbody>
                {report.errors.slice(0, 200).map((e, i) => <tr key={i}><td>{e.row}</td><td>{e.column ?? '—'}</td><td>{e.message}</td></tr>)}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  );
}
