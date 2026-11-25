import { Link } from 'react-router-dom';
import type { ChannelStockListRow } from '../api';
import { formatMoney } from '../format';

/**
 * İlanın stok ve fiyat özeti (salt okuma, ADR-052). Stok Listesi → kanal sekmesinde gösterilir; düzenleme yalnızca
 * "Fiyat & Stok Düzenle" ekranından yapılır (ADR-050/051). "Düzenle" bağlantısı o ekranı SKU ile aranmış açar.
 */
export function ListingSummary({ row }: { row: ChannelStockListRow }) {
  const rule = row.stockRuleType === 'SABIT' ? `kanala özel ${row.stockRuleValue}`
    : row.stockRuleType === 'UST_LIMIT' ? `en fazla ${row.stockRuleValue}` : 'ortak stok';
  const price = row.priceOverride ?? row.skuSalePrice;
  return (
    <div className="small" data-testid="listing-summary">
      <div>
        <span className="muted">Stok: </span><span data-testid="listing-rule">{rule}</span>
        {' · '}gidecek <strong data-testid="listing-target">{row.expected}</strong>
        {row.lastSent != null && <span className="muted"> · son gönderilen {row.lastSent}</span>}
      </div>
      <div>
        <span className="muted">Fiyat: </span>
        {price != null ? formatMoney(price) : <span className="muted">tanımlı değil</span>}
        {row.priceOverride != null && <span className="badge" title="Bu kanal için elle girildi">elle</span>}
        {row.channelSalePrice != null && <span className="muted"> · kanalda {formatMoney(row.channelSalePrice)}</span>}
        {row.lastSentSalePrice != null && <span className="muted"> · son gönderilen {formatMoney(row.lastSentSalePrice)}</span>}
      </div>
      <Link to={`/urun-duzenle?ara=${encodeURIComponent(row.skuCode)}`} data-testid="listing-edit-link">Düzenle →</Link>
    </div>
  );
}
