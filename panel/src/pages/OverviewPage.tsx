import { Link } from 'react-router-dom';
import { get, type Dashboard } from '../api';
import { Card, ChannelBadge, ErrorBox, Stat, useLoad } from '../components/ui';
import { formatDateTime, formatMoney } from '../format';

/** Ekran 1: Genel Bakış. */
export function OverviewPage() {
  const { data, error } = useLoad(() => get<Dashboard>('/api/dashboard'), []);

  return (
    <>
      <h1>Genel Bakış</h1>
      <ErrorBox error={error} />
      {data && (
        <>
          <div className="stats">
            <Stat label="Bugünkü sipariş" value={data.todayOrders} testId="stat-today-orders" />
            <Stat label="Bugünkü ciro" value={formatMoney(data.todayRevenue)} />
            <Stat label="Stok yetersiz sipariş" value={data.insufficientStockOrders} tone={data.insufficientStockOrders > 0 ? 'warn' : undefined} />
            <Stat label="Hatalı senkron" value={<Link to="/senkron?status=HATA">{data.syncErrors}</Link>} tone={data.syncErrors > 0 ? 'danger' : undefined} testId="stat-sync-errors" />
            <Stat label="Bekleyen senkron" value={data.syncPending} />
          </div>

          <div className="grid-2">
            <Card title="Kanal bazlı ciro (bugün)">
              <table className="table">
                <thead><tr><th>Kanal</th><th>Sipariş</th><th>Ciro</th></tr></thead>
                <tbody>
                  {data.byChannel.map(c => (
                    <tr key={c.channelCode}>
                      <td><ChannelBadge code={c.channelCode} /></td>
                      <td>{c.orders}</td>
                      <td>{formatMoney(c.revenue)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>

            <Card title={`Kritik stok (satılabilir ≤ ${data.criticalStockThreshold})`}>
              <table className="table">
                <thead><tr><th>SKU</th><th>Ürün</th><th>Fiziksel</th><th>Satılabilir</th></tr></thead>
                <tbody>
                  {data.criticalStock.map(s => (
                    <tr key={s.skuId} className={s.available <= 0 ? 'row-danger' : undefined}>
                      <td>{s.skuCode}</td><td>{s.productName}</td><td>{s.physical}</td><td>{s.available}</td>
                    </tr>
                  ))}
                  {data.criticalStock.length === 0 && <tr><td colSpan={4} className="empty">Kritik stok yok.</td></tr>}
                </tbody>
              </table>
            </Card>
          </div>

          <Card title="Kanal job'ları">
            <table className="table">
              <thead><tr><th>Kanal</th><th>Job</th><th>Son başarılı</th><th>Ardışık hata</th><th>Son hata</th></tr></thead>
              <tbody>
                {data.jobs.map(j => (
                  <tr key={j.channelCode + j.jobType} className={j.consecutiveFailures > 0 ? 'row-danger' : undefined}>
                    <td><ChannelBadge code={j.channelCode} /></td>
                    <td>{j.jobType}</td>
                    <td>{formatDateTime(j.lastSuccessUtc)}</td>
                    <td>{j.consecutiveFailures}</td>
                    <td className="wrap">{j.lastError ?? '—'}</td>
                  </tr>
                ))}
                {data.jobs.length === 0 && <tr><td colSpan={5} className="empty">Henüz çalışan kanal job'ı yok (kanallar pasif).</td></tr>}
              </tbody>
            </table>
          </Card>
        </>
      )}
    </>
  );
}
