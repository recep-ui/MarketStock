import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { download, get, qs, type Channel, type SalesReport } from '../api';
import { Card, ChannelBadge, ErrorBox, Stat, useLoad } from '../components/ui';
import { ExportButton } from '../components/ExportButton';
import { channelNames, dayEndUtc, dayStartUtc, formatDateTime, formatMoney, formatRelative } from '../format';

/** Türkiye günü (YYYY-MM-DD). */
const turkeyDay = (d: Date) => d.toLocaleDateString('sv-SE', { timeZone: 'Europe/Istanbul' });
const addDays = (day: string, n: number) => {
  const d = new Date(`${day}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
const shortDay = new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: 'short', timeZone: 'UTC' });
const longDay = new Intl.DateTimeFormat('tr-TR', { weekday: 'short', day: '2-digit', month: 'long', year: 'numeric', timeZone: 'UTC' });
const fmtDay = (day: string, long = false) => (long ? longDay : shortDay).format(new Date(`${day}T00:00:00Z`));
const percent = (v: number) => `%${v.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`;

type Range = { from: string; to: string };

function presets(today: string): { label: string; range: Range }[] {
  const monthStart = `${today.slice(0, 8)}01`;
  const prevMonthEnd = addDays(monthStart, -1);
  return [
    { label: 'Bugün', range: { from: today, to: today } },
    { label: 'Son 7 gün', range: { from: addDays(today, -6), to: today } },
    { label: 'Son 30 gün', range: { from: addDays(today, -29), to: today } },
    { label: 'Bu ay', range: { from: monthStart, to: today } },
    { label: 'Geçen ay', range: { from: `${prevMonthEnd.slice(0, 8)}01`, to: prevMonthEnd } }
  ];
}

/**
 * Raporlar (ADR-034): dönem ve kanal seçimiyle net ciro, sipariş, adet, iptal/iade oranları, günlük ciro grafiği,
 * kanal karşılaştırması, en çok satanlar, stoğu olup satmayanlar ve dönemin siparişlerini Excel'e aktarma.
 */
export function ReportsPage() {
  const today = turkeyDay(new Date());
  const options = presets(today);
  const [range, setRange] = useState<Range>(options[2].range);
  const [draft, setDraft] = useState<Range>(options[2].range);
  const [channelId, setChannelId] = useState('');
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<unknown>(null);

  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const report = useLoad(
    () => get<SalesReport>(`/api/reports/sales${qs({ from: range.from, to: range.to, channelId })}`),
    [range.from, range.to, channelId]
  );
  const r = report.data;

  const apply = (next: Range) => { setDraft(next); setRange(next); };
  const exportExcel = async () => {
    setExporting(true);
    setExportError(null);
    try {
      await download(`/api/orders/export${qs({ channelId, from: dayStartUtc(range.from), to: dayEndUtc(range.to) })}`, 'siparisler.xlsx');
    } catch (e) {
      setExportError(e);
    } finally {
      setExporting(false);
    }
  };

  return (
    <>
      <h1>Raporlar</h1>
      <Card>
        <div className="filters" role="group" aria-label="Dönem">
          {options.map(p => (
            <button
              key={p.label}
              type="button"
              className={p.range.from === range.from && p.range.to === range.to ? 'primary' : undefined}
              aria-pressed={p.range.from === range.from && p.range.to === range.to}
              onClick={() => apply(p.range)}
            >
              {p.label}
            </button>
          ))}
        </div>
        <form className="filters" onSubmit={e => { e.preventDefault(); setRange(draft); }}>
          <input type="date" aria-label="Başlangıç" value={draft.from} max={draft.to} onChange={e => setDraft({ ...draft, from: e.target.value })} />
          <input type="date" aria-label="Bitiş" value={draft.to} min={draft.from} onChange={e => setDraft({ ...draft, to: e.target.value })} />
          <select aria-label="Kanal" value={channelId} onChange={e => setChannelId(e.target.value)}>
            <option value="">Tüm kanallar</option>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
          <button type="submit">Uygula</button>
          <button type="button" onClick={exportExcel} disabled={exporting} title="Seçili dönemin siparişleri, satır bazında (müşteri bilgisi içermez)">
            {exporting ? 'Hazırlanıyor…' : "Siparişleri Excel'e aktar"}
          </button>
        </form>
        <p className="muted small">
          Günler Türkiye saatine göredir. Net ciro ve adet, iptal ve iade edilen siparişleri içermez; ciro, kanalın bildirdiği sipariş tutarıdır.
        </p>
        <ErrorBox error={report.error ?? exportError} onRetry={report.reload} />
      </Card>

      {report.loading && !r && <div className="loading">Yükleniyor…</div>}
      {r && (
        <div data-testid="sales-report" aria-busy={report.loading}>
          <div className="stats">
            <Stat label="Net ciro" value={formatMoney(r.totals.netRevenue)} testId="report-net-revenue" />
            <Stat label="Net sipariş" value={r.totals.netOrders} testId="report-net-orders" />
            <Stat label="Satılan adet" value={r.totals.units} />
            <Stat label="Ortalama sepet" value={formatMoney(r.totals.averageBasket)} />
            <Stat
              label={`İptal · ${percent(r.totals.cancelRate)} (${r.totals.totalOrders} siparişte)`}
              value={r.totals.cancelledOrders}
              tone={r.totals.cancelRate >= 10 ? 'warn' : undefined}
            />
            <Stat
              label={`İade · ${percent(r.totals.returnRate)} · ${formatMoney(r.totals.returnedAmount)}`}
              value={r.totals.returnedOrders}
              tone={r.totals.returnRate >= 10 ? 'warn' : undefined}
            />
          </div>

          <Card title={`Günlük net ciro · ${fmtDay(r.period.from)} – ${fmtDay(r.period.to)}`} actions={<ExportButton fileName="gunluk-ciro" sheetName="Günlük ciro" tableSelector="details table" />}>
            <DailyChart days={r.daily} />
          </Card>

          <Card title="Kanal karşılaştırması" actions={<ExportButton fileName="kanal-karsilastirmasi" sheetName="Kanal karşılaştırması" />}>
            <div className="table-wrap">
              <table className="table" data-testid="report-channels">
                <thead>
                  <tr>
                    <th>Kanal</th><th className="num">Net ciro</th><th className="num">Pay</th><th className="num">Net sipariş</th>
                    <th className="num">Adet</th><th className="num">İptal</th><th className="num">İade</th><th className="num">İade oranı</th>
                  </tr>
                </thead>
                <tbody>
                  {r.byChannel.map(c => (
                    <tr key={c.channelCode}>
                      <td><ChannelBadge code={c.channelCode} /></td>
                      <td className="num">{formatMoney(c.netRevenue)}</td>
                      <td className="num">{r.totals.netRevenue > 0 ? percent((c.netRevenue * 100) / r.totals.netRevenue) : '—'}</td>
                      <td className="num">{c.netOrders}</td>
                      <td className="num">{c.units}</td>
                      <td className="num">{c.cancelledOrders}</td>
                      <td className="num">{c.returnedOrders}</td>
                      <td className="num">{percent(c.returnRate)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {r.unmatchedUnits > 0 && (
              <p className="small muted">
                {r.unmatchedUnits} adet ürün eşleşmemiş sipariş satırında; bunlar en çok satanlar listesinde yer almaz. <Link to="/eslestirme">Eşleştirme →</Link>
              </p>
            )}
          </Card>

          <div className="grid-2">
            <Card title="En çok satanlar" actions={<ExportButton fileName="en-cok-satanlar" sheetName="En çok satanlar" />}>
              <div className="table-wrap">
              <table className="table" data-testid="report-top-skus">
                <thead><tr><th>SKU</th><th>Ürün</th><th className="num">Adet</th><th className="num">Tutar</th><th className="num">Satılabilir</th></tr></thead>
                <tbody>
                  {r.topSkus.map(s => (
                    <tr key={s.skuId}>
                      <td><Link to={`/stok/${s.skuId}`}>{s.skuCode}</Link></td>
                      <td className="wrap">{s.productName}</td>
                      <td className="num">{s.units}</td>
                      <td className="num">{formatMoney(s.revenue)}</td>
                      <td className={`num${s.available <= 0 ? ' neg' : ''}`}>{s.available}</td>
                    </tr>
                  ))}
                  {r.topSkus.length === 0 && <tr><td colSpan={5} className="empty">Bu dönemde satış yok.</td></tr>}
                </tbody>
              </table>
              </div>
            </Card>

            <Card title="Stoğu var, bu dönemde satmadı" actions={<ExportButton fileName="satmayanlar" sheetName="Satmayanlar" />}>
              <div className="table-wrap">
              <table className="table" data-testid="report-slow-movers">
                <thead><tr><th>SKU</th><th>Ürün</th><th className="num">Fiziksel</th><th>Son satış</th></tr></thead>
                <tbody>
                  {r.slowMovers.map(s => (
                    <tr key={s.skuId}>
                      <td><Link to={`/stok/${s.skuId}`}>{s.skuCode}</Link></td>
                      <td className="wrap">{s.productName}</td>
                      <td className="num">{s.physical}</td>
                      <td title={formatDateTime(s.lastSoldUtc)}>{s.lastSoldUtc ? formatRelative(s.lastSoldUtc) : 'hiç satmadı'}</td>
                    </tr>
                  ))}
                  {r.slowMovers.length === 0 && <tr><td colSpan={4} className="empty">Stoğu olan her ürün bu dönemde satıldı.</td></tr>}
                </tbody>
              </table>
              </div>
              <p className="muted small">En yüksek fiziksel stoğa göre ilk 10.</p>
            </Card>
          </div>
        </div>
      )}
    </>
  );
}

/** "Güzel" eksen üst sınırı: 1, 2, 2.5, 5 × 10ⁿ. */
function niceMax(value: number) {
  if (value <= 0) return 1;
  const exp = Math.pow(10, Math.floor(Math.log10(value)));
  const step = [1, 2, 2.5, 5, 10].find(m => m * exp >= value)!;
  return step * exp;
}
const compactMoney = (v: number) =>
  v.toLocaleString('tr-TR', { notation: 'compact', maximumFractionDigits: 1 }) + ' ₺';

/**
 * Günlük net ciro: tek seri (lejant yok, başlık adlandırır), ince çubuklar, 2 px boşluk, yalnızca üstü yuvarlak.
 * Üzerine gelince gün, ciro, sipariş ve kanal kırılımı. Erişilebilirlik: aynı veri "Tablo olarak göster"de.
 */
function DailyChart({ days }: { days: SalesReport['daily'] }) {
  const [hover, setHover] = useState<number | null>(null);
  const width = 760;
  const height = 220;
  const pad = { top: 12, right: 8, bottom: 26, left: 56 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const max = useMemo(() => niceMax(Math.max(0, ...days.map(d => d.revenue))), [days]);
  const slot = plotW / Math.max(days.length, 1);
  const gap = Math.min(2, slot * 0.2);
  const barW = Math.max(1, Math.min(28, slot - gap));
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(t => t * max);
  const labelEvery = Math.ceil(days.length / 10);
  const y = (v: number) => pad.top + plotH - (v / max) * plotH;
  const total = days.reduce((s, d) => s + d.revenue, 0);
  const h = hover != null ? days[hover] : null;

  return (
    <div className="chart" data-testid="daily-chart">
      {total === 0 && <p className="muted">Bu dönemde net satış yok.</p>}
      <div className="chart-plot" onMouseLeave={() => setHover(null)}>
        <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`Günlük net ciro, ${days.length} gün, toplam ${formatMoney(total)}`}>
          {ticks.map(t => (
            <g key={t}>
              <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className="chart-grid" />
              <text x={pad.left - 6} y={y(t)} dy="0.32em" textAnchor="end" className="chart-axis">{compactMoney(t)}</text>
            </g>
          ))}
          {days.map((d, i) => {
            const x = pad.left + i * slot + (slot - barW) / 2;
            const top = y(d.revenue);
            const bh = pad.top + plotH - top;
            const r = Math.min(4, barW / 2, bh);
            return (
              <g key={d.day}>
                {bh > 0 && (
                  <path
                    className={`chart-bar${hover === i ? ' active' : ''}`}
                    d={`M${x},${top + bh} V${top + r} Q${x},${top} ${x + r},${top} H${x + barW - r} Q${x + barW},${top} ${x + barW},${top + r} V${top + bh} Z`}
                  />
                )}
                {i % labelEvery === 0 && (
                  <text x={x + barW / 2} y={height - 8} textAnchor="middle" className="chart-axis">{fmtDay(d.day)}</text>
                )}
                {/* İsabet alanı çubuktan geniş: tüm sütun */}
                <rect x={pad.left + i * slot} y={pad.top} width={slot} height={plotH} fill="transparent" onMouseEnter={() => setHover(i)} />
              </g>
            );
          })}
          {hover != null && (
            <line x1={pad.left + hover * slot + slot / 2} x2={pad.left + hover * slot + slot / 2} y1={pad.top} y2={pad.top + plotH} className="chart-cross" />
          )}
        </svg>
        {h && (
          <div
            className="chart-tooltip"
            role="status"
            style={{ left: `${Math.min(88, Math.max(12, ((pad.left + hover! * slot + slot / 2) / width) * 100))}%` }}
          >
            <strong>{fmtDay(h.day, true)}</strong>
            <div>{formatMoney(h.revenue)} · {h.orders} sipariş</div>
            {Object.entries(h.revenueByChannel).sort((a, b) => b[1] - a[1]).map(([code, v]) => (
              <div key={code} className="muted">{channelNames[code] ?? code}: {formatMoney(v)}</div>
            ))}
          </div>
        )}
      </div>
      <details className="small">
        <summary>Tablo olarak göster</summary>
        <table className="table">
          <thead><tr><th>Gün</th><th className="num">Net sipariş</th><th className="num">Net ciro</th></tr></thead>
          <tbody>
            {days.map(d => (
              <tr key={d.day}><td>{fmtDay(d.day, true)}</td><td className="num">{d.orders}</td><td className="num">{formatMoney(d.revenue)}</td></tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  );
}
