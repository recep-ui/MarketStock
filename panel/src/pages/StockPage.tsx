import { useMemo, useState } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { Link, useSearchParams } from 'react-router-dom';
import { get, qs, type Channel, type Paged, type StockRow } from '../api';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ChannelLink, ErrorBox, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { ChannelStockTab } from './ChannelStockTab';

/**
 * Ekran 2: Stok Listesi. "Defter" sekmesi: fiziksel/rezerve/satılabilir + her kanalda görünen stok; sapma satırları kırmızı.
 * Kanal sekmeleri (ADR-035): her pazaryerinin çekilen stok listesi ayrı ayrı, bağlandığı SKU ve yanlış eşleştirme uyarılarıyla.
 * Seçili sekme ?kanal=KOD ile adreste tutulur.
 */
export function StockPage() {
  const [params, setParams] = useSearchParams();
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const selected = (channels.data ?? []).find(c => c.code === params.get('kanal')) ?? null;
  const select = (code: string | null) =>
    setParams(p => { if (code) p.set('kanal', code); else p.delete('kanal'); return p; }, { replace: true });

  return (
    <>
      <h1>Stok Listesi</h1>
      <div className="tabs" role="tablist" aria-label="Stok listesi görünümü">
        <button role="tab" aria-selected={!selected} className={!selected ? 'active' : undefined} onClick={() => select(null)}>
          Defter (tüm kanallar)
        </button>
        {(channels.data ?? []).map(c => (
          <button key={c.id} role="tab" aria-selected={selected?.id === c.id} className={selected?.id === c.id ? 'active' : undefined} onClick={() => select(c.code)}>
            {channelNames[c.code] ?? c.name}{!c.active && <small className="muted"> (pasif)</small>}
          </button>
        ))}
      </div>
      {selected ? <ChannelStockTab key={selected.id} channel={selected} /> : <LedgerStockView />}
    </>
  );
}

/** Defter görünümü (önceki Stok Listesi; değişmedi). */
function LedgerStockView() {
  const [search, setSearch] = useState('');
  const [applied, setApplied] = useState('');
  const [onlyDeviations, setOnlyDeviations] = useState(false);
  const [lowStock, setLowStock] = useState(false);
  const [page, setPage] = useState(1);

  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const rows = useLoad(
    () => get<Paged<StockRow>>(`/api/stock-overview${qs({ search: applied, onlyDeviations, lowStock, page, pageSize: 50 })}`),
    [applied, onlyDeviations, lowStock, page]
  );

  const columns = useMemo<ColumnDef<StockRow>[]>(() => [
    { header: 'SKU', cell: ({ row }) => <Link to={`/stok/${row.original.skuId}`} title="SKU detayını aç">{row.original.skuCode}</Link> },
    {
      header: 'Ürün',
      cell: ({ row }) => {
        // Ürünün kanal sayfaları (ADR-031): her ilan için ayrı link; varyant bilgisi ipucu olarak.
        const links = row.original.channels.filter(c => c.productUrl);
        return (
          <>
            {row.original.productName}
            {links.length > 0 && (
              <div className="small">
                {links.map(c => (
                  <span key={c.listingId} title={c.variant ?? c.channelBarcode ?? undefined}>
                    <ChannelLink code={c.channelCode} url={c.productUrl} label={links.length > 1 ? `${channelNames[c.channelCode] ?? c.channelCode} ${c.variant ?? c.channelBarcode ?? ''}` : undefined} />{' '}
                  </span>
                ))}
              </div>
            )}
          </>
        );
      }
    },
    { header: 'Fiziksel', accessorKey: 'physical' },
    { header: 'Rezerve', accessorKey: 'reserved' },
    { header: 'Güvenlik', accessorKey: 'safetyStock' },
    { header: 'Satılabilir', accessorKey: 'available' },
    ...(channels.data ?? []).map<ColumnDef<StockRow>>(ch => ({
      id: ch.code,
      header: channelNames[ch.code] ?? ch.code,
      cell: ({ row }) => {
        // Aynı SKU'nun bu kanalda birden fazla ilanı olabilir (ADR-023): hepsi gösterilir, herhangi biri saparsa hücre kırmızı.
        const cells = row.original.channels.filter(c => c.channelCode === ch.code);
        if (cells.length === 0) return <span className="muted">—</span>;
        if (cells.length > 1) {
          const deviates = cells.some(c => c.hasDeviation);
          return (
            <span
              className={deviates ? 'cell-deviation' : undefined}
              data-testid={`cell-${ch.code}`}
              title={cells.map(c => `${c.channelBarcode ?? c.channelProductCode ?? `#${c.listingId}`}: beklenen ${c.expected} · son gönderilen ${c.lastSent ?? '—'} · kanalda ${c.channelStock ?? '—'} (${formatDateTime(c.fetchedAtUtc)})${c.status === 'PASIF' ? ' · pasif' : ''}`).join('\n')}
            >
              {cells.map(c => c.channelStock ?? c.lastSent ?? '—').join(' · ')}
              <small className="muted"> ({cells.length} ilan)</small>
              {deviates && <small> (beklenen {cells[0].expected})</small>}
            </span>
          );
        }
        const cell = cells[0];
        const visible = cell.channelStock ?? cell.lastSent;
        return (
          <span
            className={cell.hasDeviation ? 'cell-deviation' : undefined}
            data-testid={`cell-${ch.code}`}
            title={`Beklenen ${cell.expected} · son gönderilen ${cell.lastSent ?? '—'} · kanalda ${cell.channelStock ?? '—'} (${formatDateTime(cell.fetchedAtUtc)})`}
          >
            {visible ?? '—'}
            {cell.hasDeviation && <small> (beklenen {cell.expected})</small>}
            {cell.status === 'PASIF' && <small className="muted"> pasif</small>}
          </span>
        );
      }
    }))
  ], [channels.data]);

  return (
    <>
      <Card>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(search); }}>
          <input placeholder="SKU, barkod veya ürün adı" value={search} onChange={e => setSearch(e.target.value)} aria-label="Ara" />
          <label className="check"><input type="checkbox" checked={onlyDeviations} onChange={e => { setPage(1); setOnlyDeviations(e.target.checked); }} /> Yalnızca sapmalar</label>
          <label className="check"><input type="checkbox" checked={lowStock} onChange={e => { setPage(1); setLowStock(e.target.checked); }} /> Kritik stok</label>
          <button type="submit">Ara</button>
        </form>
        <p className="muted small">Kanal sütunu: son mutabakatta kanalda görülen stok (yoksa son gönderilen). Kırmızı satır: kanaldaki değer beklenenden farklı.</p>
        <ErrorBox error={rows.error ?? channels.error} />
        <DataTable
          columns={columns}
          data={rows.data?.items ?? []}
          loading={rows.loading}
          rowTestId="stock-row"
          rowClassName={r => (r.hasDeviation ? 'row-deviation' : undefined)}
        />
        {rows.data && <Pager page={page} pageSize={rows.data.pageSize} total={rows.data.total} onPage={setPage} />}
      </Card>
    </>
  );
}
