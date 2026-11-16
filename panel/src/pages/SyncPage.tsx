import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { get, post, qs, type Channel, type Paged, type Reconciliation, type SyncItem, type SyncSummary } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ChannelBadge, ErrorBox, Notice, SyncBadge, useLoad } from '../components/ui';
import { channelNames, formatDateTime, syncStatusLabels } from '../format';

/** Ekran 7: Senkron Merkezi (son gönderimler, sadeleştirilmiş hata, "yeniden dene") + mutabakat sapmaları. */
export function SyncPage() {
  const { can } = useAuth();
  const [params] = useSearchParams();
  const [status, setStatus] = useState(params.get('status') ?? '');
  const [channelId, setChannelId] = useState('');
  const [page, setPage] = useState(1);
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);

  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const summary = useLoad(() => get<SyncSummary[]>('/api/sync/summary'), []);
  const queue = useLoad(() => get<Paged<SyncItem>>(`/api/sync/queue${qs({ status, channelId, page, pageSize: 50 })}`), [status, channelId, page]);
  const deviations = useLoad(() => get<Reconciliation[]>(`/api/sync/reconciliation${qs({ channelId, onlyDeviations: true })}`), [channelId]);

  async function retry(item: SyncItem) {
    setActionError(null);
    try {
      await post(`/api/sync/queue/${item.id}/retry`);
      setNotice(`${item.skuCode} (${channelNames[item.channelCode] ?? item.channelCode}) yeniden kuyruğa alındı.`);
      queue.reload();
      summary.reload();
    } catch (err) {
      setActionError(err);
    }
  }

  async function reconcile(code: string) {
    setActionError(null);
    try {
      await post(`/api/sync/reconciliation/${code.toLowerCase()}/run`);
      setNotice(`${channelNames[code] ?? code} mutabakatı başlatıldı; sonuç birkaç dakika içinde görünür.`);
    } catch (err) {
      setActionError(err);
    }
  }

  const columns = useMemo<ColumnDef<SyncItem>[]>(() => [
    { header: 'Kanal', cell: ({ row }) => <ChannelBadge code={row.original.channelCode} /> },
    { header: 'SKU', accessorKey: 'skuCode' },
    { header: 'Durum', cell: ({ row }) => <SyncBadge status={row.original.batchId && row.original.status === 'BEKLIYOR' ? 'GONDERILIYOR' : row.original.status} /> },
    { header: 'Deneme', accessorKey: 'attempts' },
    { header: 'Gönderilen', cell: ({ row }) => row.original.sentValue ?? '—' },
    { header: 'Hata', cell: ({ row }) => <span className="wrap" data-testid="sync-error">{row.original.error ?? '—'}</span> },
    { header: 'Son deneme', cell: ({ row }) => formatDateTime(row.original.lastAttemptUtc) },
    { header: 'Sonraki deneme', cell: ({ row }) => formatDateTime(row.original.nextAttemptUtc) },
    {
      id: 'actions',
      header: '',
      cell: ({ row }) => row.original.status === 'HATA' && can('admin')
        ? <button onClick={e => { e.stopPropagation(); retry(row.original); }}>Yeniden dene</button>
        : null
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [can]);

  return (
    <>
      <h1>Senkron Merkezi</h1>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <ErrorBox error={actionError ?? summary.error ?? queue.error} />
      <div className="stats">
        {(summary.data ?? []).map(s => (
          <div key={s.channelCode} className={`stat ${s.error > 0 ? 'danger' : ''}`}>
            <div className="stat-value"><ChannelBadge code={s.channelCode} /></div>
            <div className="stat-label">
              {s.pending} bekliyor · {s.inFlight} gönderimde · {s.sent} gönderildi · <strong>{s.error} hata</strong>
              <br />Son gönderim: {formatDateTime(s.lastSentUtc)}
            </div>
            {can('admin') && <button className="small" onClick={() => reconcile(s.channelCode)}>Mutabakat başlat</button>}
          </div>
        ))}
      </div>

      <Card title="Gönderim kuyruğu">
        <div className="filters">
          <select aria-label="Durum" value={status} onChange={e => { setPage(1); setStatus(e.target.value); }}>
            <option value="">Tüm durumlar</option>
            {Object.entries(syncStatusLabels).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <select aria-label="Kanal" value={channelId} onChange={e => { setPage(1); setChannelId(e.target.value); }}>
            <option value="">Tüm kanallar</option>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
          <button onClick={() => { queue.reload(); summary.reload(); }}>Yenile</button>
        </div>
        <DataTable columns={columns} data={queue.data?.items ?? []} loading={queue.loading} rowTestId="sync-row"
          rowClassName={r => (r.status === 'HATA' ? 'row-danger' : undefined)} />
        {queue.data && <Pager page={page} pageSize={queue.data.pageSize} total={queue.data.total} onPage={setPage} />}
      </Card>

      <Card title="Mutabakat sapmaları">
        <p className="muted small">Son mutabakatta kanaldaki stok beklenenden farklı olan veya kanalda bulunamayan eşleştirmeler.</p>
        <table className="table">
          <thead><tr><th>Kanal</th><th>SKU</th><th>Beklenen</th><th>Kanalda</th><th>Fark</th><th>Çekilme</th></tr></thead>
          <tbody>
            {(deviations.data ?? []).slice(0, 200).map(r => (
              <tr key={r.listingId} className="row-deviation" data-testid="deviation-row">
                <td><ChannelBadge code={r.channelCode} /></td>
                <td>{r.skuCode}</td>
                <td>{r.expected}</td>
                <td>{r.channelStock ?? 'kanalda yok'}</td>
                <td>{r.deviation ?? '—'}</td>
                <td>{formatDateTime(r.fetchedAtUtc)}</td>
              </tr>
            ))}
            {deviations.data?.length === 0 && <tr><td colSpan={6} className="empty">Sapma yok.</td></tr>}
          </tbody>
        </table>
      </Card>
    </>
  );
}
