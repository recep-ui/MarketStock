import { useState } from 'react';
import type { ColumnDef } from '@tanstack/react-table';
import { get, qs, type AuditEntry, type Paged, type User } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ErrorBox, useLoad } from '../components/ui';
import { dayEndUtc, dayStartUtc, formatDateTime } from '../format';

export const auditTypeLabels: Record<string, string> = {
  ISTEK: 'İşlem',
  GIRIS: 'Giriş',
  GIRIS_HATALI: 'Hatalı giriş',
  CIKIS: 'Çıkış'
};

// Yol → anlaşılır işlem adı (en uzun eşleşen ön ek). Eşleşmeyen yol olduğu gibi gösterilir.
const areaLabels: [string, string][] = [
  ['/api/users', 'Kullanıcılar'],
  ['/api/settings', 'Ayarlar'],
  ['/api/warehouses', 'Depolar'],
  ['/api/channels', 'Kanal ayarları'],
  ['/api/channel-catalog', 'Kanal kataloğu'],
  ['/api/channel-commands', 'Kanal işlemleri'],
  ['/api/listings', 'Eşleştirme'],
  ['/api/stock', 'Stok'],
  ['/api/skus', 'Ürün / SKU'],
  ['/api/products', 'Ürün'],
  ['/api/excel', 'Excel'],
  ['/api/orders', 'Siparişler'],
  ['/api/returns', 'İade / İptal'],
  ['/api/prices', 'Fiyat'],
  ['/api/questions', 'Soru-Cevap'],
  ['/api/sync', 'Senkron'],
  ['/api/auth/login', 'Oturum'],
  ['/api/auth/logout', 'Oturum']
];

const methodLabels: Record<string, string> = { POST: 'ekleme / işlem', PUT: 'güncelleme', PATCH: 'güncelleme', DELETE: 'silme' };

export function describeAudit(e: AuditEntry): string {
  if (e.type !== 'ISTEK') return auditTypeLabels[e.type] ?? e.type;
  const area = areaLabels.filter(([prefix]) => e.path?.startsWith(prefix)).sort((a, b) => b[0].length - a[0].length)[0]?.[1];
  const action = e.method ? methodLabels[e.method] ?? e.method : '';
  return area ? `${area} · ${action}` : action;
}

const columns: ColumnDef<AuditEntry>[] = [
  { header: 'Tarih', cell: ({ row }) => formatDateTime(row.original.atUtc) },
  { header: 'Kullanıcı', cell: ({ row }) => row.original.userName ?? '—' },
  { header: 'Tip', cell: ({ row }) => <span className={row.original.type === 'GIRIS_HATALI' ? 'badge danger' : 'badge'}>{auditTypeLabels[row.original.type] ?? row.original.type}</span> },
  { header: 'İşlem', cell: ({ row }) => describeAudit(row.original) },
  { header: 'Ayrıntı', cell: ({ row }) => <code className="small">{[row.original.method, row.original.path].filter(Boolean).join(' ')}</code> },
  {
    header: 'Sonuç',
    cell: ({ row }) => {
      const s = row.original.status;
      if (s == null) return '—';
      return <span className={s >= 400 ? 'neg' : undefined}>{s >= 400 ? `Hata (${s})` : `Başarılı (${s})`}</span>;
    }
  },
  { header: 'Cihaz (IP)', cell: ({ row }) => row.original.ipAddress ?? '—' }
];

/** İşlem Geçmişi (ADR-045): kim, ne zaman, ne yaptı. Yalnızca Yönetici. İstek içeriği ve parolalar kaydedilmez. */
export function AuditPage() {
  const { can } = useAuth();
  const users = useLoad(() => (can('admin') ? get<User[]>('/api/users') : Promise.resolve([] as User[])), []);
  const [filters, setFilters] = useState({ user: '', type: '', from: '', to: '', path: '', errors: false });
  const [applied, setApplied] = useState(filters);
  const [page, setPage] = useState(1);
  const entries = useLoad(
    () => get<Paged<AuditEntry>>(`/api/audit${qs({
      user: applied.user, type: applied.type, from: dayStartUtc(applied.from), to: dayEndUtc(applied.to),
      path: applied.path, errors: applied.errors || undefined, page, pageSize: 50
    })}`),
    [applied, page]
  );

  if (!can('admin')) {
    return <><h1>İşlem Geçmişi</h1><Card><p>Bu ekranı yalnızca Yönetici görebilir.</p></Card></>;
  }

  return (
    <>
      <h1>İşlem Geçmişi</h1>
      <Card>
        <p className="muted small">
          Panelden yapılan tüm değişiklikler (ekleme, güncelleme, silme, onay, gönderim), girişler, çıkışlar ve hatalı giriş denemeleri.
          İçerik (parola, API anahtarı, müşteri bilgisi) kaydedilmez; yalnızca kim, ne zaman, hangi işlem ve sonucu.
        </p>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(filters); }}>
          <select aria-label="Kullanıcı" value={filters.user} onChange={e => setFilters({ ...filters, user: e.target.value })}>
            <option value="">Tüm kullanıcılar</option>
            {(users.data ?? []).map(u => <option key={u.id} value={u.userName}>{u.fullName} ({u.userName})</option>)}
          </select>
          <select aria-label="Tip" value={filters.type} onChange={e => setFilters({ ...filters, type: e.target.value })}>
            <option value="">Tüm tipler</option>
            {Object.entries(auditTypeLabels).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <input type="date" aria-label="Başlangıç" value={filters.from} onChange={e => setFilters({ ...filters, from: e.target.value })} />
          <input type="date" aria-label="Bitiş" value={filters.to} onChange={e => setFilters({ ...filters, to: e.target.value })} />
          <input placeholder="Yolda geçen (ör. users)" aria-label="Yol" value={filters.path} onChange={e => setFilters({ ...filters, path: e.target.value })} />
          <label className="check">
            <input type="checkbox" checked={filters.errors} onChange={e => setFilters({ ...filters, errors: e.target.checked })} /> Yalnızca hatalı
          </label>
          <button type="submit">Filtrele</button>
        </form>
        <ErrorBox error={entries.error} onRetry={entries.reload} />
        <DataTable columns={columns} data={entries.data?.items ?? []} loading={entries.loading} rowTestId="audit-row" />
        {entries.data && <Pager page={page} pageSize={entries.data.pageSize} total={entries.data.total} onPage={setPage} />}
      </Card>
    </>
  );
}
