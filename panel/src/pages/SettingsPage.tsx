import { useEffect, useState, type FormEvent } from 'react';
import { api, get, post, put, type Channel, type User, type Warehouse } from '../api';
import { roleLabels, useAuth, type Role } from '../auth';
import { Card, ChannelBadge, ErrorBox, Field, Notice, useLoad } from '../components/ui';
import { formatDateTime } from '../format';
import { resolveSku } from './MovementsPage';
import { ChannelReferences } from './ChannelReferences';
import { ChannelWriteSettings } from './ChannelWrite';

/** Ekran 8: Ayarlar (kanal payı, güvenlik stoğu, uyarı eşikleri, kullanıcı rolleri). */
export function SettingsPage() {
  const { can } = useAuth();
  const [notice, setNotice] = useState<string | null>(null);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  return (
    <>
      <h1>Ayarlar</h1>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <ChannelSettings editable={can('admin')} onSaved={setNotice} />
      {channels.data && <ChannelWriteSettings channels={channels.data} editable={can('admin')} onNotice={setNotice} />}
      {channels.data && <ChannelReferences channels={channels.data} canRefresh={can('admin')} onNotice={setNotice} />}
      <div className="grid-2">
        <ThresholdSettings editable={can('admin')} onSaved={setNotice} />
        {can('stockWrite') && <SafetyStockForm onSaved={setNotice} />}
      </div>
      {can('admin') && <UserSettings onSaved={setNotice} />}
    </>
  );
}

function readCap(settingsJson: string | null): string {
  try {
    const v = settingsJson ? JSON.parse(settingsJson).stokUstLimit : undefined;
    return typeof v === 'number' ? String(v) : '';
  } catch {
    return '';
  }
}

function ChannelSettings({ editable, onSaved }: { editable: boolean; onSaved: (m: string) => void }) {
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const [rows, setRows] = useState<Record<number, { share: string; active: boolean; cap: string }>>({});
  const [error, setError] = useState<unknown>(null);
  const [credentialsFor, setCredentialsFor] = useState<number | null>(null);

  useEffect(() => {
    if (channels.data) {
      setRows(Object.fromEntries(channels.data.map(c => [c.id, { share: String(c.sharePercent), active: c.active, cap: readCap(c.settingsJson) }])));
    }
  }, [channels.data]);

  async function save(c: Channel) {
    setError(null);
    const r = rows[c.id];
    let settings: Record<string, unknown> = {};
    try {
      settings = c.settingsJson ? JSON.parse(c.settingsJson) : {};
    } catch {
      settings = {};
    }
    if (r.cap === '') delete settings.stokUstLimit;
    else settings.stokUstLimit = Number(r.cap);
    try {
      await put(`/api/channels/${c.id}`, {
        name: c.name, sharePercent: Number(r.share.replace(',', '.')), active: r.active,
        settingsJson: Object.keys(settings).length ? JSON.stringify(settings) : null
      });
      onSaved(`${c.name} kaydedildi; kanalın stokları yeniden hesaplanıp gönderilecek.`);
      channels.reload();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kanallar">
      <p className="muted small">Kanala gidecek stok = MIN( ⌊satılabilir × pay % / 100⌋, üst limit ). Pay %100: tam ortak havuz.</p>
      <ErrorBox error={error ?? channels.error} />
      <table className="table">
        <thead><tr><th>Kanal</th><th>Pay %</th><th>Üst limit</th><th>Aktif</th><th>API bilgisi</th><th /></tr></thead>
        <tbody>
          {(channels.data ?? []).map(c => {
            const r = rows[c.id];
            if (!r) return null;
            return (
              <tr key={c.id}>
                <td><ChannelBadge code={c.code} /> {c.name}</td>
                <td><input className="narrow" aria-label={`${c.name} pay`} disabled={!editable} value={r.share} onChange={e => setRows({ ...rows, [c.id]: { ...r, share: e.target.value } })} /></td>
                <td><input className="narrow" aria-label={`${c.name} üst limit`} disabled={!editable} placeholder="yok" value={r.cap} onChange={e => setRows({ ...rows, [c.id]: { ...r, cap: e.target.value } })} /></td>
                <td><input type="checkbox" aria-label={`${c.name} aktif`} disabled={!editable} checked={r.active} onChange={e => setRows({ ...rows, [c.id]: { ...r, active: e.target.checked } })} /></td>
                <td>
                  {c.hasCredentials ? 'Girildi' : <span className="muted">Yok</span>}
                  {editable && c.code in credentialSpecs && (
                    <button className="small" onClick={() => setCredentialsFor(credentialsFor === c.id ? null : c.id)}>API bilgisi</button>
                  )}
                </td>
                <td>{editable && <button onClick={() => save(c)}>Kaydet</button>}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {credentialsFor !== null && (
        <CredentialsForm
          channel={channels.data!.find(c => c.id === credentialsFor)!}
          onDone={message => { setCredentialsFor(null); onSaved(message); channels.reload(); }}
        />
      )}
    </Card>
  );
}

type CredentialField = { name: string; label: string; secret?: boolean; numeric?: boolean };
type CredentialSpec = { title: string; hint: string; fields: CredentialField[]; environments: [string, string][] };

/** Kanal başına API bilgisi alanları (sunucudaki *Credentials.Parse ile aynı). */
const credentialSpecs: Record<string, CredentialSpec> = {
  TRENDYOL: {
    title: 'Trendyol API bilgileri',
    hint: 'Satıcı paneli → Hesap Bilgilerim → Entegrasyon Bilgileri.',
    fields: [
      { name: 'sellerId', label: 'Satıcı (supplier) no', numeric: true },
      { name: 'apiKey', label: 'API Key' },
      { name: 'apiSecret', label: 'API Secret', secret: true }
    ],
    environments: [['Prod', 'Canlı'], ['Stage', 'Test (IP beyaz listesi gerekir)']]
  },
  HEPSIBURADA: {
    title: 'Hepsiburada API bilgileri',
    hint: 'Merchant ID ve Hepsiburada\'nın verdiği kullanıcı adı / şifre (servis anahtarı: Bilgilerim → Entegrasyon → Entegratör Bilgileri).',
    fields: [
      { name: 'merchantId', label: 'Merchant ID (GUID)' },
      { name: 'userName', label: 'Kullanıcı adı' },
      { name: 'password', label: 'Şifre / servis anahtarı', secret: true },
      { name: 'userAgent', label: 'User-Agent (opsiyonel)' }
    ],
    environments: [['Prod', 'Canlı'], ['Test', 'Test (SIT)']]
  },
  BEYMEN: {
    title: 'Beymen API bilgileri',
    hint: 'Partner Portal → Hesap Bilgileri → Entegrasyon Bilgileri.',
    fields: [
      { name: 'apiKey', label: 'API Key' },
      { name: 'apiPassword', label: 'API Password', secret: true }
    ],
    environments: [['Prod', 'Canlı'], ['Stage', 'Test (stage)']]
  }
};

/**
 * Kanal API bilgileri formu. Değerler sunucuda şifreli saklanır, hiçbir zaman geri okunmaz/gösterilmez;
 * değiştirmek için yeniden girilir. Gerçek çağrılar için sunucuda "Kanallar:{KOD}:Mod" = Gercek olmalıdır.
 */
function CredentialsForm({ channel, onDone }: { channel: Channel; onDone: (m: string) => void }) {
  const spec = credentialSpecs[channel.code];
  const empty = () => ({ ...Object.fromEntries(spec.fields.map(f => [f.name, ''])), environment: spec.environments[0][0] }) as Record<string, string>;
  const [form, setForm] = useState<Record<string, string>>(empty);
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const body: Record<string, unknown> = { ...form };
      for (const f of spec.fields) {
        if (f.numeric) body[f.name] = Number(form[f.name]);
        if (!f.numeric && form[f.name] === '') delete body[f.name];
      }
      await put(`/api/channels/${channel.id}/credentials`, body);
      setForm(empty());
      onDone(`${spec.title} şifreli olarak kaydedildi.`);
    } catch (err) {
      setError(err);
    }
  }

  async function remove() {
    setError(null);
    try {
      await api('DELETE', `/api/channels/${channel.id}/credentials`);
      onDone(`${spec.title} silindi.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <div className="excel-box">
      <h3>{spec.title}</h3>
      <p className="muted small">
        {spec.hint} Bilgiler şifreli saklanır ve bir daha gösterilmez. Gerçek çağrılar için sunucuda "Kanallar:{channel.code}:Mod" = Gercek olmalıdır.
      </p>
      <ErrorBox error={error} />
      <form className="filters" onSubmit={submit} autoComplete="off">
        {spec.fields.map(f => (
          <Field key={f.name} label={f.label}>
            <input
              type={f.secret ? 'password' : 'text'}
              inputMode={f.numeric ? 'numeric' : undefined}
              autoComplete={f.secret ? 'new-password' : 'off'}
              value={form[f.name]}
              onChange={e => setForm({ ...form, [f.name]: e.target.value })}
              required={!f.label.includes('opsiyonel')}
            />
          </Field>
        ))}
        <Field label="Ortam">
          <select value={form.environment} onChange={e => setForm({ ...form, environment: e.target.value })}>
            {spec.environments.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </Field>
        <button className="primary" type="submit">Kaydet</button>
        {channel.hasCredentials && <button type="button" className="danger" onClick={remove}>Sil</button>}
      </form>
    </div>
  );
}

function ThresholdSettings({ editable, onSaved }: { editable: boolean; onSaved: (m: string) => void }) {
  const settings = useLoad(() => get<{ criticalStockThreshold: number; lowStockThreshold: number | null }>('/api/settings'), []);
  const [value, setValue] = useState('');
  const [lowStock, setLowStock] = useState('');
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (settings.data) {
      setValue(String(settings.data.criticalStockThreshold));
      setLowStock(String(settings.data.lowStockThreshold ?? 0));
    }
  }, [settings.data]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await put('/api/settings', { criticalStockThreshold: Number(value), lowStockThreshold: Number(lowStock) });
      onSaved('Eşikler kaydedildi. Düşük stok eşiği değiştiyse tüm ilanlar yeniden hesaplanıp kanallara gönderilir.');
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Uyarı eşikleri">
      <p className="muted">
        Kritik stok eşiği yalnızca panelde uyarı gösterir. Kanala 0 gönderme eşiği tüm ürün ve kanallar için geçerlidir: satılabilir stok bu
        değere eşit veya altındaysa tüm kanallardaki tüm ilanlara 0 gönderilir (0 = kapalı).
      </p>
      <ErrorBox error={error ?? settings.error} />
      <form className="filters" onSubmit={submit}>
        <Field label="Kritik stok eşiği (satılabilir ≤)">
          <input type="number" min={0} disabled={!editable} value={value} onChange={e => setValue(e.target.value)} />
        </Field>
        <Field label="Kanala 0 gönderme eşiği (satılabilir ≤)">
          <input type="number" min={0} disabled={!editable} value={lowStock} onChange={e => setLowStock(e.target.value)}
            aria-label="Kanala 0 gönderme eşiği" />
        </Field>
        {editable && <button className="primary" type="submit">Kaydet</button>}
      </form>
    </Card>
  );
}

function SafetyStockForm({ onSaved }: { onSaved: (m: string) => void }) {
  const warehouses = useLoad(() => get<Warehouse[]>('/api/warehouses'), []);
  const [form, setForm] = useState({ sku: '', warehouseId: '', quantity: '' });
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const sku = await resolveSku(form.sku);
      if (!sku) throw new Error(`SKU bulunamadı: ${form.sku}`);
      const result = await put<{ available: number }>('/api/stock/safety-stock', {
        skuId: sku.id, warehouseId: Number(form.warehouseId || warehouses.data?.[0]?.id), quantity: Number(form.quantity)
      });
      onSaved(`${sku.skuCode} güvenlik stoğu kaydedildi; satılabilir ${result.available}.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Güvenlik stoğu">
      {error ? <div className="alert alert-error">{(error as Error).message}</div> : null}
      <form className="filters" onSubmit={submit}>
        <Field label="SKU kodu"><input value={form.sku} onChange={e => setForm({ ...form, sku: e.target.value })} required /></Field>
        <Field label="Depo">
          <select value={form.warehouseId} onChange={e => setForm({ ...form, warehouseId: e.target.value })}>
            {(warehouses.data ?? []).map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
        </Field>
        <Field label="Adet"><input type="number" min={0} value={form.quantity} onChange={e => setForm({ ...form, quantity: e.target.value })} required /></Field>
        <button className="primary" type="submit">Kaydet</button>
      </form>
    </Card>
  );
}

function UserSettings({ onSaved }: { onSaved: (m: string) => void }) {
  const users = useLoad(() => get<User[]>('/api/users'), []);
  const [form, setForm] = useState({ userName: '', fullName: '', role: 'Depo' as Role, password: '' });
  const [error, setError] = useState<unknown>(null);
  const roles = Object.keys(roleLabels) as Role[];

  async function run(action: () => Promise<unknown>, message: string) {
    setError(null);
    try {
      await action();
      onSaved(message);
      users.reload();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kullanıcılar ve roller">
      <p className="muted small">Yönetici: tüm işlemler · Depo: stok, sipariş durumu, iade girişi · Muhasebe: yalnızca okuma · Destek: okuma, müşteri bilgisi maskeli.</p>
      <ErrorBox error={error ?? users.error} />
      <table className="table">
        <thead><tr><th>Kullanıcı</th><th>Ad soyad</th><th>Rol</th><th>Aktif</th><th>Son giriş</th><th /></tr></thead>
        <tbody>
          {(users.data ?? []).map(u => (
            <tr key={u.id}>
              <td>{u.userName}</td>
              <td>{u.fullName}</td>
              <td>
                <select aria-label={`${u.userName} rol`} value={u.role}
                  onChange={e => run(() => put(`/api/users/${u.id}`, { fullName: u.fullName, role: e.target.value, active: u.active }), `${u.userName} rolü güncellendi.`)}>
                  {roles.map(r => <option key={r} value={r}>{roleLabels[r]}</option>)}
                </select>
              </td>
              <td>
                <input type="checkbox" aria-label={`${u.userName} aktif`} checked={u.active}
                  onChange={e => run(() => put(`/api/users/${u.id}`, { fullName: u.fullName, role: u.role, active: e.target.checked }), `${u.userName} güncellendi.`)} />
              </td>
              <td>{formatDateTime(u.lastLoginUtc)}</td>
              <td>
                <button className="small" onClick={() => {
                  const password = window.prompt(`${u.userName} için yeni parola (en az 10 karakter):`);
                  if (password) run(() => post(`/api/users/${u.id}/password`, { password }), `${u.userName} parolası değiştirildi.`);
                }}>Parola</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <h3>Yeni kullanıcı</h3>
      <form className="filters" onSubmit={e => {
        e.preventDefault();
        run(() => post('/api/users', form), `${form.userName} oluşturuldu.`).then(() => setForm({ ...form, userName: '', fullName: '', password: '' }));
      }}>
        <Field label="Kullanıcı adı"><input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })} required /></Field>
        <Field label="Ad soyad"><input value={form.fullName} onChange={e => setForm({ ...form, fullName: e.target.value })} required /></Field>
        <Field label="Rol">
          <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value as Role })}>
            {roles.map(r => <option key={r} value={r}>{roleLabels[r]}</option>)}
          </select>
        </Field>
        <Field label="Parola"><input type="password" autoComplete="new-password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} required minLength={10} /></Field>
        <button className="primary" type="submit">Oluştur</button>
      </form>
    </Card>
  );
}
