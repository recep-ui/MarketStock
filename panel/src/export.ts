// Panel listelerinin Excel'e dışa aktarımı (ADR-060). Satırlar her zaman rolün yetkili olduğu liste uçlarından gelir
// (Destek'te maskeli), sunucudaki POST /api/export/xlsx yalnızca çalışma kitabına çevirir.
import { ApiError, simplifyError, type Paged } from './api';

export type ExportKind = 'text' | 'number' | 'money' | 'date' | 'datetime' | 'bool';
export type ExportValue = string | number | boolean | null | undefined;
export type ExportColumn<T> = { header: string; kind?: ExportKind; value: (row: T) => ExportValue };
export type ExportTable = { columns: { header: string; kind?: ExportKind }[]; rows: ExportValue[][] };

/** Sunucudaki sınırla aynı (TableExportWriter.MaxRows). */
export const MAX_EXPORT_ROWS = 100_000;

/**
 * Sayfalanmış bir listenin filtreye uyan BÜTÜN kayıtlarını sırayla çeker. Sunucu sayfa boyutunu kısabilir (ör. 200/500);
 * adım sunucunun döndürdüğü pageSize'a göre atılır.
 */
export async function fetchAllPages<T>(fetchPage: (page: number, pageSize: number) => Promise<Paged<T>>, pageSize = 500): Promise<T[]> {
  const all: T[] = [];
  for (let page = 1; ; page++) {
    const result = await fetchPage(page, pageSize);
    if (page === 1 && result.total > MAX_EXPORT_ROWS) {
      throw new ApiError(400, `En fazla ${MAX_EXPORT_ROWS.toLocaleString('tr-TR')} satır dışa aktarılabilir (${result.total.toLocaleString('tr-TR')} kayıt var). Filtreyi daraltın.`);
    }
    all.push(...result.items);
    if (result.items.length === 0 || all.length >= result.total || page * result.pageSize >= result.total) return all;
  }
}

export function toExportTable<T>(columns: ExportColumn<T>[], rows: T[]): ExportTable {
  return {
    columns: columns.map(c => ({ header: c.header, kind: c.kind })),
    rows: rows.map(r => columns.map(c => normalize(c.value(r))))
  };
}

function normalize(v: ExportValue): ExportValue {
  if (v === undefined || v === '') return null;
  if (typeof v === 'number' && !Number.isFinite(v)) return null;
  return v;
}

/** Çalışma kitabını sunucuda üretir ve indirir. */
export async function downloadExport(table: ExportTable, fileName: string, sheetName?: string) {
  let response: Response;
  try {
    response = await fetch('/api/export/xlsx', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileName, sheetName: sheetName ?? fileName, columns: table.columns, rows: table.rows })
    });
  } catch {
    throw new ApiError(0, 'Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.');
  }
  if (!response.ok) {
    let problem;
    try { problem = await response.json(); } catch { problem = undefined; }
    throw new ApiError(response.status, simplifyError(response.status, problem));
  }
  const blob = await response.blob();
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition);
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = match ? decodeURIComponent(match[1] ?? match[2]) : `${fileName}.xlsx`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

// ------------------------------------------------------------------ ekrandaki tablodan okuma (sayfalanmamış listeler)

/** Dışa aktarmaya girmeyen öğeler: düğmeler, onay kutuları, görseller, gizli ipuçları. */
const SKIP = 'button, input[type=checkbox], input[type=radio], img, svg, .thumb, [aria-hidden="true"], [data-export="skip"], details';
const BLOCK = new Set(['DIV', 'P', 'LI', 'UL', 'OL', 'BR', 'TR', 'TABLE']);

/** Hücrenin görünen metni: blok öğeler arasına " · " konur; input/select'in değeri alınır. */
export function cellText(cell: Element): string {
  const parts: string[] = [];
  let current = '';
  const flush = () => { const t = current.replace(/\s+/g, ' ').trim(); if (t) parts.push(t); current = ''; };
  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE) { current += node.textContent ?? ''; return; }
    if (!(node instanceof Element)) return;
    if (node.matches(SKIP)) return;
    if (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement) { current += ' ' + node.value + ' '; return; }
    if (node instanceof HTMLSelectElement) { current += ' ' + (node.selectedOptions[0]?.text ?? '') + ' '; return; }
    const block = BLOCK.has(node.tagName);
    if (block) flush();
    node.childNodes.forEach(walk);
    if (block) flush();
  };
  cell.childNodes.forEach(walk);
  flush();
  return parts.join(' · ');
}

// tr-TR sayı: 1.234 / -12 / 1.234,56; para: ₺1.234,56. Uzun tam sayılar (barkod) ve baştaki sıfırlar metin kalır.
const NUMBER = /^-?(?:0|[1-9]\d{0,2}(?:\.\d{3})*|[1-9]\d{0,8})(?:,\d+)?$/;
const parseTr = (s: string) => Number(s.replace(/\./g, '').replace(',', '.'));
const stripMoney = (s: string) => s.replace(/₺/g, '').replace(/[\s ]/g, '');

/** Ekrandaki <table>'ı başlıklarıyla okur; tamamen sayısal/para sütunlar sayı olarak gönderilir. Boş başlıklı sütunlar atlanır. */
export function readDomTable(table: HTMLTableElement): ExportTable {
  const headRow = table.tHead?.rows[table.tHead.rows.length - 1];
  const headers = headRow ? Array.from(headRow.cells).map(c => (c.matches('[data-export="skip"]') ? '' : cellText(c) || c.getAttribute('aria-label') || '')) : [];
  const bodyRows = Array.from(table.tBodies).flatMap(b => Array.from(b.rows))
    .filter(r => !(r.cells.length === 1 && r.cells[0].classList.contains('empty')));
  const texts = bodyRows.map(r => Array.from(r.cells).map(cellText));
  const keep = headers.map((h, i) => h.trim() !== '' && h !== '→' && texts.some(t => (t[i] ?? '') !== '') ? i : -1).filter(i => i >= 0);
  const keptHeaders = headers.length ? keep : (texts[0] ?? []).map((_, i) => i);

  const columns = keptHeaders.map(i => {
    const values = texts.map(t => t[i] ?? '').filter(v => v !== '' && v !== '—');
    const money = values.length > 0 && values.every(v => v.includes('₺') && NUMBER.test(stripMoney(v)));
    const number = !money && values.length > 0 && values.every(v => NUMBER.test(v));
    return { index: i, header: headers[i] || `Sütun ${i + 1}`, kind: (money ? 'money' : number ? 'number' : 'text') as ExportKind };
  });
  return {
    columns: columns.map(c => ({ header: c.header, kind: c.kind })),
    rows: texts.map(t => columns.map(c => {
      const v = t[c.index] ?? '';
      if (v === '' || v === '—') return null;
      if (c.kind === 'money') return parseTr(stripMoney(v));
      if (c.kind === 'number') return parseTr(v);
      return v;
    }))
  };
}
