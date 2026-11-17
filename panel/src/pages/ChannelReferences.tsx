import { useState } from 'react';
import { get, post, qs, type Channel, type ChannelReferenceView } from '../api';
import { Card, ErrorBox, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';

const typeLabels: Record<string, string> = {
  KARGO_FIRMASI: 'Kargo firmaları',
  IADE_RED_GEREKCE: 'İade red gerekçeleri'
};

const outcomeLabels: Record<string, string> = {
  GUNCELLENDI: 'Güncellendi',
  DESTEKLENMIYOR: 'Kanalda desteklenmiyor',
  HATA: 'Hata'
};

/**
 * Satıcı Bilgileri (ADR-025): kanalın kargo firması ve iade red gerekçesi listeleri. Salt okuma; "Kanaldan yenile"
 * Hangfire job'ı olarak kuyruğa alınır (yalnızca Yönetici). Kaynak "Doküman" ise liste kanal dokümanındaki sabit tablodur.
 */
export function ChannelReferences({ channels, canRefresh, onNotice }: { channels: Channel[]; canRefresh: boolean; onNotice: (m: string) => void }) {
  const [channelId, setChannelId] = useState('');
  const [type, setType] = useState('KARGO_FIRMASI');
  const [includeInactive, setIncludeInactive] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const selected = channelId || String(channels[0]?.id ?? '');
  const view = useLoad(
    () => selected ? get<ChannelReferenceView>(`/api/channels/${selected}/references${qs({ type, includeInactive: String(includeInactive) })}`) : Promise.resolve(null),
    [selected, type, includeInactive]);

  async function refresh() {
    setError(null);
    try {
      await post(`/api/channels/${selected}/references/refresh`);
      const ch = channels.find(c => String(c.id) === selected);
      onNotice(`${ch ? channelNames[ch.code] ?? ch.name : 'Kanal'} referans verisi çekiliyor. Birkaç saniye sonra "Listeyi yenile" ile güncelleyin.`);
    } catch (err) {
      setError(err);
    }
  }

  const status = view.data?.status;
  return (
    <Card title="Kanal referans verisi (Satıcı Bilgileri)">
      <p className="muted small">
        Sipariş/teslimat ve iade kararı ekranlarında kullanılan listeler. Kanalda artık bulunmayan kayıt silinmez, pasif olur.
      </p>
      <div className="filters">
        <select aria-label="Referans kanalı" value={selected} onChange={e => setChannelId(e.target.value)}>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}{c.active ? '' : ' (pasif)'}</option>)}
        </select>
        <select aria-label="Referans tipi" value={type} onChange={e => setType(e.target.value)}>
          {Object.entries(typeLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <label className="check"><input type="checkbox" checked={includeInactive} onChange={e => setIncludeInactive(e.target.checked)} /> Pasifleri göster</label>
        <button onClick={view.reload}>Listeyi yenile</button>
        {canRefresh && <button className="primary" onClick={refresh} disabled={!selected}>Kanaldan yenile</button>}
      </div>
      <ErrorBox error={error ?? view.error} />
      {status && (
        <p className="muted small" data-testid="reference-status">
          Son çekme: {formatDateTime(status.lastAttemptUtc)} · son başarılı: {formatDateTime(status.lastSuccessUtc)}
          {status.lastError && <span style={{ color: "var(--danger)" }}> · Hata: {status.lastError}</span>}
          {status.results.map(r => (
            <span key={r.type}> · {typeLabels[r.type] ?? r.type}: {outcomeLabels[r.outcome] ?? r.outcome}
              {r.outcome === 'GUNCELLENDI' ? ` (${r.count})` : ''}{r.message ? ` — ${r.message}` : ''}</span>
          ))}
        </p>
      )}
      <table className="table" data-testid="reference-table">
        <thead><tr><th>Kod</th><th>Ad</th><th>Kaynak</th><th>Çekilme</th><th>Durum</th></tr></thead>
        <tbody>
          {(view.data?.items ?? []).map(r => (
            <tr key={r.id} className={r.active ? undefined : 'muted'}>
              <td><code>{r.code}</code></td>
              <td className="wrap">{r.name}</td>
              <td>{r.source === 'DOKUMAN' ? 'Kanal dokümanı' : 'Kanal API'}</td>
              <td>{formatDateTime(r.fetchedAtUtc)}</td>
              <td>{r.active ? 'Aktif' : 'Pasif'}</td>
            </tr>
          ))}
          {view.data && view.data.items.length === 0 && (
            <tr><td colSpan={5} className="empty">Kayıt yok.{canRefresh ? ' "Kanaldan yenile" ile çekin.' : ''}</td></tr>
          )}
        </tbody>
      </table>
    </Card>
  );
}
