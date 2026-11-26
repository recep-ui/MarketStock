import { useState } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { get, qs, type Channel, type OrderListItem, type Paged } from '../api';
import { DataTable, Pager } from '../components/DataTable';
import { ExportButton } from '../components/ExportButton';
import { fetchAllPages } from '../export';
import { orderExportColumns } from '../exportColumns';
import { Card, ChannelBadge, ErrorBox, StatusBadge, useLoad } from '../components/ui';
import { formatDateTime, formatMoney } from '../format';
import { OrderDetailPanel } from './OrdersPage';
import { ChannelReturns } from './ChannelReturns';

const columns: ColumnDef<OrderListItem>[] = [
  { header: 'Tarih', cell: ({ row }) => formatDateTime(row.original.orderDateUtc) },
  { header: 'Kanal', cell: ({ row }) => <ChannelBadge code={row.original.channelCode} /> },
  { header: 'Sipariş No', accessorKey: 'channelOrderNo' },
  { header: 'Durum', cell: ({ row }) => <StatusBadge status={row.original.status} /> },
  { header: 'Tutar', cell: ({ row }) => formatMoney(row.original.total) }
];

/**
 * Ekran 5: İade / İptal. İptallerde rezerv otomatik iade edilir; iadelerde mal depoya ulaşınca
 * satır bazında "Depoya girdi" onayı verilir (usp_IadeGiris, satır başına bir kez).
 */
export function ReturnsPage() {
  const [tab, setTab] = useState<'Iade' | 'Iptal' | 'Talep'>('Talep');
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<number | null>(null);
  const orders = useLoad(() => tab === 'Talep' ? Promise.resolve(null) : get<Paged<OrderListItem>>(`/api/orders${qs({ status: tab, page, pageSize: 50 })}`), [tab, page]);

  return (
    <>
      <h1>İade / İptal</h1>
      <div className="tabs">
        <button className={tab === 'Talep' ? 'active' : undefined} onClick={() => { setTab('Talep'); setSelected(null); }}>Kanal iade talepleri</button>
        <button className={tab === 'Iade' ? 'active' : undefined} onClick={() => { setTab('Iade'); setPage(1); setSelected(null); }}>İadeler</button>
        <button className={tab === 'Iptal' ? 'active' : undefined} onClick={() => { setTab('Iptal'); setPage(1); setSelected(null); }}>İptaller</button>
      </div>
      {tab === 'Talep' && <Card>{channels.data && <ChannelReturns channels={channels.data} />}</Card>}
      {tab !== 'Talep' && <Card actions={<ExportButton fileName={tab === 'Iade' ? 'iadeler' : 'iptaller'} sheetName={tab === 'Iade' ? 'İadeler' : 'İptaller'}
        columns={orderExportColumns} load={() => fetchAllPages((p, size) => get<Paged<OrderListItem>>(`/api/orders${qs({ status: tab, page: p, pageSize: size })}`))} />}>
        <p className="muted small">
          {tab === 'Iade'
            ? 'İade edilen ürün depoya fiziksel olarak ulaştığında satırdaki "Depoya girdi" ile onaylayın; stok ancak o zaman artar.'
            : 'İptal edilen siparişlerin rezervleri otomatik iade edilmiştir.'}
        </p>
        <ErrorBox error={orders.error} />
        <div className={selected ? 'split' : undefined}>
          <div>
            <DataTable columns={columns} data={orders.data?.items ?? []} loading={orders.loading} rowTestId="return-row"
              onRowClick={o => setSelected(o.id)} selectedKey={o => o.id === selected} />
            {orders.data && <Pager page={page} pageSize={orders.data.pageSize} total={orders.data.total} onPage={setPage} />}
          </div>
          {selected && <OrderDetailPanel id={selected} returnMode onChanged={orders.reload} onClose={() => setSelected(null)} />}
        </div>
      </Card>}
    </>
  );
}
