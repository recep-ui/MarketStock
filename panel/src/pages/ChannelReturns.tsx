import { useState } from 'react';
import { get, post, qs, type Channel, type ChannelReferenceView, type Paged, type ReturnDetail, type ReturnListItem } from '../api';
import { useAuth } from '../auth';
import { ChannelBadge, ErrorBox, Notice, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { ChannelFetch } from '../components/ChannelFetch';
import { ExportButton } from '../components/ExportButton';
import { fetchAllPages } from '../export';
import type { ExportColumn } from '../export';
import { commandStateLabels } from './ChannelWrite';

const decisionLabels: Record<string, string> = { ONAY: 'Onay', RED: 'Red', TESLIM_ALINDI: 'Teslim alındı' };

const exportColumns: ExportColumn<ReturnListItem>[] = [
  { header: 'Tarih', kind: 'datetime', value: r => r.requestedAtUtc },
  { header: 'Kanal', value: r => channelNames[r.channelCode] ?? r.channelCode },
  { header: 'İade no', value: r => r.returnNo },
  { header: 'Sipariş no', value: r => r.channelOrderNo },
  { header: 'Sipariş eşleşti', kind: 'bool', value: r => r.orderId != null },
  { header: 'Kanal durumu', value: r => r.channelStatus },
  { header: 'Neden', value: r => r.reason },
  { header: 'Satır', kind: 'number', value: r => r.lineCount },
  { header: 'Depoya giren satır', kind: 'number', value: r => r.receivedLineCount },
  { header: 'Son karar', value: r => (r.lastDecision ? decisionLabels[r.lastDecision] ?? r.lastDecision : null) },
  { header: 'Karar durumu', value: r => (r.lastDecisionState ? commandStateLabels[r.lastDecisionState] ?? r.lastDecisionState : null) },
  { header: 'Son güncelleme', kind: 'datetime', value: r => r.lastUpdatedUtc }
];

/**
 * Kanal iade talepleri (ADR-027). Depo "Depoya ulaştı" ile stoğa girer (satır başına bir kez; kanala bildirim tetiklemez).
 * Onay / red / teslim alındı kararını yalnızca Yönetici verir; karar kanal işlemleri kuyruğundan gider. Müşteri bilgisi saklanmaz.
 */
export function ChannelReturns({ channels }: { channels: Channel[] }) {
  const [channelId, setChannelId] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const list = useLoad(() => get<Paged<ReturnListItem>>(`/api/returns${qs({ channelId: channelId || undefined, search: search || undefined, page, pageSize: 50 })}`),
    [channelId, search, page]);

  return (
    <>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <p className="muted small">
        Kanallardan gelen iade talepleri. Mal depoya ulaşınca satırda "Depoya ulaştı" ile stoğa girin. Onay/red kararı ayrıdır ve yalnızca Yönetici verir.
      </p>
      <div className="filters">
        <select aria-label="İade kanalı" value={channelId} onChange={e => { setChannelId(e.target.value); setPage(1); setSelected(null); }}>
          <option value="">Tüm kanallar</option>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
        </select>
        <input placeholder="İade / sipariş no" aria-label="İade no" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <button onClick={list.reload}>Yenile</button>
        <ExportButton fileName="iade-talepleri" sheetName="İade talepleri" columns={exportColumns}
          load={() => fetchAllPages((p, size) => get<Paged<ReturnListItem>>(`/api/returns${qs({ channelId: channelId || undefined, search: search || undefined, page: p, pageSize: size })}`))} />
      </div>
      <ChannelFetch channels={channels} channelId={channelId} endpoint="/api/returns/fetch" jobType="IADE_CEK" label="Kanaldan iade çek" onDone={list.reload} />
      <ErrorBox error={list.error} />
      <div className={selected ? 'split' : undefined}>
        <div>
          <table className="table" data-testid="channel-return-table">
            <thead><tr><th>Tarih</th><th>Kanal</th><th>İade no</th><th>Sipariş no</th><th>Kanal durumu</th><th>Depoya giriş</th><th>Son karar</th></tr></thead>
            <tbody>
              {(list.data?.items ?? []).map(r => (
                <tr key={r.id} data-testid="channel-return-row" className={r.id === selected ? 'selected' : undefined} onClick={() => setSelected(r.id)} style={{ cursor: 'pointer' }}>
                  <td>{formatDateTime(r.requestedAtUtc)}</td>
                  <td><ChannelBadge code={r.channelCode} /></td>
                  <td>{r.returnNo}</td>
                  <td>{r.channelOrderNo}{r.orderId ? '' : <div className="muted small">sipariş eşleşmedi</div>}</td>
                  <td>{r.channelStatus ?? '—'}</td>
                  <td>{r.receivedLineCount}/{r.lineCount}</td>
                  <td>{r.lastDecision ? `${decisionLabels[r.lastDecision] ?? r.lastDecision} · ${commandStateLabels[r.lastDecisionState ?? ''] ?? r.lastDecisionState ?? ''}` : '—'}</td>
                </tr>
              ))}
              {list.data?.items.length === 0 && <tr><td colSpan={7} className="empty">İade talebi yok.</td></tr>}
            </tbody>
          </table>
          {list.data && list.data.total > list.data.pageSize && (
            <div className="filters">
              <button disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Önceki</button>
              <span className="muted">{page}</span>
              <button disabled={page * list.data.pageSize >= list.data.total} onClick={() => setPage(page + 1)}>Sonraki ›</button>
            </div>
          )}
        </div>
        {selected && <ReturnDetailPanel id={selected} onChanged={list.reload} onClose={() => setSelected(null)} onNotice={setNotice} />}
      </div>
    </>
  );
}

function ReturnDetailPanel({ id, onChanged, onClose, onNotice }: { id: number; onChanged: () => void; onClose: () => void; onNotice: (m: string) => void }) {
  const { can } = useAuth();
  const detail = useLoad(() => get<ReturnDetail>(`/api/returns/${id}`), [id]);
  const d = detail.data;
  const reasons = useLoad(
    () => d ? get<ChannelReferenceView>(`/api/channels/${d.summary.channelId}/references?type=IADE_RED_GEREKCE`) : Promise.resolve(null),
    [d?.summary.channelId]);
  const [reason, setReason] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function act(run: () => Promise<unknown>, message: string) {
    setBusy(true);
    setError(null);
    try {
      await run();
      onNotice(message);
      detail.reload();
      onChanged();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const rule = (decision: string) => d?.rules.find(r => r.decision === decision)?.rule;
  const rejectRule = rule('RED');
  const reasonOptions = (reasons.data?.items ?? []).filter(r => !rejectRule?.allowedReasons || rejectRule.allowedReasons.includes(r.code));

  function decide(decision: string, label: string) {
    if (!window.confirm(`${d?.summary.returnNo}: "${label}" kararı kanala gönderilecek. Onaylıyor musunuz?`)) return;
    void act(() => post(`/api/returns/${id}/decision`, { decision, reasonCode: decision === 'RED' ? reason || null : null, description: description || null }),
      `Karar kaydedildi: ${label}. Kanal işlemleri kuyruğundan gönderilecek.`);
  }

  return (
    <aside className="detail" data-testid="return-detail">
      <header className="card-header">
        <h2>{d ? d.summary.returnNo : 'İade talebi'}</h2>
        <button className="link" onClick={onClose} aria-label="Kapat">✕</button>
      </header>
      <ErrorBox error={detail.error ?? error} />
      {d && (
        <>
          <p><ChannelBadge code={d.summary.channelCode} /> Sipariş {d.summary.channelOrderNo} · {d.summary.channelStatus ?? '—'}</p>
          {d.summary.reason && <p className="muted">Gerekçe: {d.summary.reason}</p>}
          <h3>Satırlar</h3>
          <table className="table">
            <thead><tr><th>SKU / barkod</th><th>Adet</th><th>Depo</th></tr></thead>
            <tbody>
              {d.lines.map(l => (
                <tr key={l.id}>
                  <td>{l.skuCode ?? '—'}<div className="muted small">{l.barcode ?? l.channelProductCode ?? l.lineKey}</div></td>
                  <td>{l.quantity}</td>
                  <td className="wrap">
                    {l.received ? 'Depoya girdi'
                      : can('stockWrite') && !l.receiveProblem
                        ? <button disabled={busy} onClick={() => act(() => post(`/api/returns/${id}/lines/${l.id}/receive`), 'İade satırı depoya girildi.')}>Depoya ulaştı</button>
                        : <span className="muted small">{l.receiveProblem ?? '—'}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {can('admin') && (
            <>
              <h3>Karar (kanala bildirilir)</h3>
              <div className="actions">
                {rule('ONAY')?.supported && <button disabled={busy} className="primary" onClick={() => decide('ONAY', 'Onay')}>Onayla</button>}
                {rule('TESLIM_ALINDI')?.supported && <button disabled={busy} onClick={() => decide('TESLIM_ALINDI', 'Teslim alındı')}>Teslim alındı</button>}
              </div>
              {rejectRule?.supported ? (
                <div className="form-row">
                  <select aria-label="Red gerekçesi" value={reason} onChange={e => setReason(e.target.value)}>
                    <option value="">Red gerekçesi seçin</option>
                    {reasonOptions.map(r => <option key={r.code} value={r.code}>{r.name}</option>)}
                  </select>
                  <input placeholder="Açıklama (en fazla 500)" aria-label="Red açıklaması" maxLength={500} value={description} onChange={e => setDescription(e.target.value)} />
                  <button disabled={busy || !reason} className="danger" onClick={() => decide('RED', 'Red')}>Reddet</button>
                </div>
              ) : null}
              {d.rules.filter(r => r.rule.note).map(r => (
                <div key={r.decision} className="muted small">{decisionLabels[r.decision]}: {r.rule.supported ? r.rule.note : `desteklenmiyor — ${r.rule.note}`}</div>
              ))}
            </>
          )}

          <h3>Kararlar</h3>
          <ul className="timeline" data-testid="return-decisions">
            {d.decisions.map(x => (
              <li key={x.id}>
                <strong>{decisionLabels[x.decision] ?? x.decision}</strong>{x.reasonCode ? ` (${x.reasonCode})` : ''} · {commandStateLabels[x.commandState ?? ''] ?? x.commandState ?? ''}
                <span className="muted"> · {formatDateTime(x.atUtc)} · {x.user}</span>
                {x.commandError && <div className="muted small">{x.commandError}</div>}
              </li>
            ))}
            {d.decisions.length === 0 && <li className="muted">Henüz karar yok.</li>}
          </ul>
        </>
      )}
    </aside>
  );
}
