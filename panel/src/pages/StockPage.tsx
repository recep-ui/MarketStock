import { useMemo, useState } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { get, qs, type Channel, type Paged, type StockRow } from '../api';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ErrorBox, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';

/** Ekran 2: Stok Listesi. Fiziksel/rezerve/satılabilir + her kanalda görünen stok; sapma satırları kırmızı. */
export function StockPage() {
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
    { header: 'SKU', accessorKey: 'skuCode' },
    { header: 'Ürün', accessorKey: 'productName' },
    { header: 'Fiziksel', accessorKey: 'physical' },
    { header: 'Rezerve', accessorKey: 'reserved' },
    { header: 'Güvenlik', accessorKey: 'safetyStock' },
    { header: 'Satılabilir', accessorKey: 'available' },
    ...(channels.data ?? []).map<ColumnDef<StockRow>>(ch => ({
      id: ch.code,
      header: channelNames[ch.code] ?? ch.code,
      cell: ({ row }) => {
        const cell = row.original.channels.find(c => c.channelCode === ch.code);
        if (!cell) return <span className="muted">—</span>;
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
      <h1>Stok Listesi</h1>
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
