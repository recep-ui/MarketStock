import { useState } from 'react';
import { ApiError, get, post, put, qs, type Channel, type ChannelCapability, type ChannelCommand, type OrderNotificationRow, type OrderNotifyResult, type Paged, type PricePreview } from '../api';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ErrorBox, useLoad } from '../components/ui';
import { channelNames, formatDateTime, statusLabels } from '../format';

/* Kanala yazma (ADR-026): yetenekler, sipariş bildirimi ve kanal işlemleri kuyruğu. Gerçek kanala yazma yalnızca
   yetenek ACIK/DENEME, kanal aktif ve global yazma kilidi kapalıyken gider (deneme modunda kilit hep açıktır). */

export const capabilityLabels: Record<string, string> = {
  STOK: 'Stok gönderimi',
  SIPARIS_DURUM: 'Sipariş durumu / kargo bildirimi',
  IADE_KARAR: 'İade onay/red bildirimi',
  FIYAT: 'Fiyat gönderimi',
  SORU_CEVAP: 'Soru cevabı'
};

const stateLabels: Record<string, string> = { KAPALI: 'Kapalı', DENEME: 'Deneme (tek tek onay)', ACIK: 'Açık' };

export const commandStateLabels: Record<string, string> = {
  ONAY_BEKLIYOR: 'Onay bekliyor',
  BEKLIYOR: 'Kuyrukta',
  GONDERILDI: 'Gönderildi',
  HATA: 'Hata',
  IPTAL: 'İptal'
};

const outcomeLabels: Record<string, string> = {
  KUYRUGA_ALINDI: 'Kuyruğa alındı',
  ONAY_BEKLIYOR: 'Yönetici onayı bekliyor',
  ZATEN_VAR: 'Zaten var',
  GEREKMIYOR: 'Bildirim gerekmiyor',
  DESTEKLENMIYOR: 'Kanalda desteklenmiyor',
  REDDEDILDI: 'Reddedildi'
};

// ------------------------------------------------------------------ Ayarlar: yetenekler + bildirim modu

export function ChannelWriteSettings({ channels, editable, onNotice }: { channels: Channel[]; editable: boolean; onNotice: (m: string) => void }) {
  const [channelId, setChannelId] = useState('');
  const [error, setError] = useState<unknown>(null);
  const selected = channelId || String(channels[0]?.id ?? '');
  const caps = useLoad(() => selected ? get<ChannelCapability[]>(`/api/channels/${selected}/capabilities`) : Promise.resolve(null), [selected]);
  const mode = useLoad(() => selected ? get<{ mode: string }>(`/api/channels/${selected}/notification-mode`) : Promise.resolve(null), [selected]);

  async function setCap(capability: string, state: string) {
    setError(null);
    if (capability === 'FIYAT' && state !== 'KAPALI') {
      // ADR-028: açmadan önce önizleme ("N ilanın fiyatı değişecek"); KDV doğrulanmamış kanalda açılamaz.
      try {
        const p = await get<PricePreview>(`/api/prices/channels/${selected}/preview?onlyChanges=true`);
        if (!p.canEnable) { setError(new ApiError(400, p.blockReason ?? 'Bu kanalda fiyat gönderimi açılamaz.')); caps.reload(); return; }
        if (!window.confirm(`${p.changing} ilanın fiyatı değişecek (${p.firstSend} ilk gönderim), ${p.needsApproval} onay bekleyecek, ${p.blocked} gönderilemez.
`
          + `Ayrıntılı liste: Fiyat ekranı → önizleme.
Fiyat gönderimi "${stateLabels[state]}" yapılsın mı?`)) { caps.reload(); return; }
      } catch (err) {
        setError(err);
        return;
      }
    } else if (state !== 'KAPALI' && !window.confirm(`${capabilityLabels[capability] ?? capability}: "${stateLabels[state]}" yapılsın mı? Gerçek modlu kanalda kanala yazma başlar.`)) return;
    try {
      await put(`/api/channels/${selected}/capabilities/${capability}`, { state });
      onNotice(`${capabilityLabels[capability] ?? capability} → ${stateLabels[state]}`);
      caps.reload();
    } catch (err) {
      setError(err);
    }
  }

  async function setMode(value: string) {
    setError(null);
    try {
      await put(`/api/channels/${selected}/notification-mode`, { mode: value });
      onNotice(`Sipariş bildirim modu: ${value === 'OTOMATIK' ? 'Otomatik' : 'Onayla ve gönder'}`);
      mode.reload();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kanala yazma yetenekleri">
      <p className="muted small">
        Varsayılan KAPALI. "Deneme" durumunda her kayıt Senkron Merkezi → Kanal işlemleri'nden Yönetici onayıyla tek tek gider.
        Deneme modunda (scripts/deneme-modu.ps1) global yazma kilidi açıktır: gerçek kanala hiçbir yazma gitmez.
      </p>
      <div className="filters">
        <select aria-label="Yetenek kanalı" value={selected} onChange={e => setChannelId(e.target.value)}>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}{c.active ? '' : ' (pasif)'}</option>)}
        </select>
        <label>
          Sipariş bildirim modu{' '}
          <select aria-label="Bildirim modu" value={mode.data?.mode ?? 'ONAYLA_GONDER'} disabled={!editable} onChange={e => setMode(e.target.value)}>
            <option value="ONAYLA_GONDER">Onayla ve gönder</option>
            <option value="OTOMATIK">Otomatik</option>
          </select>
        </label>
      </div>
      <ErrorBox error={error ?? caps.error ?? mode.error} />
      <table className="table" data-testid="capability-table">
        <thead><tr><th>Yetenek</th><th>Durum</th><th>Son değiştiren</th></tr></thead>
        <tbody>
          {(caps.data ?? []).map(c => (
            <tr key={c.capability}>
              <td>{capabilityLabels[c.capability] ?? c.capability}</td>
              <td>
                <select aria-label={`Yetenek ${c.capability}`} value={c.state} disabled={!editable} onChange={e => setCap(c.capability, e.target.value)}>
                  {Object.entries(stateLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </td>
              <td className="muted">{c.changedBy ? `${c.changedBy} · ${formatDateTime(c.changedAtUtc)}` : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  );
}

// ------------------------------------------------------------------ Siparişler: kanala bildirim

export function OrderNotifications({ channels, onChanged, refreshKey }: { channels: Channel[]; onChanged: () => void; refreshKey?: unknown }) {
  const { can } = useAuth();
  const [channelId, setChannelId] = useState('');
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState('BILDIRILMEDI');
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [ownCargo, setOwnCargo] = useState(false);
  const [cargo, setCargo] = useState({ company: '', trackingNumber: '', trackingUrl: '' });
  const [confirmed, setConfirmed] = useState(false);
  const [result, setResult] = useState<OrderNotifyResult | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const rows = useLoad(() => get<Paged<OrderNotificationRow>>(`/api/orders/notifications${qs({ channelId: channelId || undefined, filter, search: search || undefined, pageSize: 200 })}`),
    [channelId, filter, search, refreshKey]);
  const selectedRows = (rows.data?.items ?? []).filter(r => selected.has(r.orderId));
  const needsCargo = selectedRows.some(r => r.status === 'Kargoda');

  async function notify() {
    setBusy(true);
    setError(null);
    try {
      const r = await post<OrderNotifyResult>('/api/orders/notifications', {
        orderIds: [...selected], ownCargo,
        cargo: ownCargo ? { company: cargo.company || null, trackingNumber: cargo.trackingNumber || null, trackingUrl: cargo.trackingUrl || null } : null
      });
      setResult(r);
      setSelected(new Set());
      setConfirmed(false);
      rows.reload();
      onChanged();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  function toggle(id: number) {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  return (
    <Card title="Kanala bildirim (sipariş durumu / kargo)">
      <p className="muted small">
        Panelde Hazırlanıyor veya Kargoda yapılan siparişler kanala ayrıca bildirilir. Kanaldan gelen durumlar zaten kanaldadır.
        Kanalın anlaşmalı kargosuyla gönderimde "Kargoda" bildirimi gerekmez.
      </p>
      <div className="filters">
        <select aria-label="Bildirim kanalı" value={channelId} onChange={e => setChannelId(e.target.value)}>
          <option value="">Tüm kanallar</option>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
        </select>
        <select aria-label="Bildirim filtresi" value={filter} onChange={e => { setFilter(e.target.value); setSelected(new Set()); }}>
          <option value="BILDIRILMEDI">Kanala bildirilmedi</option>
          <option value="BEKLEYEN">Kuyrukta / onay bekliyor</option>
          <option value="HATA">Hatalı</option>
          <option value="TUMU">Tümü (Hazırlanıyor + Kargoda)</option>
        </select>
        <input placeholder="Sipariş no" aria-label="Bildirim sipariş no" value={search} onChange={e => setSearch(e.target.value)} />
      </div>
      <ErrorBox error={error ?? rows.error} />
      <table className="table" data-testid="notification-table">
        <thead><tr>{can('orderWrite') && <th />}<th>Kanal</th><th>Sipariş no</th><th>Durum</th><th>Kaynak</th><th>Bildirim</th><th>Tarih</th></tr></thead>
        <tbody>
          {(rows.data?.items ?? []).map(r => (
            <tr key={r.orderId} data-testid="notification-row" className={r.notificationState === 'HATA' ? 'row-danger' : undefined}>
              {can('orderWrite') && <td><input type="checkbox" aria-label={`Bildir ${r.channelOrderNo}`} checked={selected.has(r.orderId)} onChange={() => toggle(r.orderId)} /></td>}
              <td><ChannelBadge code={r.channelCode} /></td>
              <td>{r.channelOrderNo}{!r.hasChannelIds && <div className="muted small">kanal kimliği eksik</div>}</td>
              <td>{statusLabels[r.status]}</td>
              <td className="muted">{r.statusSource === 'PANEL' ? 'Panel' : r.statusSource}</td>
              <td className="wrap">{r.notificationState ? commandStateLabels[r.notificationState] ?? r.notificationState : 'Bildirilmedi'}
                {r.error && <div className="muted small">{r.error}</div>}</td>
              <td>{formatDateTime(r.orderDateUtc)}</td>
            </tr>
          ))}
          {rows.data?.items.length === 0 && <tr><td colSpan={7} className="empty">Kayıt yok.</td></tr>}
        </tbody>
      </table>
      {can('orderWrite') && selected.size > 0 && (
        <div className="form-row" data-testid="notify-form">
          {needsCargo && (
            <>
              <label className="check"><input type="checkbox" checked={ownCargo} onChange={e => setOwnCargo(e.target.checked)} /> Kendi kargomuzla gönderildi</label>
              {ownCargo && (
                <>
                  <input placeholder="Kargo firması kodu" aria-label="Kargo firması" value={cargo.company} onChange={e => setCargo({ ...cargo, company: e.target.value })} />
                  <input placeholder="Takip numarası" aria-label="Takip numarası" value={cargo.trackingNumber} onChange={e => setCargo({ ...cargo, trackingNumber: e.target.value })} />
                  <input placeholder="Takip linki (https://...)" aria-label="Takip linki" value={cargo.trackingUrl} onChange={e => setCargo({ ...cargo, trackingUrl: e.target.value })} />
                </>
              )}
            </>
          )}
          <label className="check"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} /> Seçilen {selected.size} sipariş kanala bildirilecek</label>
          <button className="primary" disabled={busy || !confirmed} onClick={notify} data-testid="notify-submit">Kanala bildir</button>
        </div>
      )}
      {result && (
        <table className="table" data-testid="notify-result">
          <thead><tr><th>Sipariş</th><th>Sonuç</th><th>Açıklama</th></tr></thead>
          <tbody>
            {result.items.map(i => (
              <tr key={i.orderId} className={i.outcome === 'REDDEDILDI' || i.outcome === 'DESTEKLENMIYOR' ? 'row-danger' : undefined}>
                <td>{i.channelOrderNo ?? i.orderId}</td>
                <td>{outcomeLabels[i.outcome] ?? i.outcome}</td>
                <td className="wrap">{i.message ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

/** Sipariş detayında kanala bildirim geçmişi. */
export function OrderNotificationHistory({ orderId }: { orderId: number }) {
  const list = useLoad(() => get<ChannelCommand[]>(`/api/orders/${orderId}/notifications`), [orderId]);
  if (!list.data || list.data.length === 0) return null;
  return (
    <>
      <h3>Kanala bildirim</h3>
      <ul className="timeline" data-testid="order-notifications">
        {list.data.map(c => (
          <li key={c.id}>
            <strong>{c.targetKey.split(':').pop()}</strong> · {commandStateLabels[c.state] ?? c.state}
            <span className="muted"> · {formatDateTime(c.sentAtUtc ?? c.createdAtUtc)}{c.createdBy ? ` · ${c.createdBy}` : ''}</span>
            {c.error && <div className="muted small">{c.error}</div>}
          </li>
        ))}
      </ul>
    </>
  );
}

// ------------------------------------------------------------------ Senkron Merkezi: kanal işlemleri

export function ChannelCommands({ channelId, onNotice }: { channelId: string; onNotice: (m: string) => void }) {
  const { can } = useAuth();
  const [state, setState] = useState('');
  const [error, setError] = useState<unknown>(null);
  const list = useLoad(() => get<Paged<ChannelCommand>>(`/api/channel-commands${qs({ channelId: channelId || undefined, state: state || undefined, pageSize: 100 })}`),
    [channelId, state]);

  async function act(id: number, action: 'approve' | 'retry' | 'cancel', label: string) {
    if (action !== 'retry' && !window.confirm(`Kayıt #${id}: ${label}?`)) return;
    setError(null);
    try {
      await post(`/api/channel-commands/${id}/${action}`);
      onNotice(`Kayıt #${id}: ${label} yapıldı.`);
      list.reload();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kanal işlemleri (sipariş bildirimi, iade kararı, soru cevabı)">
      <div className="filters">
        <select aria-label="İşlem durumu" value={state} onChange={e => setState(e.target.value)}>
          <option value="">Tüm durumlar</option>
          {Object.entries(commandStateLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <button onClick={list.reload}>Yenile</button>
      </div>
      <ErrorBox error={error ?? list.error} />
      <table className="table" data-testid="command-table">
        <thead><tr><th>#</th><th>Kanal</th><th>Tip</th><th>Hedef</th><th>Durum</th><th>Deneme</th><th>Hata</th><th>Oluşturan</th>{can('admin') && <th />}</tr></thead>
        <tbody>
          {(list.data?.items ?? []).map(c => (
            <tr key={c.id} data-testid="command-row" className={c.state === 'HATA' ? 'row-danger' : undefined}>
              <td>{c.id}</td>
              <td><ChannelBadge code={c.channelCode} /></td>
              <td>{capabilityLabels[c.type] ?? c.type}</td>
              <td><code title={c.payloadJson}>{c.targetKey}</code></td>
              <td>{commandStateLabels[c.state] ?? c.state}</td>
              <td>{c.attempts}</td>
              <td className="wrap">{c.error ?? ''}</td>
              <td className="muted">{c.createdBy ?? ''} · {formatDateTime(c.createdAtUtc)}</td>
              {can('admin') && (
                <td>
                  {c.state === 'ONAY_BEKLIYOR' && <button className="small primary" onClick={() => act(c.id, 'approve', 'kanala gönderimi onayla')}>Onayla</button>}
                  {c.state === 'HATA' && <button className="small" onClick={() => act(c.id, 'retry', 'yeniden dene')}>Yeniden dene</button>}
                  {['ONAY_BEKLIYOR', 'BEKLIYOR', 'HATA'].includes(c.state) && <button className="small" onClick={() => act(c.id, 'cancel', 'iptal et')}>İptal</button>}
                </td>
              )}
            </tr>
          ))}
          {list.data?.items.length === 0 && <tr><td colSpan={9} className="empty">Kayıt yok.</td></tr>}
        </tbody>
      </table>
    </Card>
  );
}
