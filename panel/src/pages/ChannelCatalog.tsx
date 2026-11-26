import { useEffect, useState } from 'react';
import { get, post, qs, upload, type CatalogFetchStatus, type CatalogImportReport, type CatalogImportRow, type Channel, type ChannelStatus, type Paged, type UnmatchedItem } from '../api';
import { CatalogNoMatchHelp, ChannelDataSourceNote } from '../components/ChannelDataSource';
import { Card, ChannelBadge, ErrorBox, Notice, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { ExportButton } from '../components/ExportButton';
import { fetchAllPages } from '../export';
import { unmatchedExportColumns } from '../exportColumns';
import { resolveSku } from './MovementsPage';

/** Satıcı panelinden indirilen ürün dosyası biçimi olan kanallar (ADR-020). */
const fileChannels = ['TRENDYOL', 'BEYMEN'];

const outcomeLabels: Record<string, string> = {
  ESLESTI: 'Eşleşti',
  OLUSTURULDU: 'Yeni SKU',
  ESLESME_YOK: 'Eşleşmedi',
  BELIRSIZ: 'Belirsiz',
  CAKISMA: 'Çakışma'
};
const stepLabels: Record<string, string> = {
  MEVCUT_ESLESTIRME: 'mevcut eşleştirme',
  BARKOD: 'barkod',
  STOK_KODU: 'stok kodu',
  MODEL_RENK_BEDEN: 'model+renk+beden'
};
const reasonLabels: Record<string, string> = { ESLESME_YOK: 'Eşleşmedi', BELIRSIZ: 'Belirsiz', CAKISMA: 'Çakışma' };

/**
 * Kanal ürün Excel'i yükleme: önce önizleme (hiçbir şey yazılmaz), sonra onayla uygula.
 * "Eşleşmeyenlerden SKU oluştur" yalnızca açıkça seçilirse; belirsiz / çakışan / önerili satırlardan SKU açılmaz.
 */
export function ChannelCatalogImport({ channels, onApplied }: { channels: Channel[]; onApplied: () => void }) {
  const candidates = channels.filter(c => fileChannels.includes(c.code));
  const [channelId, setChannelId] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [createMissing, setCreateMissing] = useState(false);
  const [preview, setPreview] = useState<CatalogImportReport | null>(null);
  const [result, setResult] = useState<CatalogImportReport | null>(null);
  const [filter, setFilter] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const selected = channelId || String(candidates[0]?.id ?? '');

  async function run(validateOnly: boolean) {
    if (!file || !selected) return;
    setBusy(true);
    setError(null);
    try {
      const report = await upload<CatalogImportReport>(`/api/channels/${selected}/catalog/import${qs({ validateOnly: String(validateOnly), createMissing: String(createMissing) })}`, file);
      if (validateOnly) {
        setPreview(report);
        setResult(null);
      } else {
        setResult(report);
        setPreview(null);
        onApplied();
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  function reset() {
    setPreview(null);
    setResult(null);
  }

  const report = result ?? preview;
  return (
    <Card title="Kanal ürün dosyasından içe al">
      <p className="muted">
        Trendyol veya Beymen satıcı panelinden indirdiğiniz ürün listesini yükleyin. Eşleştirme sırası: bu kanaldaki mevcut eşleştirme →
        barkod → satıcı stok kodu → model + renk + beden. Ürün adı benzerliği yalnızca öneridir; otomatik bağlanmaz.
      </p>
      <div className="filters">
        <select aria-label="Kanal (dosya)" value={selected} onChange={e => { setChannelId(e.target.value); reset(); }}>
          {candidates.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
        </select>
        <input type="file" accept=".xlsx" aria-label="Kanal ürün dosyası" onChange={e => { setFile(e.target.files?.[0] ?? null); reset(); }} />
        <label className="check">
          <input type="checkbox" checked={createMissing} onChange={e => { setCreateMissing(e.target.checked); reset(); }} />
          Eşleşmeyenlerden ürün/SKU oluştur
        </label>
        <button disabled={!file || busy} onClick={() => run(true)}>{busy && !preview ? 'İşleniyor…' : 'Önizle'}</button>
        {preview && preview.fileErrors.length === 0 && (
          <button className="primary" disabled={busy} onClick={() => run(false)} data-testid="catalog-apply">
            {busy ? 'Uygulanıyor…' : 'Onayla ve uygula'}
          </button>
        )}
      </div>
      <ErrorBox error={error} />
      {report && <CatalogReport report={report} filter={filter} onFilter={setFilter} />}
    </Card>
  );
}

/**
 * Kanal API'sinden katalog çekme (Hangfire job'ı). Önizlemede hiçbir şey yazılmaz. Durum her 2 sn'de sorgulanır.
 * Kanal API'leri yalnızca doğrulanmış alanları döndürür (Hepsiburada/Beymen'de model-renk-beden yok; ADR-022).
 */
export function ChannelCatalogFetch({ channels, onApplied }: { channels: Channel[]; onApplied: () => void }) {
  const [channelId, setChannelId] = useState('');
  const [createMissing, setCreateMissing] = useState(false);
  const [status, setStatus] = useState<CatalogFetchStatus | null>(null);
  const [filter, setFilter] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [tick, setTick] = useState(0);
  const selected = channelId || String(channels[0]?.id ?? '');
  const running = status?.state === 'CALISIYOR';
  const statuses = useLoad(() => get<ChannelStatus[]>('/api/channels/status'), []);

  useEffect(() => {
    if (!selected) return;
    let cancelled = false;
    get<CatalogFetchStatus>(`/api/channels/${selected}/catalog/fetch-status`)
      .then(s => {
        if (cancelled) return;
        setStatus(prev => {
          if (prev?.state === 'CALISIYOR' && s.state === 'TAMAMLANDI' && s.report && !s.report.validateOnly) onApplied();
          return s;
        });
        // Kutu, ekrandaki önizlemenin seçeneğini göstersin (ekran yeniden açıldığında varsayılana dönmesin)
        if (s.state === 'TAMAMLANDI' && s.report?.validateOnly) setCreateMissing(s.report.createMissing);
      })
      .catch(setError);
    return () => { cancelled = true; };
  }, [selected, tick]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!running) return;
    const t = setTimeout(() => setTick(x => x + 1), 2000);
    return () => clearTimeout(t);
  }, [running, tick]);

  /** Uygulama, önizlenen seçenekle yapılır (ekran yenilense veya kutu değişse bile önizlemede görülenden farklı sonuç çıkmasın). */
  const previewCreateMissing = status?.state === 'TAMAMLANDI' && status.report?.validateOnly ? status.report.createMissing : null;

  async function start(validateOnly: boolean) {
    setError(null);
    const create = validateOnly ? createMissing : (previewCreateMissing ?? createMissing);
    try {
      await post(`/api/channels/${selected}/catalog/fetch${qs({ validateOnly: String(validateOnly), createMissing: String(create) })}`);
      setTick(x => x + 1);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kanaldan ürün çek (API)">
      <p className="muted">
        Kanalın API'sinden ürün listesini çeker ve dosya içe alma ile aynı kurallarla eşleştirir. Önce önizleyin; uygulama ayrı bir adımdır.
        Hepsiburada ve Beymen API'leri yalnızca kanal kodu/barkod/stok kodu verir (model-renk-beden yok).
      </p>
      <div className="filters">
        <select aria-label="Kanal (API)" value={selected} onChange={e => { setChannelId(e.target.value); setStatus(null); }}>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
        </select>
        <label className="check">
          <input type="checkbox" checked={createMissing} onChange={e => setCreateMissing(e.target.checked)} />
          Eşleşmeyenlerden ürün/SKU oluştur
        </label>
        <button disabled={running || !selected} onClick={() => start(true)}>Kanaldan çek ve önizle</button>
        {status?.state === 'TAMAMLANDI' && status.report?.validateOnly && (
          <button className="primary" disabled={running} onClick={() => start(false)} data-testid="catalog-fetch-apply"
            title="Önizlemedeki seçenekle uygulanır">
            Çek ve uygula{previewCreateMissing ? ' (eşleşmeyenlerden ürün oluştur)' : ' (yalnızca eşleştir)'}
          </button>
        )}
      </div>
      <ChannelDataSourceNote status={statuses.data?.find(s => String(s.id) === selected)} />
      <ErrorBox error={error} />
      {running && <div className="alert alert-warn">Kanaldan ürünler çekiliyor… (başlangıç {formatDateTime(status?.lastAttemptUtc ?? null)})</div>}
      {status?.state === 'HATA' && <div className="alert alert-error">Son çekme başarısız: {status.lastError}</div>}
      {status?.state === 'TAMAMLANDI' && status.report && (
        <>
          <p className="muted">Son çekme: {formatDateTime(status.lastSuccessUtc)}</p>
          <CatalogReport report={status.report} filter={filter} onFilter={setFilter} />
        </>
      )}
    </Card>
  );
}

function CatalogReport({ report, filter, onFilter }: { report: CatalogImportReport; filter: string; onFilter: (v: string) => void }) {
  if (report.fileErrors.length > 0) {
    return <>{report.fileErrors.map(e => <div key={e} className="alert alert-error">{e}</div>)}</>;
  }
  const matched = report.matchedExisting + report.matchedByBarcode + report.matchedByStockCode + report.matchedByModelColorSize;
  const errorRows = new Set(report.errors.map(e => e.row)).size;
  const rows = filter ? report.rows.filter(r => r.outcome === filter) : report.rows;
  return (
    <div className="report" data-testid="catalog-report" style={{ maxHeight: 'none' }}>
      <div className={`alert ${report.validateOnly ? 'alert-warn' : 'alert-ok'}`}>
        {report.validateOnly ? 'ÖNİZLEME — henüz hiçbir şey yazılmadı.' : 'Uygulandı.'} {report.totalRows} satır ·{' '}
        <strong>{matched}</strong> eşleşti (mevcut {report.matchedExisting}, barkod {report.matchedByBarcode}, stok kodu {report.matchedByStockCode},
        model+renk+beden {report.matchedByModelColorSize}) · {report.listingsCreated} yeni / {report.listingsUpdated} güncellenen /{' '}
        {report.listingsUnchanged} değişmeyen eşleştirme
        {report.createMissing && <> · {report.productsCreated} yeni ürün, {report.skusCreated} yeni SKU</>} ·{' '}
        <strong>{report.unmatched}</strong> eşleşmedi ({report.withSuggestion} öneriyle) · <strong>{report.ambiguous}</strong> belirsiz ·{' '}
        <strong>{report.conflicts}</strong> çakışma · {report.passive} pasif · {errorRows > 0 ? <strong>{errorRows} hatalı satır</strong> : 'hatalı satır yok'}
      </div>
      <CatalogNoMatchHelp report={report} />
      {report.warnings.map(w => <div key={w} className="alert alert-warn">{w}</div>)}
      {report.errors.length > 0 && (
        <table className="table" data-testid="catalog-errors">
          <thead><tr><th>Satır</th><th>Sütun</th><th>Hata</th></tr></thead>
          <tbody>
            {report.errors.slice(0, 200).map((e, i) => <tr key={i}><td>{e.row}</td><td>{e.column ?? '—'}</td><td>{e.message}</td></tr>)}
          </tbody>
        </table>
      )}
      <div className="tabs">
        {['', 'CAKISMA', 'BELIRSIZ', 'ESLESME_YOK', 'OLUSTURULDU', 'ESLESTI'].map(o => (
          <button key={o} className={filter === o ? 'active' : ''} onClick={() => onFilter(o)}>
            {o ? outcomeLabels[o] : 'Tümü'} ({o ? report.rows.filter(r => r.outcome === o).length : report.rows.length})
          </button>
        ))}
      </div>
      <div className="actions">
        <ExportButton<CatalogImportRow> fileName="katalog-raporu" sheetName="Katalog raporu" load={() => Promise.resolve(rows)} columns={[
          { header: 'Satır', kind: 'number', value: r => r.row },
          { header: 'Sonuç', value: r => outcomeLabels[r.outcome] ?? r.outcome },
          { header: 'Pasif', kind: 'bool', value: r => r.passive },
          { header: 'Barkod', value: r => r.barcode },
          { header: 'Kanal ürün kodu', value: r => r.channelProductCode },
          { header: 'Stok kodu', value: r => r.stockCode },
          { header: 'Ürün', value: r => r.name },
          { header: 'Renk', value: r => r.color },
          { header: 'Beden', value: r => r.size },
          { header: 'SKU', value: r => r.skuCode },
          { header: 'Adım', value: r => (r.step ? stepLabels[r.step] ?? r.step : null) },
          { header: 'Açıklama', value: r => r.detail },
          { header: 'Önerilen SKU', value: r => r.suggestedSkuCode }
        ]} />
        {report.errors.length > 0 && <ExportButton fileName="katalog-hatalari" sheetName="Hatalar" tableSelector="[data-testid=catalog-errors]" />}
      </div>
      <div className="table-wrap" style={{ maxHeight: 420, overflow: 'auto' }}>
        <table className="table" data-testid="catalog-rows">
          <thead>
            <tr><th>Satır</th><th>Sonuç</th><th>Barkod</th><th>Stok kodu</th><th>Ürün</th><th>Renk / Beden</th><th>SKU</th><th>Açıklama / öneri</th></tr>
          </thead>
          <tbody>{rows.slice(0, 500).map(r => <CatalogRowView key={r.row} r={r} />)}</tbody>
        </table>
      </div>
      {(report.rowsTruncated || rows.length > 500) && <p className="muted">Tablo ilk satırları gösteriyor; tamamı uygulanır.</p>}
    </div>
  );
}

function CatalogRowView({ r }: { r: CatalogImportRow }) {
  const tone = r.outcome === 'ESLESTI' || r.outcome === 'OLUSTURULDU' ? 'ok' : 'warn';
  return (
    <tr>
      <td>{r.row}</td>
      <td><span className={`badge ${tone}`}>{outcomeLabels[r.outcome] ?? r.outcome}</span>{r.passive && <span className="badge">pasif</span>}</td>
      <td>{r.barcode ?? '—'}</td>
      <td>{r.stockCode ?? '—'}</td>
      <td>{r.name ?? '—'}</td>
      <td>{[r.color, r.size].filter(Boolean).join(' / ') || '—'}</td>
      <td>{r.skuCode ?? '—'}{r.step && <div className="muted">{stepLabels[r.step] ?? r.step}</div>}</td>
      <td style={{ whiteSpace: 'normal', minWidth: 260 }}>
        {r.detail}
        {r.suggestedSkuCode && <div className="muted">Öneri: {r.suggestedSkuCode} (benzerlik {Math.round((r.suggestionScore ?? 0) * 100)}%)</div>}
      </td>
    </tr>
  );
}

/** Eşleşmeyen kanal ürünleri: öneri yalnızca ön doldurur; bağlama kullanıcının onayıyla yapılır. */
export function UnmatchedChannelItems({ channels, canWrite, refreshKey, onLinked }:
  { channels: Channel[]; canWrite: boolean; refreshKey: number; onLinked: () => void }) {
  const [form, setForm] = useState({ channelId: '', status: 'BEKLIYOR', search: '' });
  const [applied, setApplied] = useState(form);
  const [page, setPage] = useState(1);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const items = useLoad(() => get<Paged<UnmatchedItem>>(`/api/channel-catalog/unmatched${qs({ ...applied, page, pageSize: 50 })}`),
    [applied, page, refreshKey]);

  async function link(item: UnmatchedItem, skuCode: string) {
    setError(null);
    try {
      const sku = await resolveSku(skuCode);
      if (!sku) throw new Error(`SKU bulunamadı: ${skuCode}`);
      await post(`/api/channel-catalog/unmatched/${item.id}/link`, { skuId: sku.id });
      setNotice(`${item.channelBarcode ?? item.channelProductCode} → ${sku.skuCode} bağlandı.`);
      items.reload();
      onLinked();
    } catch (err) {
      setError(err);
    }
  }

  async function ignore(item: UnmatchedItem) {
    setError(null);
    try {
      await post(`/api/channel-catalog/unmatched/${item.id}/ignore`);
      items.reload();
    } catch (err) {
      setError(err);
    }
  }

  const data = items.data;
  return (
    <Card title={`Eşleşmeyen kanal ürünleri${data ? ` (${data.total})` : ''}`} actions={<ExportButton fileName="eslesmeyen-kanal-urunleri" sheetName="Eşleşmeyen ürünler"
      columns={unmatchedExportColumns} load={() => fetchAllPages((p, size) => get<Paged<UnmatchedItem>>(`/api/channel-catalog/unmatched${qs({ ...applied, page: p, pageSize: size })}`))} />}>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(form); }}>
        <select aria-label="Kanal (eşleşmeyen)" value={form.channelId} onChange={e => setForm({ ...form, channelId: e.target.value })}>
          <option value="">Tüm kanallar</option>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
        </select>
        <select aria-label="Durum" value={form.status} onChange={e => setForm({ ...form, status: e.target.value })}>
          <option value="BEKLIYOR">Bekleyen</option><option value="BAGLANDI">Bağlanan</option>
          <option value="YOKSAYILDI">Yok sayılan</option><option value="TUMU">Tümü</option>
        </select>
        <input placeholder="Barkod / stok kodu / model / ad" value={form.search} onChange={e => setForm({ ...form, search: e.target.value })} aria-label="Ara (eşleşmeyen)" />
        <button type="submit">Ara</button>
      </form>
      <ErrorBox error={error ?? items.error} />
      <div className="table-wrap">
        <table className="table" data-testid="unmatched-table">
          <thead>
            <tr><th>Kanal</th><th>Barkod / kod</th><th>Ürün</th><th>Model · renk · beden</th><th>Neden</th><th>Son görülme</th>{canWrite && <th>İşlem</th>}</tr>
          </thead>
          <tbody>
            {items.loading && !data && <tr><td className="empty" colSpan={7}>Yükleniyor…</td></tr>}
            {data && data.items.length === 0 && <tr><td className="empty" colSpan={7}>Kayıt yok.</td></tr>}
            {data?.items.map(item => (
              <UnmatchedRow key={item.id} item={item} canWrite={canWrite} onLink={link} onIgnore={ignore} />
            ))}
          </tbody>
        </table>
      </div>
      {data && data.total > data.pageSize && (
        <div className="filters">
          <button disabled={page <= 1} onClick={() => setPage(page - 1)}>Önceki</button>
          <span className="muted">Sayfa {page} / {Math.ceil(data.total / data.pageSize)}</span>
          <button disabled={page * data.pageSize >= data.total} onClick={() => setPage(page + 1)}>Sonraki</button>
        </div>
      )}
    </Card>
  );
}

function UnmatchedRow({ item, canWrite, onLink, onIgnore }:
  { item: UnmatchedItem; canWrite: boolean; onLink: (item: UnmatchedItem, sku: string) => void; onIgnore: (item: UnmatchedItem) => void }) {
  // Öneri yalnızca kutuyu ön doldurur; bağlamak için kullanıcı "Bağla"ya basar.
  const [sku, setSku] = useState(item.suggestedSkuCode ?? '');
  const pending = item.status === 'BEKLIYOR' || item.status === 'YOKSAYILDI';
  return (
    <tr data-testid="unmatched-row">
      <td><ChannelBadge code={item.channelCode} /></td>
      <td>{item.channelBarcode ?? '—'}{item.channelProductCode && <div className="muted">{item.channelProductCode}</div>}
        {item.channelStockCode && <div className="muted">stok: {item.channelStockCode}</div>}</td>
      <td style={{ whiteSpace: 'normal', minWidth: 200 }}><div className="with-thumb"><Thumb url={item.imageUrl} alt={item.productName ?? 'ürün'} /><div>
        {item.productName ?? '—'}{item.brand && <div className="muted">{item.brand}</div>}
        {item.channelStatus && <div className="muted">kanal: {item.channelStatus}</div>}</div></div></td>
      <td>{[item.modelCode, item.color, item.size].filter(Boolean).join(' · ') || '—'}</td>
      <td style={{ whiteSpace: 'normal', minWidth: 220 }}>
        <span className="badge warn">{reasonLabels[item.reason] ?? item.reason}</span>
        {item.status !== 'BEKLIYOR' && <span className="badge">{item.status}</span>}
        <div className="muted">{item.reasonDetail}</div>
        {item.suggestedSkuCode && (
          <div>Öneri: <strong>{item.suggestedSkuCode}</strong> {item.suggestedProductName} ({Math.round((item.suggestionScore ?? 0) * 100)}%)</div>
        )}
      </td>
      <td>{formatDateTime(item.lastSeenUtc)}</td>
      {canWrite && (
        <td>
          {pending ? (
            <div className="filters" style={{ margin: 0 }}>
              <input value={sku} onChange={e => setSku(e.target.value)} placeholder="SKU kodu" aria-label="Bağlanacak SKU" style={{ width: 160 }} />
              <button className="primary" disabled={!sku.trim()} onClick={() => onLink(item, sku)}>Bağla</button>
              {item.status === 'BEKLIYOR' && <button onClick={() => onIgnore(item)}>Yok say</button>}
            </div>
          ) : (
            <span className="muted">{item.resolvedBy}</span>
          )}
        </td>
      )}
    </tr>
  );
}
