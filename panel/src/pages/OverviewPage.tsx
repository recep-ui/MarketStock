import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get, type ChannelStatus, type Dashboard } from '../api';
import { ChannelStatusGrid } from '../components/ChannelStatus';
import { ExportButton } from '../components/ExportButton';
import { Card, ChannelBadge, ErrorBox, ErrorExplain, Stat, useLoad } from '../components/ui';
import { formatDateTime, formatMoney, formatRelative, jobLabels } from '../format';

/** Ekran 1: Genel Bakış. Üstte kanal durumu (ADR-033), sonra günün sayıları, ciro, kritik stok ve kanal işleri. */
export function OverviewPage() {
  const { data, error, loading, reload } = useLoad(() => get<Dashboard>('/api/dashboard'), []);
  const statuses = useLoad(() => get<ChannelStatus[]>('/api/channels/status'), []);
  const [showHealthy, setShowHealthy] = useState(false);

  // Hatalı işler önce; sağlıklı olanlar istenirse gösterilir
  const jobs = [...(data?.jobs ?? [])].sort((a, b) => b.consecutiveFailures - a.consecutiveFailures);
  const failing = jobs.filter(j => j.consecutiveFailures > 0);
  const visibleJobs = showHealthy ? jobs : failing;

  return (
    <>
      <h1>Genel Bakış</h1>
      <ErrorBox error={error ?? statuses.error} onRetry={() => { reload(); statuses.reload(); }} />
      {(loading || statuses.loading) && !data && <div className="loading">Yükleniyor…</div>}

      {statuses.data && (
        <Card title="Kanal durumu" actions={<Link to="/ayarlar">Kanal ayarları →</Link>}>
          {statuses.data.some(s => s.active || s.hasCredentials)
            ? <ChannelStatusGrid compact statuses={statuses.data.filter(s => s.active || s.hasCredentials)} />
            : <p className="muted" data-testid="no-active-channel">Henüz aktif kanal yok. Kanal ayarlarından API bilgisini girip kanalı aktif yapın.</p>}
          {statuses.data.some(s => !s.active && !s.hasCredentials) && (
            <p className="muted small">
              Pasif kanallar: {statuses.data.filter(s => !s.active && !s.hasCredentials).map(s => s.name).join(', ')} (Ayarlar → Kanallar'dan açılır).
            </p>
          )}
        </Card>
      )}

      {data && (
        <>
          <div className="stats">
            <Stat label="Bugünkü sipariş" value={<Link to="/siparisler">{data.todayOrders}</Link>} testId="stat-today-orders" />
            <Stat label="Bugünkü ciro" value={formatMoney(data.todayRevenue)} />
            <Stat label="Stok yetersiz sipariş" value={<Link to="/siparisler">{data.insufficientStockOrders}</Link>} tone={data.insufficientStockOrders > 0 ? 'warn' : undefined} />
            <Stat label="Hatalı senkron" value={<Link to="/senkron?status=HATA">{data.syncErrors}</Link>} tone={data.syncErrors > 0 ? 'danger' : undefined} testId="stat-sync-errors" />
            <Stat label="Bekleyen senkron" value={<Link to="/senkron?status=BEKLIYOR">{data.syncPending}</Link>} />
          </div>

          <div className="grid-2">
            <Card title="Kanal bazlı ciro (bugün)" actions={<ExportButton fileName="kanal-bazli-ciro" sheetName="Kanal bazlı ciro" />}>
              <table className="table">
                <thead><tr><th>Kanal</th><th className="num">Sipariş</th><th className="num">Ciro</th></tr></thead>
                <tbody>
                  {data.byChannel.map(c => (
                    <tr key={c.channelCode}>
                      <td><ChannelBadge code={c.channelCode} /></td>
                      <td className="num">{c.orders}</td>
                      <td className="num">{formatMoney(c.revenue)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>

            <Card title={`Kritik stok (satılabilir ≤ ${data.criticalStockThreshold})`} actions={<ExportButton fileName="kritik-stok" sheetName="Kritik stok" />}>
              <table className="table">
                <thead><tr><th>SKU</th><th>Ürün</th><th className="num">Fiziksel</th><th className="num">Satılabilir</th></tr></thead>
                <tbody>
                  {data.criticalStock.map(s => (
                    <tr key={s.skuId} className={s.available <= 0 ? 'row-danger' : undefined}>
                      <td><Link to={`/stok/${s.skuId}`}>{s.skuCode}</Link></td><td>{s.productName}</td><td className="num">{s.physical}</td>
                      <td className="num">{s.available <= 0 ? <><span aria-hidden="true">✕ </span>{s.available} · tükendi</> : s.available}</td>
                    </tr>
                  ))}
                  {data.criticalStock.length === 0 && <tr><td colSpan={4} className="empty">Kritik stok yok.</td></tr>}
                </tbody>
              </table>
            </Card>
          </div>

          <Card
            title="Kanal işleri"
            actions={<>
              {jobs.length > failing.length && (
                <label className="check small">
                  <input type="checkbox" checked={showHealthy} onChange={e => setShowHealthy(e.target.checked)} /> Sorunsuz işleri de göster ({jobs.length - failing.length})
                </label>
              )}
              {visibleJobs.length > 0 && <ExportButton fileName="kanal-isleri" sheetName="Kanal işleri" />}
            </>}>
            {failing.length === 0 && jobs.length > 0 && !showHealthy && (
              <p className="ok-text" data-testid="jobs-all-ok"><span aria-hidden="true">✓</span> Tüm kanal işleri son çalışmada başarılı.</p>
            )}
            {visibleJobs.length > 0 && (
              <table className="table" data-testid="jobs-table">
                <thead><tr><th>Kanal</th><th>İş</th><th>Durum</th><th>Son deneme</th><th>Son başarılı</th></tr></thead>
                <tbody>
                  {visibleJobs.map(j => (
                    <tr key={j.channelCode + j.jobType}>
                      <td><ChannelBadge code={j.channelCode} /></td>
                      <td>{jobLabels[j.jobType] ?? j.jobType}</td>
                      <td>
                        {j.consecutiveFailures > 0
                          ? <>
                              <span className="text-danger"><span aria-hidden="true">✕</span> {j.consecutiveFailures} kez üst üste başarısız</span>
                              <ErrorExplain compact info={j.errorInfo} raw={j.lastError} />
                            </>
                          : <span className="ok-text"><span aria-hidden="true">✓</span> Başarılı</span>}
                      </td>
                      <td title={formatDateTime(j.lastAttemptUtc)}>{formatRelative(j.lastAttemptUtc)}</td>
                      <td title={formatDateTime(j.lastSuccessUtc)}>{formatRelative(j.lastSuccessUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {jobs.length === 0 && <p className="muted">Henüz çalışan kanal işi yok (kanallar pasif).</p>}
          </Card>
        </>
      )}
    </>
  );
}
