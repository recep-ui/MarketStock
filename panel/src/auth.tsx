import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { get, post, setUnauthorizedHandler } from './api';

export type Role = 'Yonetici' | 'Depo' | 'Muhasebe' | 'Destek';
export type Me = { userName: string; fullName: string; role: Role };

/** Sunucudaki politikalarla aynı (ADR-014). Arayüz yalnızca gizler; asıl yetki kontrolü API'dedir. */
const policies = {
  read: ['Yonetici', 'Depo', 'Muhasebe', 'Destek'],
  stockWrite: ['Yonetici', 'Depo'],
  orderWrite: ['Yonetici', 'Depo'],
  catalogWrite: ['Yonetici'],
  admin: ['Yonetici']
} satisfies Record<string, Role[]>;
export type Policy = keyof typeof policies;

export const roleLabels: Record<Role, string> = { Yonetici: 'Yönetici', Depo: 'Depo', Muhasebe: 'Muhasebe', Destek: 'Destek' };

type AuthState = {
  me: Me | null;
  loading: boolean;
  login: (userName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  can: (policy: Policy) => boolean;
};

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null));
    get<Me>('/api/auth/me').then(setMe).catch(() => setMe(null)).finally(() => setLoading(false));
  }, []);

  const login = useCallback(async (userName: string, password: string) => {
    setMe(await post<Me>('/api/auth/login', { userName, password }));
  }, []);

  const logout = useCallback(async () => {
    await post('/api/auth/logout');
    setMe(null);
  }, []);

  const can = useCallback((policy: Policy) => !!me && (policies[policy] as Role[]).includes(me.role), [me]);

  return <AuthContext.Provider value={{ me, loading, login, logout, can }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('AuthProvider eksik');
  return ctx;
}
