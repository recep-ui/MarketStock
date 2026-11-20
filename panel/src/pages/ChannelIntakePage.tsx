import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get, post, qs, type Channel, type ChannelStatus, type ChannelStockList, type IntakeApplyResult, type IntakePreview, type IntakePreviewRow } from '../api';
import { ChannelDataSourceNote } from '../components/ChannelDataSource';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ErrorBox, Notice, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';

const flagLabels: Record<string, string> = {
  KANALDA_DEGER_YOK: 'Kaynak kanalda değer yok',
  ILANLAR_FARKLI: 'Kanalın ilanları farklı değer gösteriyor (en düşüğü alındı)',
  PAY_100_DEGIL: 'Kanal payı %100 değil; değer geri hesaplanamaz',
  ESIK_BELIRSIZ: 'Kanal 0 gösteriyor ama düşük stok eşiği var; gerçek değer belirsiz',
  UST_LIMITTE: 'Değer kanal üst limitinde; gerçek değer daha büyük olabilir',
  ESKI_CEKIM: 'Kanal değeri 24 saatten eski',
  ILAN_DEGERI_YOK: 'İlanlardan bazıları kanal stok sorgusunda dönmedi (kara liste / arşiv vb.)',
  DEFTER_SONRADAN_DEGISTI: 'Kanal değeri çekildikten sonra defterde giriş/sayım var',
  SENKRON_BEKLIYOR: 'Bu ilan için gönderim bekliyor; kanal değeri değişmek üzere',
  FARK_YOK: 'Defterle aynı',
  CEKIMDEN_SONRA_SIPARIS: 'Çekimden sonra sipariş/iptal var; hedeften düşüldü'
};

/**
 * Kanal stoğunu deftere al (ADR-024). Önizleme hiçbir şey yazmaz; uygulama Yönetici onayıyla SAYIM (kaynak KANAL_ALIM) olarak yazar.
 * Yeni Fiziksel = hedef satılabilir + Rezerve + Güvenlik stoğu; önizlemeden sonraki siparişler uygulamada hedeften düşülür.
 */
export function ChannelIntakePage() {
  const { can } = useAuth();
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const [channelId, setChannelId] = useState('');
  const [onlyDifferences, setOnlyDifferences] = useState(true);
  const [preview, setPreview] = useState<IntakePreview | null>(null);
  const [targets, setTargets] = useState<Record<number, string>>({});
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [result, setResult] = useState<IntakeApplyResult | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const selectedChannel = channelId || String(channels.data?.find(c => c.active)?.id ?? channels.data?.[0]?.id ?? '');
  // ADR-038: kanalın veri kaynağı (mock / test ortamı) ve eşleşmiş ilan sayısı — boş önizlemenin nedenini söylemek için
  const statuses = useLoad(() => get<ChannelStatus[]>('/api/channels/status'), []);
  const summary = useLoad(
    () => (selectedChannel ? get<ChannelStockList>(`/api/stock-overview/channels/${selectedChannel}?pageSize=1`) : Promise.resolve(null)),
    [selectedChannel]
  );
  const selectedStatus = statuses.data?.find(s => String(s.id) === selectedChannel);
  const noListings = summary.data != null && summary.data.totalListings === 0;

  async function loadPreview() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const p = await get<IntakePreview>(`/api/stock/channel-intake/preview${qs({ sourceChannelId: selectedChannel, onlyDifferences })}`);
      setPreview(p);
      setTargets(Object.fromEntries(p.rows.map(r => [r.skuId, r.proposedTarget?.toString() ?? ''])));
      setSelected(new Set(p.rows.filter(r => r.defaultSelected).map(r => r.skuId)));
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  async function fetchFresh() {
    const ch = channels.data?.find(c => String(c.id) === selectedChannel);
    if (!ch) return;
    setError(null);
    try {
      await post(`/api/sync/reconciliation/${ch.code.toLowerCase()}/run`);
      setNotice(`${channelNames[ch.code] ?? ch.name} kanalından stoklar çekiliyor (mutabakat). Birkaç dakika sonra "Önizle" ile yenileyin.`);
    } catch (err) {
      setError(err);
    }
  }

  async function apply() {
    if (!preview) return;
    const rows = preview.rows
      .filter(r => selected.has(r.skuId) && targets[r.skuId] !== '' && targets[r.skuId] !== undefined)
      .map(r => ({ skuId: r.skuId, targetAvailable: Number(targets[r.skuId]), expectedPhysical: r.physical }));
    if (rows.length === 0) return;
    if (!window.confirm(`${rows.length} SKU'nun fiziksel stoğu kanal değerine göre değiştirilecek (SAYIM, kaynak KANAL_ALIM). Onaylıyor musunuz?`)) return;
    setBusy(true);
    setError(null);
    try {
      const r = await post<IntakeApplyResult>('/api/stock/channel-intake/apply',
        { sourceChannelId: preview.sourceChannelId, previewedAtUtc: preview.previewedAtUtc, rows });
      setResult(r);
      setNotice(`Parti ${r.batchId}: ${r.applied} uygulandı, ${r.unchanged} fark yok, ${r.rejected} reddedildi; ${r.pushQueued} ilan kanallara gönderilmek üzere kuyruğa alındı.`);
      setPreview(null);
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

  if (!can('stockWrite')) {
    return <><h1>Kanal Stoğu → Defter</h1><div className="alert alert-warn">Bu ekran Yönetici ve Depo rollerine açıktır.</div></>;
  }

  return (
    <>
      <h1>Kanal Stoğu → Defter</h1>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <Card title="Kanaldaki stoğu deftere al">
        <p className="muted">
          Kanaldaki değerin doğru olduğu durumlarda (ör. stok kanal panelinden elle güncellenmişse) kullanılır. Kaynak kanalın son mutabakat
          değerleri SKU bazında gösterilir; hedefi düzenleyip onayladığınız satırlar defterde SAYIM hareketi (kaynak KANAL_ALIM, kullanıcı
          adınızla) olarak yazılır ve ardından tüm kanallara gönderilir. Yeni fiziksel = hedef + rezerve + güvenlik stoğu.
        </p>
        <div className="filters">
          <select aria-label="Kaynak kanal" value={selectedChannel} onChange={e => { setChannelId(e.target.value); setPreview(null); }}>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}{c.active ? '' : ' (pasif)'}</option>)}
          </select>
          <label className="check"><input type="checkbox" checked={onlyDifferences} onChange={e => setOnlyDifferences(e.target.checked)} /> Yalnızca farklı olanlar</label>
          <button onClick={fetchFresh} disabled={!can('admin')} title="Mutabakat çalıştırma Yönetici yetkisi ister">Kanaldan şimdi çek</button>
          <button className="primary" disabled={busy || !selectedChannel} onClick={loadPreview}>Önizle</button>
          {preview && can('admin') && (
            <button className="primary" disabled={busy || selected.size === 0} onClick={apply} data-testid="intake-apply">
              Seçilenleri deftere al ({selected.size})
            </button>
          )}
        </div>
        {channels.data?.find(c => String(c.id) === selectedChannel)?.active === false && (
          <div className="alert alert-warn" data-testid="intake-channel-passive">
            Seçilen kanal pasif: kanaldan stok çekilemez (mutabakat çalışmaz). Ayarlar → Kanallar'dan kanalı aktif edin.
          </div>
        )}
        <ChannelDataSourceNote status={selectedStatus} />
        {noListings && summary.data && (
          <div className="alert alert-warn" data-testid="intake-no-listings">
            <strong>{channelNames[summary.data.channelCode] ?? summary.data.channelCode} kanalında eşleşmiş ilan yok.</strong> Kanal stoğu
            yalnızca defterdeki bir SKU'ya bağlı ilanlar için deftere alınabilir; mutabakat çalıştırmak bunu değiştirmez.
            {summary.data.unmatchedCount > 0
              ? <> {summary.data.unmatchedCount} kanal ürünü eşleşme bekliyor: önce <Link to="/eslestirme">Eşleştirme</Link> ekranından SKU'lara bağlayın.</>
              : <> Önce <Link to="/eslestirme">Eşleştirme</Link> ekranından “Kanaldan ürün çek” ile kanal ürünlerini getirip SKU'lara bağlayın.</>}
          </div>
        )}
        <ErrorBox error={error ?? channels.error} />
        {preview && !preview.enabled && <div className="alert alert-error">{preview.disabledReason}</div>}
        {preview?.enabled && (
          <>
            <p className="muted">
              Önizleme: {formatDateTime(preview.previewedAtUtc)} · {preview.rows.length} SKU · düşük stok eşiği {preview.lowStockThreshold}
              {!can('admin') && ' · Uygulama yalnızca Yönetici rolüne açıktır.'}
            </p>
            <div className="table-wrap" style={{ maxHeight: 560, overflow: 'auto' }}>
              <table className="table" data-testid="intake-table">
                <thead>
                  <tr>
                    <th /><th>SKU</th><th>Fiziksel</th><th>Rezerve</th><th>Güvenlik</th><th>Satılabilir</th>
                    <th>Kanallardaki değerler</th><th>Hedef satılabilir</th><th>Yeni fiziksel</th><th>Uyarılar</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.rows.map(r => <IntakeRow key={r.skuId} row={r} selected={selected.has(r.skuId)} onToggle={() => toggle(r.skuId)}
                    target={targets[r.skuId] ?? ''} onTarget={v => setTargets({ ...targets, [r.skuId]: v })} />)}
                  {preview.rows.length === 0 && (
                    <tr><td colSpan={10} className="empty" data-testid="intake-empty">
                      {noListings
                        ? 'Satır yok: bu kanalda eşleşmiş ilan yok (yukarıdaki uyarıya bakın).'
                        : onlyDifferences
                          ? 'Satır yok: defterle farklı değer yok ya da kanal stoğu henüz çekilmedi. "Yalnızca farklı olanlar"ı kaldırın veya "Kanaldan şimdi çek" ile mutabakat çalıştırın.'
                          : 'Satır yok. Önce "Kanaldan şimdi çek" ile mutabakat çalıştırın.'}
                    </td></tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
        {result && (
          <table className="table" data-testid="intake-result">
            <thead><tr><th>SKU</th><th>Sonuç</th><th>Fiziksel</th><th>Satılabilir</th><th>Açıklama</th></tr></thead>
            <tbody>
              {result.rows.map(r => (
                <tr key={r.skuId} className={r.outcome === 'REDDEDILDI' ? 'row-danger' : undefined}>
                  <td>{r.skuCode ?? r.skuId}</td>
                  <td>{r.outcome === 'UYGULANDI' ? 'Uygulandı' : r.outcome === 'FARK_YOK' ? 'Fark yok' : 'Reddedildi'}</td>
                  <td>{r.physical ?? '—'}</td>
                  <td>{r.available ?? '—'}</td>
                  <td className="wrap">{r.message ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function IntakeRow({ row, selected, onToggle, target, onTarget }:
  { row: IntakePreviewRow; selected: boolean; onToggle: () => void; target: string; onTarget: (v: string) => void }) {
  const t = target === '' ? null : Number(target);
  const newPhysical = t === null || Number.isNaN(t) ? null : t + row.reserved + row.safetyStock;
  return (
    <tr data-testid="intake-row" className={row.flags.some(f => f !== 'CEKIMDEN_SONRA_SIPARIS') ? 'row-deviation' : undefined}>
      <td><input type="checkbox" aria-label={`Seç ${row.skuCode}`} checked={selected} onChange={onToggle} /></td>
      <td>{row.skuCode}<div className="muted">{row.productName}</div></td>
      <td>{row.physical}</td>
      <td>{row.reserved}</td>
      <td>{row.safetyStock}</td>
      <td>{row.available}</td>
      <td style={{ whiteSpace: 'normal', minWidth: 200 }}>
        {row.channels.map(c => (
          <div key={c.listingId} title={`${c.channelBarcode ?? c.channelProductCode ?? ''} · beklenen ${c.expected} · ${formatDateTime(c.fetchedAtUtc)}`}>
            <ChannelBadge code={c.channelCode} /> {c.channelStock ?? (c.missingInChannel ? <span className="muted">kanalda yok</span> : '—')}
            {c.channelStock === row.minValue && row.channels.length > 1 && <small className="muted"> (en düşük)</small>}
          </div>
        ))}
        {row.reservedSinceSnapshot !== 0 && <div className="muted">Çekimden sonra net rezervasyon: {row.reservedSinceSnapshot}</div>}
      </td>
      <td>
        <input type="number" min={0} value={target} onChange={e => onTarget(e.target.value)} aria-label={`Hedef ${row.skuCode}`} style={{ width: 90 }} />
      </td>
      <td>{newPhysical ?? '—'}</td>
      <td style={{ whiteSpace: 'normal', minWidth: 220 }}>
        {row.flags.map(f => <div key={f} className="muted">{flagLabels[f] ?? f}</div>)}
      </td>
    </tr>
  );
}
