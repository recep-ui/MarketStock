import { Link } from 'react-router-dom';
import type { CatalogImportReport, ChannelStatus } from '../api';
import { channelNames } from '../format';

/**
 * Kanaldan gelen verinin gerçek mağaza verisi olmadığı durumlarda uyarı (ADR-038):
 * - Mock (sahte) mod: deneme modunda gerçek kanala hiç gidilmez; gelen ürün/stok yoktur ya da demo verisidir.
 * - Test ortamı (ör. Hepsiburada SIT): kanal API bilgisi test ortamına bağlı; gelen ürünler kanalın deneme ilanlarıdır,
 *   gerçek mağazadaki ürünlerle eşleşmez ve deftere alınmamalıdır.
 */
export function ChannelDataSourceNote({ status }: { status: ChannelStatus | null | undefined }) {
  if (!status) return null;
  const name = channelNames[status.code] ?? status.name;
  if (status.mode === 'MOCK') {
    return (
      <div className="alert alert-warn" data-testid="channel-source-mock">
        <strong>{name} sahte (mock) modda çalışıyor.</strong> Gerçek {name}'a istek gitmez; buradan gelen ürün ve stoklar gerçek mağazanız
        değildir. Gerçek veri için deneme modu bu kanal gerçek modda başlatılmalı (ör. <code>-GercekKanal {status.code}</code>).
      </div>
    );
  }
  if (status.environment != null && status.environment !== 'Prod') {
    return (
      <div className="alert alert-warn" data-testid="channel-source-test">
        <strong>{name} TEST ortamına bağlı ({status.environment}).</strong> Gelen ürünler {name}'ın deneme ilanlarıdır; gerçek mağazanızdaki
        ürünlerle eşleşmez ve deftere alınmamalıdır. Gerçek ürünler için canlı API bilgilerini girip ortamı “Canlı” yapın
        (<Link to={`/ayarlar?sekme=kanallar&kanal=${status.id}`}>Ayarlar → Kanallar</Link>).
      </div>
    );
  }
  return null;
}

/** Katalog çekme/içe alma hiçbir ürünü eşleştiremediğinde nedenleri (rapordaki satırlardan) ve sonraki adım. */
export function CatalogNoMatchHelp({ report }: { report: CatalogImportReport }) {
  const matched = report.matchedExisting + report.matchedByBarcode + report.matchedByStockCode + report.matchedByModelColorSize;
  if (matched > 0 || report.unmatched === 0 || report.skusCreated > 0) return null;
  const unmatchedRows = report.rows.filter(r => r.outcome === 'ESLESME_YOK');
  const noBarcode = unmatchedRows.filter(r => !r.barcode).length;
  const withStockCode = unmatchedRows.filter(r => r.stockCode).length;
  return (
    <div className="alert alert-warn" data-testid="catalog-no-match">
      <strong>Hiçbir ürün defterdeki bir SKU'ya bağlanmadı.</strong> Bu ürünler için kanal ilanı oluşmadığından Kanal Stoğu → Defter ve
      stok senkronu bu ürünleri göremez.
      <ul>
        {withStockCode > 0 && (
          <li>{withStockCode} ürünün stok kodu, defterdeki hiçbir SKU koduyla aynı değil.</li>
        )}
        {noBarcode > 0 && (
          <li>
            {noBarcode} üründe barkod yok: barkodla eşleşemez{report.createMissing ? ' ve barkodsuz üründen SKU oluşturulmaz' : ''}.
          </li>
        )}
        <li>
          Doğru olduğunu bildiğiniz ürünleri aşağıdaki “Eşleşmeyen kanal ürünleri” listesinden bir SKU'ya elle bağlayabilir ya da kanal
          panelinde satıcı stok kodunu defterdeki SKU koduyla aynı yapabilirsiniz.
        </li>
      </ul>
    </div>
  );
}
