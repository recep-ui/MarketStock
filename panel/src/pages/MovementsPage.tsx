import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { get, post, qs, type Movement, type Paged, type SkuItem, type Warehouse } from '../api';
import { useAuth } from '../auth';
import { DataTable, Pager } from '../components/DataTable';
import { Card, ErrorBox, Field, Notice, useLoad } from '../components/ui';
import { dayEndUtc, dayStartUtc, formatDateTime, movementLabels } from '../format';

const columns: ColumnDef<Movement>[] = [
  { header: 'Tarih', cell: ({ row }) => formatDateTime(row.original.atUtc) },
  { header: 'SKU', accessorKey: 'skuCode' },
  { header: 'Depo', accessorKey: 'warehouseName' },
  { header: 'Tip', cell: ({ row }) => movementLabels[row.original.type] ?? row.original.type },
  { header: 'Adet', cell: ({ row }) => <span className={row.original.quantity < 0 ? 'neg' : undefined}>{row.original.quantity}</span> },
  { header: 'Kaynak', cell: ({ row }) => [row.original.sourceType, row.original.sourceId].filter(Boolean).join(' · ') || '—' },
  { header: 'Kullanıcı', cell: ({ row }) => row.original.user ?? '—' }
];

/** SKU kodunu Id'ye çevirir (tam eşleşme). */
export async function resolveSku(code: string): Promise<SkuItem | null> {
  const result = await get<Paged<SkuItem>>(`/api/skus${qs({ search: code, pageSize: 20 })}`);
  return result.items.find(s => s.skuCode === code.trim()) ?? null;
}

/** Ekran 3: Stok Hareketleri (filtrelenebilir defter) + stok girişi / sayım. */
export function MovementsPage() {
  const { can } = useAuth();
  // SKU detaydan gelen ?sku=KOD filtreyi doldurur (ADR-034)
  const [params] = useSearchParams();
  const [filters, setFilters] = useState({ sku: params.get('sku') ?? '', type: '', from: '', to: '' });
  const [applied, setApplied] = useState(filters);
  const [page, setPage] = useState(1);
  const movements = useLoad(
    () => get<Paged<Movement>>(`/api/stock-movements${qs({ sku: applied.sku, type: applied.type, from: dayStartUtc(applied.from), to: dayEndUtc(applied.to), page, pageSize: 50 })}`),
    [applied, page]
  );

  return (
    <>
      <h1>Stok Hareketleri</h1>
      {can('stockWrite') && <StockEntryForm onDone={movements.reload} />}
      <Card>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(filters); }}>
          <input placeholder="SKU (ön ek)" value={filters.sku} onChange={e => setFilters({ ...filters, sku: e.target.value })} aria-label="SKU" />
          <select value={filters.type} onChange={e => setFilters({ ...filters, type: e.target.value })} aria-label="Tip">
            <option value="">Tüm tipler</option>
            {Object.entries(movementLabels).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <input type="date" value={filters.from} onChange={e => setFilters({ ...filters, from: e.target.value })} aria-label="Başlangıç" />
          <input type="date" value={filters.to} onChange={e => setFilters({ ...filters, to: e.target.value })} aria-label="Bitiş" />
          <button type="submit">Filtrele</button>
        </form>
        <ErrorBox error={movements.error} />
        <DataTable columns={columns} data={movements.data?.items ?? []} loading={movements.loading} rowTestId="movement-row" />
        {movements.data && <Pager page={page} pageSize={movements.data.pageSize} total={movements.data.total} onPage={setPage} />}
      </Card>
    </>
  );
}

function StockEntryForm({ onDone }: { onDone: () => void }) {
  const warehouses = useLoad(() => get<Warehouse[]>('/api/warehouses'), []);
  const [form, setForm] = useState({ sku: '', warehouseId: '', operation: 'receipt', quantity: '', reference: '' });
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const sku = await resolveSku(form.sku);
      if (!sku) throw new Error(`SKU bulunamadı: ${form.sku}`);
      const warehouseId = Number(form.warehouseId || warehouses.data?.[0]?.id);
      const quantity = Number(form.quantity);
      const result = form.operation === 'receipt'
        ? await post<{ physical: number }>('/api/stock/receipts', { skuId: sku.id, warehouseId, quantity, reference: form.reference || null })
        : await post<{ physical: number }>('/api/stock/counts', { skuId: sku.id, warehouseId, countedQuantity: quantity, reference: form.reference || null });
      setNotice(`${sku.skuCode}: yeni fiziksel stok ${result.physical}.`);
      setForm({ ...form, quantity: '', reference: '' });
      onDone();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Stok girişi / sayım">
      <Notice text={notice} onClose={() => setNotice(null)} />
      {error ? <div className="alert alert-error">{(error as Error).message}</div> : null}
      <form className="filters" onSubmit={submit}>
        <Field label="SKU kodu"><input value={form.sku} onChange={e => setForm({ ...form, sku: e.target.value })} required /></Field>
        <Field label="Depo">
          <select value={form.warehouseId} onChange={e => setForm({ ...form, warehouseId: e.target.value })}>
            {(warehouses.data ?? []).map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
        </Field>
        <Field label="İşlem">
          <select value={form.operation} onChange={e => setForm({ ...form, operation: e.target.value })}>
            <option value="receipt">Giriş (ekle)</option>
            <option value="count">Sayım (eşitle)</option>
          </select>
        </Field>
        <Field label="Adet"><input type="number" min={0} value={form.quantity} onChange={e => setForm({ ...form, quantity: e.target.value })} required /></Field>
        <Field label="Referans"><input value={form.reference} onChange={e => setForm({ ...form, reference: e.target.value })} placeholder="İrsaliye no" /></Field>
        <button className="primary" type="submit">Kaydet</button>
      </form>
    </Card>
  );
}
