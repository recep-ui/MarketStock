import { flexRender, getCoreRowModel, useReactTable, type ColumnDef } from '@tanstack/react-table';

type Props<T> = {
  columns: ColumnDef<T, any>[];
  data: T[];
  loading?: boolean;
  empty?: string;
  rowClassName?: (row: T) => string | undefined;
  rowTestId?: string;
  onRowClick?: (row: T) => void;
  selectedKey?: (row: T) => boolean;
};

/** TanStack Table tabanlı tablo. Sayfalama sunucu tarafında yapılır (bkz. Pager). */
export function DataTable<T>({ columns, data, loading, empty, rowClassName, rowTestId, onRowClick, selectedKey }: Props<T>) {
  const table = useReactTable({ data, columns, getCoreRowModel: getCoreRowModel() });
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          {table.getHeaderGroups().map(hg => (
            <tr key={hg.id}>
              {hg.headers.map(h => (
                <th key={h.id}>{h.isPlaceholder ? null : flexRender(h.column.columnDef.header, h.getContext())}</th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map(row => (
            <tr
              key={row.id}
              data-testid={rowTestId}
              className={[rowClassName?.(row.original), onRowClick ? 'clickable' : '', selectedKey?.(row.original) ? 'selected' : '']
                .filter(Boolean).join(' ')}
              onClick={onRowClick ? () => onRowClick(row.original) : undefined}
            >
              {row.getVisibleCells().map(cell => (
                <td key={cell.id}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>
              ))}
            </tr>
          ))}
          {!loading && data.length === 0 && (
            <tr>
              <td colSpan={columns.length} className="empty">{empty ?? 'Kayıt yok.'}</td>
            </tr>
          )}
        </tbody>
      </table>
      {loading && <div className="loading">Yükleniyor…</div>}
    </div>
  );
}

export function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  return (
    <div className="pager">
      <span>{total.toLocaleString('tr-TR')} kayıt</span>
      <button disabled={page <= 1} onClick={() => onPage(page - 1)}>‹ Önceki</button>
      <span>{page} / {pages}</span>
      <button disabled={page >= pages} onClick={() => onPage(page + 1)}>Sonraki ›</button>
    </div>
  );
}
