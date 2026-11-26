import { useRef, useState } from 'react';
import { ApiError } from '../api';
import { downloadExport, readDomTable, toExportTable, type ExportColumn } from '../export';

type Props<T> = {
  /** İndirilecek dosyanın adı (uzantısız; sunucu tarih ekler). */
  fileName: string;
  sheetName?: string;
  label?: string;
  /**
   * Veri modu (sayfalanmış listeler): filtreye uyan bütün kayıtları yükler (ör. fetchAllPages) ve sütunlarla Excel'e çevirir.
   * Verilmezse ekran modu: düğmenin bulunduğu kartın (.card) içindeki tablo (veya tableSelector) olduğu gibi okunur.
   */
  load?: () => Promise<T[]>;
  columns?: ExportColumn<T>[];
  tableSelector?: string;
};

/**
 * "Excel'e aktar" düğmesi (ADR-060). Her liste ekranında aynı bileşen: veri liste uçlarından (rol yetkisi ve Destek maskelemesiyle)
 * gelir, sunucudaki ortak uç (POST /api/export/xlsx) çalışma kitabını üretir. Hata sadeleştirilmiş mesajla düğmenin yanında gösterilir.
 */
export function ExportButton<T>({ fileName, sheetName, label = "Excel'e aktar", load, columns, tableSelector }: Props<T>) {
  const ref = useRef<HTMLButtonElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      let table;
      if (load && columns) {
        table = toExportTable(columns, await load());
      } else {
        const scope = ref.current?.closest('[data-export-scope], .card') ?? document;
        const el = scope.querySelector<HTMLTableElement>(tableSelector ?? 'table');
        if (!el) throw new ApiError(400, 'Aktarılacak tablo bulunamadı.');
        table = readDomTable(el);
      }
      if (table.rows.length === 0) throw new ApiError(400, 'Aktarılacak kayıt yok.');
      await downloadExport(table, fileName, sheetName);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Dışa aktarma tamamlanamadı.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="export-button">
      <button ref={ref} type="button" className="small inline" onClick={run} disabled={busy} data-testid="export-excel"
        title="Ekrandaki filtreye uyan bütün kayıtları Excel dosyası olarak indir">
        {busy ? 'Hazırlanıyor…' : label}
      </button>
      {error && <span className="small danger-text" role="alert"> {error}</span>}
    </span>
  );
}
