import { createContext, useContext } from 'react'
import type { Me } from '../api'

export type Kind = 'customer' | 'quote' | 'documents' | 'settings' | 'taxcodes' | 'taxgroups' | 'pricing'
export interface Shell {
  me: Me
  /** Open (or focus) a window. `arg` is a customer id or document id; omitted = a blank entry window. */
  open: (kind: Kind, arg?: number) => void
  close: (id: string) => void
  setTitle: (id: string, title: string) => void
  refreshMe: () => void
  toast: (message: string, kind?: 'ok' | 'error') => void
}
export const ShellContext = createContext<Shell | null>(null)
export const useShell = () => useContext(ShellContext)!
export const WinIdContext = createContext<string>('')
export const useWinId = () => useContext(WinIdContext)
