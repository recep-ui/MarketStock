import { useMemo, useState } from 'react';
import { get, post, type StockApprovalItem, type StockApprovalResult } from '../api';
import { useAuth } from '../auth';
import { ExportButton } from '../components/ExportButton';
import { Card, ChannelBadge, ErrorBox, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';

/*
  Onay bekleyen stok gönderimleri (ADR-048). Stok yeteneği "Onaylı" olan kanallarda hiçbir stok değişikliği kendiliğinden
  pazaryerine gitmez: hangi ürünün hangi kanalda kaça değişeceği burada görülür, kanal kutucuklarıyla ve satır seçimiyle
  onaylanır ya da reddedilir. Onaylanan değer saklanır; gönderimden önce stok yine değişirse kayıt tekrar buraya düşer.
*/

/** Kanalda şu an görünen (yoksa son gönderilen) değere göre fark. */
function baseOf(r: StockApprovalItem): number | null {
  return r.channelStock ?? r.lastSent;
}

function DiffCell({ row }: { row: StockApprovalItem }) {
  const base = baseOf(row);
  if (base == null) return <span className="muted">ilk gönderim</span>;
  const d = row.newValue - base;
  if (d === 0) return <span className="muted">değişmiyor</span>;
  return <strong className={d < 0 ? 'danger-text' : 'ok-text'}>{d > 0 ? `+${d}` : d}</strong>;
}

export function StockApprovals({ onChanged }: { onChanged: (message: string) => void }) {
  const { can } = useAuth();
  const items = useLoad(() => get<StockApprovalItem[]>('/api/sync/stock-approvals'), []);
  const [unticked, setUnticked] = useState<Set<string>>(new Set());   // kutucuğu kaldırılan kanallar
  const [deselected, setDeselected] = useState<Set<number>>(new Set()); // seçimi kaldırılan satırlar
  const [search, setSearch] = useState('');
  const [onlyDecrease, setOnlyDecrease] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [skipped, setSkipped] = useState<StockApprovalResult['skipped']>([]);
  const [busy, setBusy] = useState(false);

  const all = items.data ?? [];
  const channels = useMemo(() => {
    const map = new Map<string, number>();
    for (const r of all) map.set(r.channelCode, (map.get(r.channelCode) ?? 0) + 1);
    return [...map.entries()];
  }, [all]);

  const visible = all.filter(r => !unticked.has(r.channelCode)
    && (!search || `${r.skuCode} ${r.productName} ${r.channelBarcode ?? ''}`.toLocaleUpperCase('tr').includes(search.trim().toLocaleUpperCase('tr')))
    && (!onlyDecrease || (baseOf(r) != null && r.newValue < baseOf(r)!)));
  const chosen = visible.filter(r => !deselected.has(r.queueId));
  const shown = visible.slice(0, 500);

  function toggleChannel(code: string) {
    setUnticked(prev => {
      const next = new Set(prev);
      if (next.has(code)) next.delete(code); else next.add(code);
      return next;
    });
  }

  function toggleRow(id: number) {
    setDeselected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  function setAllVisible(selected: boolean) {
    setDeselected(prev => {
      const next = new Set(prev);
      for (const r of visible) { if (selected) next.delete(r.queueId); else next.add(r.queueId); }
      return next;
    });
  }

  /** Onay penceresi için kanal bazında özet: kaç ürün, kaçı azalıyor/artıyor/ilk gönderim. */
  function summary(rows: StockApprovalItem[]): string {
    const byChannel = new Map<string, StockApprovalItem[]>();
    for (const r of rows) byChannel.set(r.channelCode, [...(byChannel.get(r.channelCode) ?? []), r]);
    return [...byChannel.entries()].map(([code, list]) => {
      const down = list.filter(r => baseOf(r) != null && r.newValue < baseOf(r)!).length;
      const up = list.filter(r => baseOf(r) != null && r.newValue > baseOf(r)!).length;
      const first = list.filter(r => baseOf(r) == null).length;
      const zero = list.filter(r => r.newValue === 0).length;
      return `• ${channelNames[code] ?? code}: ${list.length} ürün — ${down} azalış, ${up} artış${first ? `, ${first} ilk gönderim` : ''}${zero ? ` (${zero} ürün 0'a iniyor)` : ''}`;
    }).join('\n');
  }

  async function approve() {
    if (chosen.length === 0) return;
    if (!window.confirm(`Aşağıdaki stoklar pazaryerlerine GÖNDERİLECEK:\n\n${summary(chosen)}\n\nOnaylıyor musunuz?`)) return;
    setBusy(true);
    setError(null);
    try {
      const r = await post<StockApprovalResult>('/api/sync/stock-approvals/approve', { items: chosen.map(c => ({ queueId: c.queueId, value: c.newValue })) });
      setSkipped(r.skipped);
      onChanged(`${r.approved} stok gönderimi onaylandı; birkaç saniye içinde kanala gider.${r.skipped.length ? ` ${r.skipped.length} kayıt atlandı.` : ''}`);
      setDeselected(new Set());
      items.reload();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  async function reject() {
    if (chosen.length === 0) return;
    if (!window.confirm(`Seçilen ${chosen.length} stok gönderimi REDDEDİLECEK; pazaryerine gönderilmeyecek.\n\n${summary(chosen)}\n\nStok yeniden değişirse yeni değer tekrar onaya düşer. Devam edilsin mi?`)) return;
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ rejected: number }>('/api/sync/stock-approvals/reject', { queueIds: chosen.map(c => c.queueId) });
      onChanged(`${r.rejected} stok gönderimi reddedildi.`);
      setDeselected(new Set());
      items.reload();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card title={`Onay bekleyen stok gönderimleri (${all.length})`} actions={all.length > 0 && (
      <ExportButton<StockApprovalItem> fileName="onay-bekleyen-stok" sheetName="Onay bekleyen stok" load={() => Promise.resolve(visible)} columns={[
        { header: 'Ürün', value: r => r.productName },
        { header: 'SKU', value: r => r.skuCode },
        { header: 'Kanal', value: r => channelNames[r.channelCode] ?? r.channelCode },
        { header: 'Kanal barkodu', value: r => r.channelBarcode },
        { header: 'Kanal ürün kodu', value: r => r.channelProductCode },
        { header: 'Kanalda şu an', kind: 'number', value: r => r.channelStock },
        { header: 'Son gönderilen', kind: 'number', value: r => r.lastSent },
        { header: 'Gönderilecek', kind: 'number', value: r => r.newValue },
        { header: 'Oluşma', kind: 'datetime', value: r => r.createdUtc }
      ]} />
    )}>
      <p className="muted small">
        Stok gönderimi "Onaylı" olan kanallarda stok değişiklikleri pazaryerine siz onaylamadan gitmez. Göndermek istediğiniz kanalları
        işaretleyin, satırları kontrol edin. "Kanalda şu an": son mutabakatta pazaryerinde görülen stok. Kanal ayarı: Ayarlar → Kanallar → Stok gönderimi.
      </p>
      <ErrorBox error={error ?? items.error} onRetry={items.reload} />
      {all.length === 0 && !items.loading && <p className="muted" data-testid="approvals-empty">Onay bekleyen stok gönderimi yok.</p>}
      {all.length > 0 && (
        <>
          <div className="filters" data-testid="approval-channels">
            {channels.map(([code, count]) => (
              <label key={code} className="check">
                <input type="checkbox" checked={!unticked.has(code)} onChange={() => toggleChannel(code)} aria-label={`${channelNames[code] ?? code} kanalı`} />
                <ChannelBadge code={code} /> {count}
              </label>
            ))}
            <input placeholder="SKU / ürün / barkod ara" value={search} onChange={e => setSearch(e.target.value)} aria-label="Onay ara" />
            <label className="check"><input type="checkbox" checked={onlyDecrease} onChange={e => setOnlyDecrease(e.target.checked)} /> Yalnızca azalanlar</label>
          </div>
          {can('admin') && (
            <div className="filters">
              <button className="primary" disabled={busy || chosen.length === 0} onClick={approve} data-testid="approve-stock">
                Seçilenleri kanala gönder ({chosen.length})
              </button>
              <button disabled={busy || chosen.length === 0} onClick={reject} data-testid="reject-stock">Seçilenleri reddet</button>
              <button onClick={() => setAllVisible(true)}>Tümünü seç</button>
              <button onClick={() => setAllVisible(false)}>Seçimi kaldır</button>
              <button onClick={items.reload}>Yenile</button>
            </div>
          )}
          {skipped.length > 0 && (
            <div className="alert alert-warn">
              {skipped.length} kayıt onaylanmadı: {skipped.slice(0, 3).map(s => s.reason).join(' · ')}{skipped.length > 3 ? ' …' : ''}
            </div>
          )}
          <div className="table-wrap">
            <table className="table" data-testid="stock-approvals">
              <thead>
                <tr>
                  <th />
                  <th>Ürün</th><th>SKU</th><th>Kanal</th><th>Kanalda şu an</th><th>Son gönderilen</th><th>Gönderilecek</th><th>Fark</th><th>Oluşma</th>
                </tr>
              </thead>
              <tbody>
                {shown.map(r => (
                  <tr key={r.queueId} data-testid="approval-row" className={baseOf(r) != null && r.newValue < baseOf(r)! ? 'row-warn' : undefined}>
                    <td><input type="checkbox" checked={!deselected.has(r.queueId)} onChange={() => toggleRow(r.queueId)} aria-label={`${r.skuCode} seç`} /></td>
                    <td className="wrap-cell"><div className="with-thumb"><Thumb url={r.imageUrl} alt={r.productName} size={40} /><div>{r.productName}</div></div></td>
                    <td>{r.skuCode}<div className="muted small">{r.channelBarcode ?? r.channelProductCode}</div></td>
                    <td><ChannelBadge code={r.channelCode} /></td>
                    <td title={r.channelStockAtUtc ? `Çekim: ${formatDateTime(r.channelStockAtUtc)}` : 'Mutabakat yapılmamış'}>{r.channelStock ?? <span className="muted">—</span>}</td>
                    <td>{r.lastSent ?? <span className="muted">—</span>}</td>
                    <td><strong>{r.newValue}</strong></td>
                    <td><DiffCell row={r} /></td>
                    <td className="muted small">{formatDateTime(r.createdUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {visible.length > shown.length && <p className="muted small">İlk {shown.length} satır gösteriliyor; seçim görünmeyenler dahil {chosen.length} kaydı kapsar. Aramayla daraltabilirsiniz.</p>}
        </>
      )}
    </Card>
  );
}
