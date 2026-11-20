import { Link, useParams } from 'react-router-dom';
import { get, type SkuDetail } from '../api';
import { Card, ChannelBadge, ChannelLink, ErrorBox, Stat, StatusBadge, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime, formatMoney, formatRelative, movementLabels } from '../format';

const ruleText = (type: string | null | undefined, value: number | null | undefined) =>
  type === 'UST_LIMIT' ? `üst limit ${value}` : type === 'SABIT' ? `sabit ${value}` : null;

/**
 * SKU detay (ADR-034): tek ürünün her şeyi bir ekranda — bakiye, depolar, kanal ilanları (stok, sapma, link),
 * fiyat, satış özeti, son hareketler ve son siparişler. Stok listesi, kritik stok, raporlar ve genel aramadan açılır.
 */
export function SkuDetailPage() {
  const { skuId } = useParams();
  const { data: d, error, loading, reload } = useLoad(() => get<SkuDetail>(`/api/skus/${skuId}/detail`), [skuId]);

  const variant = d ? [d.color, d.size].filter(Boolean).join(' / ') : '';

  return (
    <>
      <p className="small"><Link to="/stok">← Stok Listesi</Link></p>
      <ErrorBox error={error} onRetry={reload} />
      {loading && !d && <div className="loading">Yükleniyor…</div>}
      {d && (
        <div data-testid="sku-detail">
          <h1>
            {d.skuCode} <span className="muted">· {d.productName}</span>
          </h1>
          <p className="muted small">
            {[d.brand, d.category, variant || null, d.barcode ? `Barkod ${d.barcode}` : null].filter(Boolean).join(' · ')}
            {d.salePrice != null && <> · Satış fiyatı <strong>{formatMoney(d.salePrice)}</strong></>}
            {d.listPrice != null && d.listPrice !== d.salePrice && <> (liste {formatMoney(d.listPrice)})</>}
          </p>

          <div className="stats">
            <Stat label="Fiziksel" value={d.physical} />
            <Stat label="Rezerve" value={d.reserved} />
            <Stat label="Güvenlik stoğu" value={d.safetyStock} />
            <Stat label="Satılabilir" value={d.available} tone={d.available <= 0 ? 'danger' : undefined} testId="sku-available" />
            <Stat label="Son 7 gün satış" value={`${d.sales.units7Days} adet`} />
            <Stat
              label={`Son 30 gün · ${d.sales.orders30Days} sipariş · ${formatMoney(d.sales.revenue30Days)}`}
              value={`${d.sales.units30Days} adet`}
              testId="sku-sales-30"
            />
          </div>
          <p className="muted small">
            Satış adetleri iptal ve iade edilen siparişleri içermez.{' '}
            {d.sales.lastSoldUtc ? <>Son satış: <span title={formatDateTime(d.sales.lastSoldUtc)}>{formatRelative(d.sales.lastSoldUtc)}</span>.</> : 'Henüz satış yok.'}
          </p>

          <Card title="Kanallardaki ilanlar">
            {d.channels.length === 0 ? (
              <p className="muted">Bu SKU hiçbir kanal ilanıyla eşleşmemiş. <Link to="/eslestirme">Eşleştirme →</Link></p>
            ) : (
              <>
              {/* ADR-042: aynı SKU'ya bağlı ilanların kanal görselleri yan yana; farklı ürün görünüyorsa eşleşme yanlış olabilir */}
              <div className="thumb-strip" data-testid="sku-thumb-strip" aria-label="Kanallardaki görseller">
                {d.channels.map(c => (
                  <figure key={c.listingId}>
                    <Thumb url={c.imageUrl} alt={`${channelNames[c.channelCode] ?? c.channelCode} görseli`} size={96} />
                    <figcaption>{channelNames[c.channelCode] ?? c.channelCode}{c.variant ? ` · ${c.variant}` : ''}</figcaption>
                  </figure>
                ))}
              </div>
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Kanal</th><th>Kanal barkodu / kodu</th><th>Durum</th><th className="num">Gitmesi gereken</th>
                      <th className="num">Son gönderilen</th><th className="num">Kanalda görülen</th><th>Mutabakat</th><th></th>
                    </tr>
                  </thead>
                  <tbody>
                    {d.channels.map(c => (
                      <tr key={c.listingId} className={c.hasDeviation ? 'row-deviation' : undefined} data-testid="sku-listing">
                        <td><ChannelBadge code={c.channelCode} />{c.variant && <div className="small muted">{c.variant}</div>}</td>
                        <td>{c.channelBarcode ?? c.channelProductCode ?? '—'}</td>
                        <td>{c.status === 'PASIF' ? <span className="badge">Pasif</span> : <span className="badge ok">Aktif</span>}</td>
                        <td className="num">
                          {c.expected}
                          {ruleText(c.stockRuleType, c.stockRuleValue) && <div className="small muted">{ruleText(c.stockRuleType, c.stockRuleValue)}</div>}
                        </td>
                        <td className="num">{c.lastSent ?? '—'}</td>
                        <td className="num">
                          {c.channelStock ?? (c.missingInChannel
                            ? <span className="muted small" title="Son mutabakatta bu ilan kanalda bulunamadı">kanalda yok</span>
                            : '—')}
                          {c.hasDeviation && <small> (sapma)</small>}
                        </td>
                        <td title={formatDateTime(c.fetchedAtUtc)}>{c.fetchedAtUtc ? formatRelative(c.fetchedAtUtc) : '—'}</td>
                        <td><ChannelLink code={c.channelCode} url={c.productUrl} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              </>
            )}
            {d.hasDeviation && <p className="small">Sapma var: kanaldaki değer defterle uyuşmuyor. <Link to="/senkron">Senkron Merkezi →</Link></p>}
          </Card>

          <div className="grid-2">
            <Card title="Son siparişler" actions={<Link to={`/siparisler`}>Tüm siparişler →</Link>}>
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>Tarih</th><th>Kanal</th><th>Sipariş no</th><th>Durum</th><th className="num">Adet</th><th className="num">Birim</th></tr></thead>
                  <tbody>
                    {d.recentOrders.map(o => (
                      <tr key={o.orderId} data-testid="sku-order">
                        <td>{formatDateTime(o.orderDateUtc)}</td>
                        <td><ChannelBadge code={o.channelCode} /></td>
                        <td>
                          <Link to={`/siparisler?order=${o.orderId}`}>{o.channelOrderNo}</Link>
                          {o.insufficientStock && <span className="badge warn">Stok yetersiz</span>}
                        </td>
                        <td><StatusBadge status={o.status} /></td>
                        <td className="num">{o.quantity}</td>
                        <td className="num">{formatMoney(o.unitPrice)}</td>
                      </tr>
                    ))}
                    {d.recentOrders.length === 0 && <tr><td colSpan={6} className="empty">Sipariş yok.</td></tr>}
                  </tbody>
                </table>
              </div>
            </Card>

            <Card title="Son stok hareketleri" actions={<Link to={`/hareketler?sku=${encodeURIComponent(d.skuCode)}`}>Tüm hareketler →</Link>}>
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>Tarih</th><th>Tür</th><th className="num">Adet</th><th>Depo</th><th>Kaynak</th></tr></thead>
                  <tbody>
                    {d.recentMovements.map(m => (
                      <tr key={m.id}>
                        <td>{formatDateTime(m.atUtc)}</td>
                        <td>{movementLabels[m.type] ?? m.type}</td>
                        <td className="num">{m.quantity}</td>
                        <td>{m.warehouseName}</td>
                        <td className="small">{m.sourceType ?? '—'}{m.user && <span className="muted"> · {m.user}</span>}</td>
                      </tr>
                    ))}
                    {d.recentMovements.length === 0 && <tr><td colSpan={5} className="empty">Hareket yok.</td></tr>}
                  </tbody>
                </table>
              </div>
            </Card>
          </div>

          {d.warehouses.length > 1 && (
            <Card title="Depolar">
              <table className="table">
                <thead><tr><th>Depo</th><th className="num">Fiziksel</th><th className="num">Rezerve</th><th className="num">Güvenlik</th><th className="num">Satılabilir</th></tr></thead>
                <tbody>
                  {d.warehouses.map(w => (
                    <tr key={w.warehouseId}>
                      <td>{w.warehouseName}</td><td className="num">{w.physical}</td><td className="num">{w.reserved}</td>
                      <td className="num">{w.safetyStock}</td><td className="num">{w.available}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>
          )}
        </div>
      )}
    </>
  );
}
