import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get, qs, type Channel, type ChannelStockList, type ChannelStockListRow } from '../api';
import { Pager } from '../components/DataTable';
import { Card, ChannelLink, ErrorBox, Stat, useLoad } from '../components/ui';
import { channelNames, formatDateTime, formatRelative } from '../format';

const basisLabels: Record<ChannelStockListRow['matchBasis'], { text: string; title: string; tone?: 'ok' }> = {
  BARKOD: { text: '✓ Barkod', title: 'Kanalın barkodu SKU barkoduyla aynı', tone: 'ok' },
  STOK_KODU: { text: '✓ Stok kodu', title: 'Kanalın stok kodu SKU koduyla aynı (barkod farklı ya da bilinmiyor)', tone: 'ok' },
  DIGER: { text: 'Kodla doğrulanamadı', title: 'Barkod ve stok kodu eşleşmeyi doğrulamıyor (elle bağlama, model eşleşmesi veya kanal kodu bilinmiyor)' }
};

const warningLabels: Record<string, string> = {
  BARKOD_FARKLI: 'Barkod farklı', STOK_KODU_FARKLI: 'Stok kodu farklı', RENK_FARKLI: 'Renk farklı',
  BEDEN_FARKLI: 'Beden farklı', AD_BENZEMIYOR: 'Ad benzemiyor'
};

const variantText = (color: string | null, size: string | null) => [color, size].filter(Boolean).join(' / ');

/**
 * Stok Listesi → kanal sekmesi (ADR-035). Kanalın çekilen stok listesi: her satırda kanalın bildirdiği barkod/kod/ad/stok ve
 * bağlandığı SKU yan yana. Kanal bilgisi SKU ile uyuşmuyorsa "Kontrol et" (yanlış eşleştirme olabilir). Eşleştirmeyi değiştirmez.
 */
export function ChannelStockTab({ channel }: { channel: Channel }) {
  const [form, setForm] = useState({ search: '', onlyDeviations: false, onlyReview: false });
  const [applied, setApplied] = useState(form);
  const [page, setPage] = useState(1);
  const name = channelNames[channel.code] ?? channel.name;

  const data = useLoad(
    () => get<ChannelStockList>(`/api/stock-overview/channels/${channel.id}${qs({
      search: applied.search, onlyDeviations: applied.onlyDeviations, onlyReview: applied.onlyReview, page, pageSize: 50
    })}`),
    [channel.id, applied, page]
  );
  const d = data.data;
  const quick = (patch: Partial<typeof form>) => {
    const next = { ...form, ...patch };
    setForm(next);
    setApplied(next);
    setPage(1);
  };

  return (
    <>
      {d && (
        <div className="stats" data-testid="channel-stock-summary">
          <Stat label={`${name} ilanı`} value={d.totalListings} />
          <Stat
            label="Kontrol et (olası yanlış eşleşme)"
            value={<button className="link stat-link" onClick={() => quick({ onlyReview: true, onlyDeviations: false })}>{d.reviewCount}</button>}
            tone={d.reviewCount > 0 ? 'warn' : undefined}
            testId="channel-review-count"
          />
          <Stat
            label="Stok sapması"
            value={<button className="link stat-link" onClick={() => quick({ onlyDeviations: true, onlyReview: false })}>{d.deviationCount}</button>}
            tone={d.deviationCount > 0 ? 'danger' : undefined}
          />
          <Stat label="Kanal stoğu hiç çekilmemiş" value={d.notFetchedCount} />
          <Stat label="Kanalda var, eşleşmemiş" value={<Link to="/eslestirme">{d.unmatchedCount}</Link>} tone={d.unmatchedCount > 0 ? 'warn' : undefined} />
          <Stat
            label="Son stok çekimi"
            value={<span title={formatDateTime(d.lastFetchedUtc)}>{d.lastFetchedUtc ? formatRelative(d.lastFetchedUtc) : '—'}</span>}
          />
        </div>
      )}

      <Card>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(form); }}>
          <input
            placeholder="Kanal barkodu, kanal kodu, kanaldaki ad veya SKU"
            aria-label="Kanal ilanlarında bul"
            value={form.search}
            onChange={e => setForm({ ...form, search: e.target.value })}
          />
          <label className="check">
            <input type="checkbox" checked={form.onlyReview} onChange={e => quick({ onlyReview: e.target.checked })} /> Yalnızca kontrol gerekenler
          </label>
          <label className="check">
            <input type="checkbox" checked={form.onlyDeviations} onChange={e => quick({ onlyDeviations: e.target.checked })} /> Yalnızca stok sapmaları
          </label>
          <button type="submit">Filtrele</button>
        </form>
        <p className="muted small">
          Sol: {name}'in bildirdiği (son katalog çekimi / mutabakat). Sağ: bağlı olduğu SKU ve deftere göre gitmesi gereken stok.
          <strong> Kontrol et</strong>: kanalın bilgisi SKU ile uyuşmuyor, yanlış eşleşme olabilir; düzeltmek için <Link to="/eslestirme">Eşleştirme</Link>.
          Çoklanan ilanlarda barkod farklı olabilir. Bu ekran eşleştirmeyi değiştirmez.
        </p>
        <ErrorBox error={data.error} onRetry={data.reload} />
        {data.loading && !d && <div className="loading">Yükleniyor…</div>}
        {d && (
          <div className="table-wrap">
            <table className="table channel-stock-table" data-testid="channel-stock-table" aria-busy={data.loading}>
              <thead>
                <tr>
                  <th>{name}'de görünen ürün</th>
                  <th className="num">Kanal stoğu</th>
                  <th aria-label="bağlı">→</th>
                  <th>Bağlı SKU (defter)</th>
                  <th className="num">Gitmesi gereken</th>
                  <th>Eşleşme kontrolü</th>
                </tr>
              </thead>
              <tbody>
                {d.rows.items.map(r => (
                  <tr
                    key={r.listingId}
                    data-testid="channel-stock-row"
                    className={r.needsReview ? 'row-review' : r.hasDeviation ? 'row-deviation' : undefined}
                  >
                    <td className="wrap-cell">
                      <div><strong>{r.channel.name ?? <span className="muted">(kanal adı bilinmiyor)</span>}</strong></div>
                      <div className="small muted">
                        {[r.channel.barcode && `Barkod ${r.channel.barcode}`, r.channel.productCode && `Kod ${r.channel.productCode}`,
                          r.channel.stockCode && `Stok kodu ${r.channel.stockCode}`].filter(Boolean).join(' · ')}
                      </div>
                      {(r.channel.variant || variantText(r.channel.color, r.channel.size)) && (
                        <div className="small">{r.channel.variant ?? variantText(r.channel.color, r.channel.size)}</div>
                      )}
                      <div className="small">
                        {r.status === 'PASIF' && <span className="badge">Pasif</span>}
                        <ChannelLink code={channel.code} url={r.productUrl} />
                      </div>
                    </td>
                    <td className="num">
                      {r.channelStock ?? <span className="muted small">çekilmedi</span>}
                      {r.fetchedAtUtc && <div className="small muted" title={formatDateTime(r.fetchedAtUtc)}>{formatRelative(r.fetchedAtUtc)}</div>}
                    </td>
                    <td className="muted" aria-hidden="true">→</td>
                    <td className="wrap-cell">
                      <div><Link to={`/stok/${r.skuId}`}>{r.skuCode}</Link></div>
                      <div className="small">{r.productName}</div>
                      <div className="small muted">
                        {[variantText(r.color, r.size), r.skuBarcode && `Barkod ${r.skuBarcode}`].filter(Boolean).join(' · ')}
                      </div>
                    </td>
                    <td className="num">
                      {r.expected}
                      {r.hasDeviation && <div className="small cell-deviation">sapma: kanalda {r.channelStock}</div>}
                    </td>
                    <td className="wrap-cell">
                      <span className={`badge${basisLabels[r.matchBasis].tone ? ' ' + basisLabels[r.matchBasis].tone : ''}`} title={basisLabels[r.matchBasis].title}>
                        {basisLabels[r.matchBasis].text}
                      </span>
                      {r.warnings.length > 0 && (
                        <ul className="match-warnings" aria-label="Kontrol et">
                          {r.warnings.map(w => (
                            <li key={w.code} title={w.message}>
                              <span aria-hidden="true">⚠ </span><strong>{warningLabels[w.code] ?? w.code}</strong>
                              <span className="small">: {w.message.includes(': ') ? w.message.slice(w.message.indexOf(': ') + 2) : w.message}</span>
                            </li>
                          ))}
                        </ul>
                      )}
                    </td>
                  </tr>
                ))}
                {d.rows.items.length === 0 && (
                  <tr><td colSpan={6} className="empty">
                    {d.totalListings === 0 ? `${name} için henüz eşleşmiş ilan yok.` : 'Filtreye uyan ilan yok.'}
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
        {d && <Pager page={page} pageSize={d.rows.pageSize} total={d.rows.total} onPage={setPage} />}
      </Card>
    </>
  );
}
