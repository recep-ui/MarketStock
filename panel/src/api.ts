// API istemcisi. Hatalar kullanıcıya sadeleştirilmiş Türkçe mesajla gösterilir; ham sunucu ayrıntısı gösterilmez.

export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors?: Record<string, string[]>) {
    super(message);
  }
}

type ProblemDetails = { title?: string; detail?: string; errors?: Record<string, string[]>; status?: number };

/** Sunucu yanıtını kullanıcı dostu mesaja çevirir. */
export function simplifyError(status: number, problem?: ProblemDetails): string {
  if (status === 401) return 'Oturumunuz sona erdi. Lütfen tekrar giriş yapın.';
  if (status === 403) return 'Bu işlem için yetkiniz yok.';
  if (status === 404 && !problem?.detail) return 'Kayıt bulunamadı.';
  if (status >= 500) return 'Beklenmeyen bir hata oluştu. Lütfen biraz sonra tekrar deneyin.';
  if (problem?.errors) {
    const messages = Object.values(problem.errors).flat();
    if (messages.length > 0) return messages.join(' ');
  }
  return problem?.detail || problem?.title || 'İşlem tamamlanamadı.';
}

let onUnauthorized: () => void = () => {};
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

async function handle<T>(response: Response): Promise<T> {
  if (response.ok) {
    if (response.status === 204 || response.status === 202) return undefined as T;
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
  let problem: ProblemDetails | undefined;
  try {
    problem = await response.json();
  } catch {
    problem = undefined;
  }
  if (response.status === 401) onUnauthorized();
  throw new ApiError(response.status, simplifyError(response.status, problem), problem?.errors);
}

export async function api<T>(method: string, url: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
  } catch {
    throw new ApiError(0, 'Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.');
  }
  return handle<T>(response);
}

export const get = <T>(url: string) => api<T>('GET', url);
export const post = <T>(url: string, body?: unknown) => api<T>('POST', url, body);
export const put = <T>(url: string, body?: unknown) => api<T>('PUT', url, body);

export async function upload<T>(url: string, file: File): Promise<T> {
  const form = new FormData();
  form.append('file', file);
  let response: Response;
  try {
    response = await fetch(url, { method: 'POST', body: form, credentials: 'same-origin' });
  } catch {
    throw new ApiError(0, 'Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.');
  }
  // 422: dosya düzeyi hata raporu (gövde ImportReport)
  if (response.status === 422) return (await response.json()) as T;
  return handle<T>(response);
}

/** Dosyayı indirir (şablon / dışa aktarma). */
export async function download(url: string, fallbackName: string) {
  const response = await fetch(url, { credentials: 'same-origin' });
  if (!response.ok) await handle(response);
  const blob = await response.blob();
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = match ? decodeURIComponent(match[1]) : fallbackName;
  a.click();
  URL.revokeObjectURL(a.href);
}

/** Sorgu dizesi; boş değerler atlanır. */
export function qs(params: Record<string, string | number | boolean | undefined | null>) {
  const s = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '' && v !== false) s.set(k, String(v));
  }
  const text = s.toString();
  return text ? `?${text}` : '';
}

// ----------------------------------------------------------------- tipler (API sözleşmeleri)

export type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };
export type OrderStatus = 'Yeni' | 'Onaylandi' | 'Hazirlaniyor' | 'Kargoda' | 'TeslimEdildi' | 'Iptal' | 'Iade';
export type ReturnListItem = {
  id: number; channelId: number; channelCode: string; returnNo: string; channelOrderNo: string; orderId: number | null; channelStatus: string | null;
  reason: string | null; requestedAtUtc: string; lastUpdatedUtc: string; lineCount: number; receivedLineCount: number;
  lastDecision: string | null; lastDecisionState: string | null;
};
export type ReturnDecisionRule = { supported: boolean; note: string | null; requiresReason: boolean; allowedReasons: string[] | null };
export type ReturnDetail = {
  summary: ReturnListItem;
  lines: { id: number; lineKey: string; barcode: string | null; channelProductCode: string | null; orderLineId: number | null; skuId: number | null;
           skuCode: string | null; quantity: number; received: boolean; receiveProblem: string | null }[];
  decisions: { id: number; decision: string; reasonCode: string | null; description: string | null; user: string | null; atUtc: string;
               commandId: number | null; commandState: string | null; commandError: string | null }[];
  rules: { decision: string; rule: ReturnDecisionRule }[];
};
// ------------------------------------------------------------------ Fiyat (ADR-028)
export type ChannelPriceRule = { multiplier: number; fixedAdd: number; rounding: string; floor: number | null };
export type ChannelPriceInfo = {
  channelId: number; channelCode: string; rule: ChannelPriceRule; capabilityState: string; supported: boolean; vatIncluded: boolean | null;
  sendsListPrice: boolean; samePriceForProductVariants: boolean; note: string; source: string | null; canEnable: boolean; blockReason: string | null;
};
export type PricePreviewRow = {
  listingId: number; skuId: number; skuCode: string; productName: string; channelProductCode: string | null; channelBarcode: string | null;
  lastSentSale: number | null; lastSentList: number | null; newSale: number | null; newList: number | null; changePercent: number | null;
  floorApplied: boolean; fromOverride: boolean; status: string; problem: string | null; pendingApprovalId: number | null;
  channelSalePrice?: number | null; channelListPrice?: number | null; productUrl?: string | null;
};
export type PricePreview = {
  channelId: number; channelCode: string; capabilityState: string; canEnable: boolean; blockReason: string | null; thresholdPercent: number;
  total: number; changing: number; firstSend: number; needsApproval: number; unchanged: number; blocked: number; rows: PricePreviewRow[];
};
export type PriceApproval = {
  id: number; listingId: number; channelId: number; channelCode: string; skuCode: string; productName: string; oldSale: number | null;
  newSale: number; newList: number; changePercent: number | null; reason: string; state: string; createdUtc: string;
  decidedBy: string | null; decidedUtc: string | null;
};
export type PriceHistory = {
  id: number; type: string; skuId: number | null; skuCode: string | null; listingId: number | null; channelCode: string | null;
  oldSale: number | null; newSale: number | null; oldList: number | null; newList: number | null; detail: string | null; source: string;
  user: string | null; atUtc: string;
};
export type SkuPrice = { skuId: number; skuCode: string; productName: string; listPrice: number | null; salePrice: number | null };

export type ChannelCapability = { capability: string; state: 'KAPALI' | 'DENEME' | 'ACIK'; changedBy: string | null; changedAtUtc: string };
export type ChannelCommand = {
  id: number; channelId: number; channelCode: string; type: string; targetKey: string; payloadJson: string; state: string; attempts: number;
  nextAttemptUtc: string | null; error: string | null; channelOperationNo: string | null; createdBy: string | null; createdAtUtc: string;
  approvedBy: string | null; approvedAtUtc: string | null; sentAtUtc: string | null; errorInfo?: ChannelErrorInfo | null;
};
export type OrderNotificationRow = {
  orderId: number; channelId: number; channelCode: string; channelOrderNo: string; status: OrderStatus; orderDateUtc: string; statusSource: string;
  commandId: number | null; notificationState: string | null; error: string | null; hasChannelIds: boolean;
};
export type OrderNotifyResult = { items: { orderId: number; channelOrderNo: string | null; outcome: string; commandId: number | null; message: string | null }[] };
export type ChannelReferenceDto = {
  id: number; type: string; code: string; name: string; dataJson: string | null; source: 'API' | 'DOKUMAN'; fetchedAtUtc: string; active: boolean;
};
export type ChannelReferenceTypeResult = { type: string; outcome: 'GUNCELLENDI' | 'DESTEKLENMIYOR' | 'HATA'; count: number; deactivated: number; source: string | null; message: string | null };
export type ChannelReferenceView = {
  items: ChannelReferenceDto[];
  status: { channelId: number; lastAttemptUtc: string | null; lastSuccessUtc: string | null; lastError: string | null; results: ChannelReferenceTypeResult[] };
};
export type Channel = { id: number; code: string; name: string; sharePercent: number; active: boolean; settingsJson: string | null; hasCredentials: boolean };
export type OrderListItem = {
  id: number; channelId: number; channelCode: string; channelOrderNo: string; status: OrderStatus; total: number;
  orderDateUtc: string; insufficientStock: boolean; lineCount: number; unmatchedLineCount: number;
};
export type OrderLine = {
  id: number; skuId: number | null; skuCode: string | null; productName: string | null; quantity: number; unitPrice: number;
  commission: number | null; reserved: boolean; shipped: boolean; released: boolean; returnReceived: boolean;
};
export type OrderDetail = OrderListItem & {
  customer: { fullName: string | null; city: string | null; district: string | null } | null;
  cargo: { company: string | null; trackingNumber: string | null; trackingUrl: string | null } | null;
  lines: OrderLine[];
  timeline: { id: number; from: OrderStatus | null; to: OrderStatus; source: string; atUtc: string }[];
};
export type ChannelCell = {
  channelCode: string; listingId: number; status: string; expected: number; lastSent: number | null;
  channelStock: number | null; fetchedAtUtc: string | null; hasDeviation: boolean;
  channelBarcode: string | null; channelProductCode: string | null; productUrl?: string | null; variant?: string | null;
};
export type StockRow = {
  skuId: number; skuCode: string; productName: string; physical: number; reserved: number; safetyStock: number;
  available: number; channels: ChannelCell[]; hasDeviation: boolean;
};
export type Movement = {
  id: number; atUtc: string; skuId: number; skuCode: string; warehouseName: string; type: string; quantity: number;
  sourceType: string | null; sourceId: string | null; user: string | null;
};
export type SyncItem = {
  id: number; listingId: number; skuCode: string; channelCode: string; type: string; status: 'BEKLIYOR' | 'GONDERILDI' | 'HATA';
  attempts: number; error: string | null; batchId: string | null; sentValue: number | null; sentPrice?: number | null; createdUtc: string;
  lastAttemptUtc: string | null; nextAttemptUtc: string | null; errorInfo?: ChannelErrorInfo | null;
};
export type SyncSummary = { channelCode: string; pending: number; inFlight: number; sent: number; error: number; lastSentUtc: string | null };
export type Reconciliation = {
  listingId: number; skuCode: string; channelCode: string; channelProductCode: string | null; channelBarcode: string | null;
  expected: number; lastSent: number | null; channelStock: number | null; fetchedAtUtc: string | null; deviation: number | null;
};
export type PushListingsResult = { queued: number; skipped: { listingId: number; reason: string }[] };
export type IntakeChannelValue = {
  listingId: number; channelCode: string; channelBarcode: string | null; channelProductCode: string | null;
  channelStock: number | null; fetchedAtUtc: string | null; expected: number; syncPending: boolean;
};
export type IntakePreviewRow = {
  skuId: number; skuCode: string; productName: string; physical: number; reserved: number; safetyStock: number; available: number;
  channels: IntakeChannelValue[]; sourceValue: number | null; minValue: number | null; reservedSinceSnapshot: number;
  proposedTarget: number | null; proposedPhysical: number | null; flags: string[]; defaultSelected: boolean;
};
export type IntakePreview = {
  sourceChannelId: number; sourceChannelCode: string; enabled: boolean; disabledReason: string | null; lowStockThreshold: number;
  previewedAtUtc: string; rows: IntakePreviewRow[];
};
export type IntakeRowResult = {
  skuId: number; skuCode: string | null; outcome: 'UYGULANDI' | 'FARK_YOK' | 'REDDEDILDI'; message: string | null;
  physical: number | null; available: number | null; movementId: number | null;
};
export type IntakeApplyResult = {
  batchId: string; applied: number; unchanged: number; rejected: number; pushQueued: number; pushSkipped: number; rows: IntakeRowResult[];
};
export type Listing = {
  id: number; skuId: number; skuCode: string; channelId: number; channelCode: string; channelProductCode: string | null;
  channelBarcode: string | null; status: string; lastSentStock: number | null; lastSyncUtc: string | null;
  productUrl?: string | null; variant?: string | null;
};
export type Warehouse = { id: number; name: string };
export type SkuItem = { id: number; skuCode: string; barcode: string | null; productName: string; physical: number; available: number };
export type ImportReport = {
  kind: string; validateOnly: boolean; totalRows: number; validRows: number; created: number; updated: number; unchanged: number;
  errors: { row: number; column: string | null; message: string }[]; fileErrors: string[];
};
export type CatalogImportRow = {
  row: number; barcode: string | null; channelProductCode: string | null; stockCode: string | null; name: string | null;
  color: string | null; size: string | null; outcome: string; skuCode: string | null; step: string | null; listingChange: string | null;
  detail: string | null; suggestedSkuCode: string | null; suggestionScore: number | null; passive: boolean;
};
export type CatalogImportReport = {
  channelCode: string; format: string | null; source: string; validateOnly: boolean; createMissing: boolean; totalRows: number; validRows: number;
  matchedExisting: number; matchedByBarcode: number; matchedByStockCode: number; matchedByModelColorSize: number;
  listingsCreated: number; listingsUpdated: number; listingsUnchanged: number; productsCreated: number; skusCreated: number;
  unmatched: number; ambiguous: number; conflicts: number; withSuggestion: number; passive: number; pushDeferred: number;
  rows: CatalogImportRow[]; rowsTruncated: boolean;
  errors: { row: number; column: string | null; message: string }[]; fileErrors: string[]; warnings: string[];
};
export type CatalogFetchStatus = {
  channelId: number; state: 'YOK' | 'CALISIYOR' | 'TAMAMLANDI' | 'HATA'; lastAttemptUtc: string | null; lastSuccessUtc: string | null;
  lastError: string | null; report: CatalogImportReport | null;
};
export type UnmatchedItem = {
  id: number; channelId: number; channelCode: string; source: string; channelProductCode: string | null; channelBarcode: string | null;
  channelStockCode: string | null; modelCode: string | null; color: string | null; size: string | null; productName: string | null;
  brand: string | null; channelStatus: string | null; reason: string; reasonDetail: string | null; suggestedSkuId: number | null;
  suggestedSkuCode: string | null; suggestedProductName: string | null; suggestionScore: number | null; status: string;
  firstSeenUtc: string; lastSeenUtc: string; resolvedBy: string | null; resolvedAtUtc: string | null;
};
export type Dashboard = {
  day: string; todayOrders: number; todayRevenue: number;
  byChannel: { channelCode: string; orders: number; revenue: number }[];
  insufficientStockOrders: number; criticalStockThreshold: number;
  criticalStock: { skuId: number; skuCode: string; productName: string; physical: number; reserved: number; available: number }[];
  syncErrors: number; syncPending: number;
  jobs: { channelCode: string; jobType: string; lastSuccessUtc: string | null; lastAttemptUtc: string | null; consecutiveFailures: number; lastError: string | null; errorInfo?: ChannelErrorInfo | null }[];
};
export type User = { id: number; userName: string; fullName: string; role: string; active: boolean; lastLoginUtc: string | null };

/** ADR-033: kayıtlı hata metninin türü ve ne yapılacağı (sunucu sınıflandırır). */
export type ChannelErrorInfo = { kind: string; title: string; advice: string; transient: boolean; problems: string[]; moreCount: number };
export type ChannelJobStatus = {
  jobType: string; lastSuccessUtc: string | null; lastAttemptUtc: string | null; consecutiveFailures: number; lastError: string | null;
  errorInfo: ChannelErrorInfo | null;
};
/** ADR-033: kanal durum kartı. Gizli bilgi içermez. */
export type ChannelStatus = {
  id: number; code: string; name: string; active: boolean; hasCredentials: boolean; mode: 'MOCK' | 'GERCEK'; realAdapterAvailable: boolean;
  environment: string | null; writeLocked: boolean; capabilities: ChannelCapability[]; jobs: ChannelJobStatus[];
  health: 'CALISIYOR' | 'DIKKAT' | 'HATA' | 'SAHTE' | 'KAPALI'; healthReason: string;
};
