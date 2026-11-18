import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { get, post, qs, type Channel, type Paged, type PushListingsResult, type Reconciliation, type SyncItem, type SyncSummary } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ChannelBadge, ErrorBox, Notice, SyncBadge, useLoad } from '../components/ui';
import { channelNames, formatDateTime, formatMoney, syncStatusLabels } from '../format';
import { ChannelCommands } from './ChannelWrite';

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
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [deviationSearch, setDeviationSearch] = useState('');
  const visibleDeviations = (deviations.data ?? [])
    .filter(r => !deviationSearch || r.skuCode.toLocaleUpperCase('tr').includes(deviationSearch.trim().toLocaleUpperCase('tr')));
  const [skipped, setSkipped] = useState<PushListingsResult['skipped']>([]);

  /** Sapmayı kanala yaz (ADR-024): defterdeki değer seçilen ilanlara mevcut senkron hattından zorla gönderilir. */
  async function pushToChannel(listingIds: number[]) {
    setActionError(null);
    try {
      const r = await post<PushListingsResult>('/api/sync/reconciliation/push', { listingIds });
      setSkipped(r.skipped);
      setNotice(`${r.queued} ilan defterdeki değerle kuyruğa alındı${r.skipped.length ? `, ${r.skipped.length} atlandı` : ''}.`);
      setSelected(new Set());
      queue.reload();
    } catch (err) {
      setActionError(err);
    }
  }

  async function refreshChannel() {
    const ch = (channels.data ?? []).find(c => String(c.id) === channelId);
    if (!ch || !window.confirm(`${channelNames[ch.code] ?? ch.name}: kanalın TÜM aktif ilanları değer aynı olsa da yeniden gönderilecek. Devam edilsin mi?`)) return;
    setActionError(null);
    try {
      await post(`/api/sync/channels/${ch.id}/refresh`);
      setNotice(`${channelNames[ch.code] ?? ch.name} kanalının tüm ilanları yeniden gönderim için kuyruğa alınıyor.`);
    } catch (err) {
      setActionError(err);
    }
  }

  function toggle(id: number) {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

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
    { header: 'Gönderilen', cell: ({ row }) => row.original.sentPrice != null ? formatMoney(row.original.sentPrice) : row.original.sentValue ?? '—' },
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

      <ChannelCommands channelId={channelId} onNotice={setNotice} />
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
        <p className="muted small">
          Son mutabakatta kanaldaki stok beklenenden farklı olan veya kanalda bulunamayan eşleştirmeler. "Kanala gönder" defterdeki değeri
          (Beklenen) yeniden gönderir. Kanaldaki değerin doğru olduğunu düşünüyorsanız Stok Listesi → "Kanal stoğunu deftere al" ekranını kullanın.
        </p>
        {can('admin') && (
          <div className="filters">
            <button className="primary" disabled={selected.size === 0} onClick={() => pushToChannel([...selected])} data-testid="push-selected">
              Seçilenleri kanala gönder ({selected.size})
            </button>
            <button disabled={visibleDeviations.length === 0}
              onClick={() => pushToChannel(visibleDeviations.slice(0, 5000).map(r => r.listingId))}>Listelenen sapmaları kanala gönder ({visibleDeviations.length})</button>
            <button disabled={!channelId} onClick={refreshChannel} title="Önce yukarıdan kanal seçin">Kanalı tamamen yenile</button>
          </div>
        )}
        <div className="filters">
          <input placeholder="SKU ara" value={deviationSearch} onChange={e => setDeviationSearch(e.target.value)} aria-label="Sapma ara" />
          <span className="muted">{visibleDeviations.length} sapma{visibleDeviations.length > 200 ? ' (ilk 200 gösteriliyor)' : ''}</span>
        </div>
        {skipped.length > 0 && (
          <div className="alert alert-warn">
            Atlanan ilanlar: {skipped.slice(0, 20).map(s => `#${s.listingId}: ${s.reason}`).join(' · ')}{skipped.length > 20 ? ' …' : ''}
          </div>
        )}
        <table className="table">
          <thead><tr>{can('admin') && <th />}<th>Kanal</th><th>SKU</th><th>Beklenen</th><th>Kanalda</th><th>Fark</th><th>Çekilme</th>{can('admin') && <th />}</tr></thead>
          <tbody>
            {visibleDeviations.slice(0, 200).map(r => (
              <tr key={r.listingId} className="row-deviation" data-testid="deviation-row">
                {can('admin') && <td><input type="checkbox" aria-label={`Seç ${r.skuCode}`} checked={selected.has(r.listingId)} onChange={() => toggle(r.listingId)} /></td>}
                <td><ChannelBadge code={r.channelCode} /></td>
                <td>{r.skuCode}</td>
                <td>{r.expected}</td>
                <td>{r.channelStock ?? 'kanalda yok'}</td>
                <td>{r.deviation ?? '—'}</td>
                <td>{formatDateTime(r.fetchedAtUtc)}</td>
                {can('admin') && <td><button className="small" onClick={() => pushToChannel([r.listingId])}>Kanala gönder</button></td>}
              </tr>
            ))}
            {deviations.data && visibleDeviations.length === 0 && <tr><td colSpan={8} className="empty">Sapma yok.</td></tr>}
          </tbody>
        </table>
      </Card>
    </>
  );
}
