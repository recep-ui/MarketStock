import { useState, type FormEvent } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../auth';
import { ErrorBox, Field } from '../components/ui';

export function LoginPage() {
  const { me, login } = useAuth();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  if (me) return <Navigate to="/" replace />;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(userName, password);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login">
      <form className="card login-card" onSubmit={submit}>
        <h1>PazaryeriHub</h1>
        <p className="muted">Stok ve sipariş yönetim paneli</p>
        <ErrorBox error={error} />
        <Field label="Kullanıcı adı">
          <input name="userName" autoComplete="username" value={userName} onChange={e => setUserName(e.target.value)} required />
        </Field>
        <Field label="Parola">
          <input name="password" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required />
        </Field>
        <button className="primary" type="submit" disabled={busy}>{busy ? 'Giriş yapılıyor…' : 'Giriş yap'}</button>
      </form>
    </div>
  );
}
