import { useMemo, useState } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { get, post, qs, type Channel, type OrderDetail, type OrderListItem, type OrderStatus, type Paged } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ChannelBadge, ErrorBox, StatusBadge, useLoad } from '../components/ui';
import { channelNames, dayEndUtc, dayStartUtc, formatDateTime, formatMoney, nextStatuses, statusLabels } from '../format';
import { OrderNotificationHistory, OrderNotifications } from './ChannelWrite';

const columns: ColumnDef<OrderListItem>[] = [
  { header: 'Tarih', cell: ({ row }) => formatDateTime(row.original.orderDateUtc) },
  { header: 'Kanal', cell: ({ row }) => <ChannelBadge code={row.original.channelCode} /> },
  { header: 'Sipariş No', accessorKey: 'channelOrderNo' },
  { header: 'Durum', cell: ({ row }) => <StatusBadge status={row.original.status} /> },
  { header: 'Tutar', cell: ({ row }) => formatMoney(row.original.total) },
  { header: 'Satır', accessorKey: 'lineCount' },
  {
    header: 'Uyarı',
    cell: ({ row }) => (
      <>
        {row.original.insufficientStock && <span className="badge warn">Stok yetersiz</span>}
        {row.original.unmatchedLineCount > 0 && <span className="badge warn">Eşleşmeyen ürün</span>}
      </>
    )
  }
];

type Filters = { channelId: string; status: string; from: string; to: string; sku: string; search: string; insufficientStock: boolean };
const emptyFilters: Filters = { channelId: '', status: '', from: '', to: '', sku: '', search: '', insufficientStock: false };

/** Ekran 4: Siparişler (tüm kanallar tek listede, kanal rozeti, durum/tarih/ürün filtresi, detayda zaman çizelgesi). */
export function OrdersPage() {
  const [filters, setFilters] = useState<Filters>(emptyFilters);
  const [applied, setApplied] = useState<Filters>(emptyFilters);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<number | null>(null);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const orders = useLoad(
    () => get<Paged<OrderListItem>>(`/api/orders${qs({
      channelId: applied.channelId, status: applied.status, from: dayStartUtc(applied.from), to: dayEndUtc(applied.to),
      sku: applied.sku, search: applied.search, insufficientStock: applied.insufficientStock || undefined, page, pageSize: 50
    })}`),
    [applied, page]
  );

  return (
    <>
      <h1>Siparişler</h1>
      <Card>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setSelected(null); setApplied(filters); }}>
          <select aria-label="Kanal" value={filters.channelId} onChange={e => setFilters({ ...filters, channelId: e.target.value })}>
            <option value="">Tüm kanallar</option>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
          <select aria-label="Durum" value={filters.status} onChange={e => setFilters({ ...filters, status: e.target.value })}>
            <option value="">Tüm durumlar</option>
            {(Object.keys(statusLabels) as OrderStatus[]).map(s => <option key={s} value={s}>{statusLabels[s]}</option>)}
          </select>
          <input type="date" aria-label="Başlangıç" value={filters.from} onChange={e => setFilters({ ...filters, from: e.target.value })} />
          <input type="date" aria-label="Bitiş" value={filters.to} onChange={e => setFilters({ ...filters, to: e.target.value })} />
          <input placeholder="Ürün (SKU)" aria-label="Ürün" value={filters.sku} onChange={e => setFilters({ ...filters, sku: e.target.value })} />
          <input placeholder="Sipariş no" aria-label="Sipariş no" value={filters.search} onChange={e => setFilters({ ...filters, search: e.target.value })} />
          <label className="check"><input type="checkbox" checked={filters.insufficientStock} onChange={e => setFilters({ ...filters, insufficientStock: e.target.checked })} /> Stok yetersiz</label>
          <button type="submit">Filtrele</button>
          <button type="button" className="link" onClick={() => { setFilters(emptyFilters); setApplied(emptyFilters); setPage(1); }}>Temizle</button>
        </form>
        <ErrorBox error={orders.error} />
        <div className={selected ? 'split' : undefined}>
          <div>
            <DataTable
              columns={columns}
              data={orders.data?.items ?? []}
              loading={orders.loading}
              rowTestId="order-row"
              onRowClick={o => setSelected(o.id)}
              selectedKey={o => o.id === selected}
            />
            {orders.data && <Pager page={page} pageSize={orders.data.pageSize} total={orders.data.total} onPage={setPage} />}
          </div>
          {selected && <OrderDetailPanel id={selected} onChanged={orders.reload} onClose={() => setSelected(null)} />}
        </div>
      </Card>
      {channels.data && <OrderNotifications channels={channels.data} onChanged={orders.reload} refreshKey={orders.data} />}
    </>
  );
}

export function OrderDetailPanel({ id, onChanged, onClose, returnMode }: { id: number; onChanged: () => void; onClose: () => void; returnMode?: boolean }) {
  const { can } = useAuth();
  const [actionError, setActionError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const detail = useLoad(() => get<OrderDetail>(`/api/orders/${id}`), [id]);
  const d = detail.data;
  const next = useMemo(() => (d ? nextStatuses[d.status] : []), [d]);

  async function act(run: () => Promise<unknown>) {
    setBusy(true);
    setActionError(null);
    try {
      await run();
      detail.reload();
      onChanged();
    } catch (err) {
      setActionError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="detail" data-testid="order-detail">
      <header className="card-header">
        <h2>{d ? d.channelOrderNo : 'Sipariş'}</h2>
        <button className="link" onClick={onClose} aria-label="Kapat">✕</button>
      </header>
      <ErrorBox error={detail.error ?? actionError} />
      {d && (
        <>
          <p><ChannelBadge code={d.channelCode} /> <StatusBadge status={d.status} /> {formatMoney(d.total)}</p>
          {d.insufficientStock && <div className="alert alert-warn">Stok yetersiz: satırlardan en az biri rezerve edilemedi.</div>}
          <dl className="kv">
            <dt>Müşteri</dt><dd data-testid="customer-name">{d.customer?.fullName ?? '—'}</dd>
            <dt>İl / İlçe</dt><dd>{[d.customer?.city, d.customer?.district].filter(Boolean).join(' / ') || '—'}</dd>
            <dt>Kargo</dt><dd>{d.cargo ? `${d.cargo.company ?? ''} ${d.cargo.trackingNumber ?? ''}` : '—'}</dd>
          </dl>

          {!returnMode && can('orderWrite') && next.length > 0 && (
            <div className="actions">
              {next.map(s => (
                <button key={s} disabled={busy} className={s === 'Iptal' ? 'danger' : undefined}
                  onClick={() => act(() => post(`/api/orders/${d.id}/status`, { status: s }))}>
                  {statusLabels[s]}
                </button>
              ))}
            </div>
          )}

          <h3>Satırlar</h3>
          <table className="table">
            <thead><tr><th>SKU</th><th>Ürün</th><th>Adet</th><th>Fiyat</th><th>Stok</th>{returnMode && <th>Depo</th>}</tr></thead>
            <tbody>
              {d.lines.map(l => (
                <tr key={l.id}>
                  <td>{l.skuCode ?? <span className="badge warn">Eşleşmedi</span>}</td>
                  <td>{l.productName ?? '—'}</td>
                  <td>{l.quantity}</td>
                  <td>{formatMoney(l.unitPrice)}</td>
                  <td>{l.returnReceived ? 'İade girdi' : l.shipped ? 'Sevk edildi' : l.released ? 'Rezerv iade' : l.reserved ? 'Rezerve' : '—'}</td>
                  {returnMode && (
                    <td>
                      {l.returnReceived ? <span className="badge ok">Depoya girdi ✓</span>
                        : d.status === 'Iade' && l.shipped && l.skuId && can('stockWrite')
                          ? <button disabled={busy} onClick={() => act(() => post(`/api/orders/${d.id}/lines/${l.id}/return-receipt`))}>Depoya girdi</button>
                          : '—'}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>

          <OrderNotificationHistory orderId={d.id} />

          <h3>Zaman çizelgesi</h3>
          <ol className="timeline" data-testid="order-timeline">
            {d.timeline.map(e => (
              <li key={e.id}>
                <strong>{statusLabels[e.to]}</strong> <span className="muted">{formatDateTime(e.atUtc)} · {e.source}</span>
              </li>
            ))}
          </ol>
        </>
      )}
    </aside>
  );
}
