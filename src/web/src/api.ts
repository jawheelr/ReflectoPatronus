// Thin API client. Access token lives in memory only; the refresh token in sessionStorage (cleared when the tab
// closes). Both are opaque, revocable per device. A 401 triggers one refresh-and-retry.
export interface UserInfo { id: number; code: string; name: string | null; timeZone: string }
interface TokenResponse { accessToken: string; refreshToken: string; expiresInSeconds: number; user: UserInfo }
export interface Me { id: number; code: string; name: string | null; timeZone: string; userTimeZone: string | null; companyTimeZone: string; isAdmin: boolean }
export interface SessionInfo { id: string; deviceName: string; platform: string; createdAt: string; lastSeenAt: string; current: boolean }
export interface CustomerSummary { id: number; number: string; lastName: string; firstName: string | null; city: string | null; state: string | null; phone: string | null; balance: number }
export interface CustomerPhone { slot: number; number: string; ext: string | null }
export interface CustomerDetail extends CustomerSummary {
  type: string; address1: string | null; address2: string | null; zip: string | null; email: string | null
  contactName: string | null; taxGroup: string | null; taxExempt: boolean; taxId: string | null
  priceCategory: string | null; category: string | null; printStatement: boolean; noPrices: boolean
  creditLimit: number | null; lastTransactionOn: string | null; notePrinted: string | null; noteInternal: string | null
  phones: CustomerPhone[]
}
export interface Page<T> { items: T[]; limit: number; offset: number; hasMore: boolean }

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

const RT = 'rp.refresh'
let accessToken: string | null = null
let onSignedOut: () => void = () => {}
export const setSignedOutHandler = (f: () => void) => { onSignedOut = f }
export const hasStoredSession = () => sessionStorage.getItem(RT) !== null

async function raw(path: string, init: RequestInit = {}, auth = true): Promise<Response> {
  const headers = new Headers(init.headers)
  if (init.body) headers.set('Content-Type', 'application/json')
  if (auth && accessToken) headers.set('Authorization', `Bearer ${accessToken}`)
  return fetch(`/api/v1${path}`, { ...init, headers })
}

async function errorOf(r: Response): Promise<ApiError> {
  let msg = r.statusText
  try { msg = (await r.json()).error ?? msg } catch { /* not JSON */ }
  return new ApiError(r.status, msg)
}

function store(t: TokenResponse) { accessToken = t.accessToken; sessionStorage.setItem(RT, t.refreshToken) }

let refreshing: Promise<boolean> | null = null
export function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    const rt = sessionStorage.getItem(RT)
    if (!rt) return false
    const r = await raw('/auth/refresh', { method: 'POST', body: JSON.stringify({ refreshToken: rt }) }, false)
    if (!r.ok) { accessToken = null; sessionStorage.removeItem(RT); return false }
    store(await r.json()); return true
  })().finally(() => { refreshing = null })
  return refreshing
}

async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  let r = await raw(path, init)
  if (r.status === 401 && await refresh()) r = await raw(path, init)
  if (r.status === 401) { onSignedOut(); throw await errorOf(r) }
  if (!r.ok) throw await errorOf(r)
  return r.status === 204 ? (undefined as T) : r.json()
}

export async function login(userCode: string, password: string): Promise<UserInfo> {
  const deviceName = `Web browser (${navigator.platform || 'unknown'})`
  const r = await raw('/auth/login', { method: 'POST', body: JSON.stringify({ userCode, password, deviceName, platform: 'web' }) }, false)
  if (!r.ok) throw await errorOf(r)
  const t: TokenResponse = await r.json(); store(t); return t.user
}
export async function logout() {
  try { await raw('/auth/logout', { method: 'POST' }) } finally { accessToken = null; sessionStorage.removeItem(RT) }
}

export const getMe = () => call<Me>('/me')
export const setTimeZone = (timeZone: string | null) => call<void>('/me/settings', { method: 'PUT', body: JSON.stringify({ timeZone }) })
export const getTimeZones = () => call<string[]>('/timezones')
export const getSessions = () => call<SessionInfo[]>('/me/sessions')
export const revokeSession = (id: string) => call<void>(`/me/sessions/${id}`, { method: 'DELETE' })
export const searchCustomers = (q: string, offset = 0, limit = 25, sort: 'name' | 'code' = 'name') =>
  call<Page<CustomerSummary>>(`/customers?q=${encodeURIComponent(q)}&sort=${sort}&limit=${limit}&offset=${offset}`)
export const getCustomer = (id: number) => call<CustomerDetail>(`/customers/${id}`)

export interface DocumentSummary { id: number; type: string; number: string; date: string; customerId: number | null; customerName: string | null; poNumber: string | null; voided: boolean; total: number }
export interface DocumentLine { seq: number; parentSeq: number | null; source: string; qty: number | null; partCode: string | null; description: string | null; dims: string | null; shape: string | null; thickness: string | null; unitPrice: number | null; listPrice: number | null; multiplier: number; amount: number; taxable: boolean }
export interface DocumentParty { name: string | null; address1: string | null; address2: string | null; city: string | null; state: string | null; zip: string | null; phones: string[] }
export interface DocumentDetail {
  id: number; type: string; typeName: string; number: string; date: string; lossDate: string | null; voided: boolean
  customerId: number | null; billToCustomerId: number | null; customer: DocumentParty; jobSite: DocumentParty | null
  poNumber: string | null; policyNumber: string | null; claimNumber: string | null; sourceDocumentNumber: string | null
  terms: string | null; salesPerson: string | null; taxGroup: string | null; priceCategory: string | null; discountPercent: number | null
  specialInstructions: string | null; workOrderInstructions: string | null; notePrinted: string | null; noteInternal: string | null
  createdAt: string; printedAt: string | null
  totals: { lines: number; taxable: number; tax: number; total: number; glass: number; part: number; labor: number; custom: number }
  lines: DocumentLine[]; taxes: { code: string; percent: number; amount: number }[]
  installers: { code: string; name: string | null; hours: number | null; amount: number | null }[]
  statuses: { at: string | null; status: string; note: string | null }[]; revisions: { at: string; total: number | null; comment: string | null }[]
  shopOrMobile: string | null
}
export interface DocumentQuery { type?: string; customerId?: number; q?: string; from?: string; to?: string; includeVoided?: boolean; offset?: number; limit?: number }
export const searchDocuments = (f: DocumentQuery) => {
  const p = new URLSearchParams()
  if (f.type) p.set('type', f.type)
  if (f.customerId) p.set('customerId', String(f.customerId))
  if (f.q) p.set('q', f.q)
  if (f.from) p.set('from', f.from)
  if (f.to) p.set('to', f.to)
  if (f.includeVoided) p.set('includeVoided', 'true')
  p.set('limit', String(f.limit ?? 25)); p.set('offset', String(f.offset ?? 0))
  return call<Page<DocumentSummary>>(`/documents?${p}`)
}
export const getDocument = (id: number) => call<DocumentDetail>(`/documents/${id}`)

export interface TaxCode { code: string; description: string | null; percent: number; taxesLabor: boolean; nonCalc: boolean }
export interface TaxGroup { code: string; description: string | null; codes: { position: number; code: string; description: string | null; percent: number }[] }
export const getTaxCodes = () => call<TaxCode[]>('/tax-codes')
export const getTaxGroups = () => call<TaxGroup[]>('/tax-groups')

// ---- supplier pricing (admin only) ----
export interface Supplier {
  id: number; name: string; active: boolean; sourceType: 'manual' | 'upload' | 'feed'; feedUrl: string | null; hasAuthHeader: boolean
  feedFormat: 'csv' | 'json'; columnMap: { sku?: string; cost?: string; description?: string }; refreshHours: number | null
  lastRunAt: string | null; lastStatus: string | null; lastMessage: string | null; itemCount: number; linkedCount: number
}
export interface SupplierInput { name: string; active: boolean; sourceType: string; feedUrl: string | null; feedAuthHeader: string | null; feedFormat: string; columnMap: object; refreshHours: number | null }
export interface SupplierItem { id: number; sku: string; description: string | null; unitCost: number; costUpdatedAt: string; links: number }
export interface IngestResult { added: number; changed: number; unchanged: number; batchId: number | null; priceChanges: number }
export interface CostLink { thicknessId: number; partCode: string; partDescription: string | null; thickNum: number; thickDen: number; supplierItemId: number; sku: string; unitCost: number; factor: number; supplierId: number }
export interface ThicknessHit { thicknessId: number; partCode: string; partDescription: string | null; thickNum: number; thickDen: number; linked: boolean }
export interface Markup { code: string; description: string | null; markupPct: number | null; linkedPrices: number }
export interface Batch { id: number; createdAt: string; source: string; status: string; decidedAt: string | null; supplier: string | null; decidedBy: string | null; changeCount: number }
export interface BatchChange { partCode: string; partDescription: string | null; thickNum: number; thickDen: number; category: string | null; oldPrice: number | null; newPrice: number; cost: number; markupPct: number }
const J = (b: unknown): RequestInit => ({ body: JSON.stringify(b) })
export const getSuppliers = () => call<Supplier[]>('/pricing/suppliers')
export const saveSupplier = (id: number | null, i: SupplierInput) => call<{ id: number }>(id ? `/pricing/suppliers/${id}` : '/pricing/suppliers', { method: id ? 'PUT' : 'POST', ...J(i) })
export const deleteSupplier = (id: number) => call<void>(`/pricing/suppliers/${id}`, { method: 'DELETE' })
export const getSupplierItems = (id: number, q = '') => call<SupplierItem[]>(`/pricing/suppliers/${id}/items?q=${encodeURIComponent(q)}`)
export const postCosts = (id: number, rows: { sku: string; description: string | null; cost: number }[]) => call<IngestResult>(`/pricing/suppliers/${id}/costs`, { method: 'POST', ...J({ rows }) })
export const adjustCosts = (id: number, percent: number) => call<IngestResult>(`/pricing/suppliers/${id}/adjust`, { method: 'POST', ...J({ percent }) })
export const importCosts = (id: number, content: string) => call<IngestResult>(`/pricing/suppliers/${id}/import`, { method: 'POST', ...J({ content }) })
export const runFeed = (id: number) => call<IngestResult>(`/pricing/suppliers/${id}/run`, { method: 'POST' })
export const getLinks = () => call<CostLink[]>('/pricing/links')
export const searchThicknesses = (q: string) => call<ThicknessHit[]>(`/pricing/thicknesses?q=${encodeURIComponent(q)}`)
export const putLink = (thicknessId: number, supplierItemId: number, factor: number) => call<void>(`/pricing/links/${thicknessId}`, { method: 'PUT', ...J({ supplierItemId, factor }) })
export const deleteLink = (thicknessId: number) => call<void>(`/pricing/links/${thicknessId}`, { method: 'DELETE' })
export const getMarkups = () => call<Markup[]>('/pricing/markups')
export const putMarkup = (code: string, markupPct: number | null) => call<IngestResult>(`/pricing/markups/${encodeURIComponent(code)}`, { method: 'PUT', ...J({ markupPct }) })
export const recalculate = () => call<IngestResult>('/pricing/recalculate', { method: 'POST' })
export const getBatches = (status = '') => call<Batch[]>(`/pricing/batches?status=${status}`)
export const getBatch = (id: number) => call<BatchChange[]>(`/pricing/batches/${id}`)
export const decideBatch = (id: number, approve: boolean) => call<{ status: string; pricesUpdated: number }>(`/pricing/batches/${id}/${approve ? 'approve' : 'reject'}`, { method: 'POST' })
