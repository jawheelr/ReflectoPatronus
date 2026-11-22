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
  phones: CustomerPhone[]; createdAt: string; updatedAt: string
}
export interface CustomerInput {
  number: string; type: string; lastName: string; firstName: string; address1: string; address2: string; city: string; state: string; zip: string
  email: string; contactName: string; taxGroup: string; taxExempt: boolean; taxId: string; priceCategory: string; category: string
  printStatement: boolean; noPrices: boolean; creditLimit: number | null; notePrinted: string; noteInternal: string
  phones: CustomerPhone[]; updatedAt?: string
}
export interface CodeName { code: string; description: string | null }
export interface CustomerLookups { priceCategories: CodeName[]; customerCategories: CodeName[]; taxGroups: CodeName[]; types: string[] }
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
export const createCustomer = (c: CustomerInput) => call<{ id: number }>('/customers', { method: 'POST', body: JSON.stringify(c) })
export const updateCustomer = (id: number, c: CustomerInput) => call<void>(`/customers/${id}`, { method: 'PUT', body: JSON.stringify(c) })
export const deleteCustomer = (id: number) => call<void>(`/customers/${id}`, { method: 'DELETE' })
export const getCustomerLookups = () => call<CustomerLookups>('/lookups/customer')

export interface DocumentSummary { id: number; type: string; number: string; date: string; customerId: number | null; customerName: string | null; poNumber: string | null; voided: boolean; total: number }
export interface DocumentLine { seq: number; parentSeq: number | null; source: string; qty: number | null; partCode: string | null; description: string | null; dims: string | null; shape: string | null; thickness: string | null; unitPrice: number | null; listPrice: number | null; multiplier: number; amount: number; taxable: boolean
  thickNum: number | null; thickDen: number | null; widthIn: number | null; heightIn: number | null; partId: number | null; units: number | null; specialPrice: boolean; amountOverride: boolean }
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
  shopOrMobile: string | null; updatedAt: string; salesPersonId: number | null; editable: boolean; balance: number; paid: number
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

// Quote entry: the server prices everything; the client only sends what the user typed.
export interface DraftLine { source: string; qty: number | null; partId: number | null; thickNum: number | null; thickDen: number | null; widthIn: number | null; heightIn: number | null
  units: number | null; priceLevel: number | null; rate: number | null; unitPrice: number | null; amount: number | null; description: string | null; taxable: boolean | null }
export interface DraftDocument { customerId: number | null; priceCategory: string | null; taxGroup: string | null; discountPercent: number | null; lines: DraftLine[] }
export interface PricedLine { seq: number; source: string; qty: number; partCode: string | null; description: string | null; thickness: string | null; dims: string | null
  sqFt: number | null; rate: number | null; unitPrice: number | null; amount: number; taxable: boolean; specialPrice: boolean; warnings: string[] }
export interface CalcResult { lines: PricedLine[]; taxes: { code: string; percent: number; amount: number }[]; subtotal: number; tax: number; total: number; priceCategory: string | null; taxGroup: string | null; warnings: string[] }
export interface DocumentInput extends DraftDocument {
  date: string | null; poNumber: string | null; salesPersonId: number | null; terms: string | null; shopOrMobile: string | null
  specialInstructions: string | null; workOrderInstructions: string | null; notePrinted: string | null; noteInternal: string | null
  jobSite: DocumentParty | null; lossDate: string | null; policyNumber: string | null; claimNumber: string | null; updatedAt: string | null
}
export interface CatalogPart { id: number; code: string; description: string | null; source: string; uomId: number | null; thicknesses: { num: number; den: number; text: string }[] }
export const calculateDocument = (d: DraftDocument) => call<CalcResult>('/documents/calculate', { method: 'POST', body: JSON.stringify(d) })
export const createDocument = (d: DocumentInput) => call<{ id: number; number: string; total: number }>('/documents', { method: 'POST', body: JSON.stringify(d) })
export const updateDocument = (id: number, d: DocumentInput) => call<{ total: number }>(`/documents/${id}`, { method: 'PUT', body: JSON.stringify(d) })
export const voidDocument = (id: number) => call<void>(`/documents/${id}/void`, { method: 'POST' })
export const getCatalogParts = (q: string, source: 'G' | 'P') => call<CatalogPart[]>(`/catalog/parts?q=${encodeURIComponent(q)}&source=${source}`)

export interface TaxCode { code: string; description: string | null; percent: number; taxesLabor: boolean; nonCalc: boolean }
export interface TaxGroup { code: string; description: string | null; codes: { position: number; code: string; description: string | null; percent: number }[] }
export const getTaxCodes = () => call<TaxCode[]>('/tax-codes')
export const getTaxGroups = () => call<TaxGroup[]>('/tax-groups')
export interface TaxGroupInput { code: string; description: string; codes: { position: number; code: string }[] }
export const createTaxGroup = (g: TaxGroupInput) => call<{ code: string }>('/tax-groups', { method: 'POST', body: JSON.stringify(g) })
export const updateTaxGroup = (code: string, g: TaxGroupInput) => call<void>(`/tax-groups/${encodeURIComponent(code)}`, { method: 'PUT', body: JSON.stringify(g) })
export const deleteTaxGroup = (code: string) => call<void>(`/tax-groups/${encodeURIComponent(code)}`, { method: 'DELETE' })

// Small reference tables (tax codes, categories, salesmen/installers, statuses): one generic editor.
export interface MaintCol { api: string; db: string; label: string; type: 'Text' | 'Bool' | 'Decimal' | 0 | 1 | 2; required: boolean; max: number; readOnly: boolean }
export interface MaintTable { key: string; title: string; note: string | null; keyColumn: MaintCol; keyIdentity: boolean; cols: MaintCol[]; protected: boolean }
export type MaintRow = Record<string, string | number | boolean | null>
export const getMaintTables = () => call<MaintTable[]>('/maintenance')
export const getMaintRows = (t: string) => call<MaintRow[]>(`/maintenance/${t}`)
export const createMaintRow = (t: string, row: MaintRow) => call<{ key: string }>(`/maintenance/${t}`, { method: 'POST', body: JSON.stringify(row) })
export const updateMaintRow = (t: string, key: string, row: MaintRow) => call<void>(`/maintenance/${t}/${encodeURIComponent(key)}`, { method: 'PUT', body: JSON.stringify(row) })
export const deleteMaintRow = (t: string, key: string) => call<void>(`/maintenance/${t}/${encodeURIComponent(key)}`, { method: 'DELETE' })

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

// ---- database export / import (admin only) ----
export interface TransferTable { table: string; inserted: number; updated: number; skipped: number; deleted: number }
export interface TransferResult { mode: string; overwrite: boolean; dryRun: boolean; applied: boolean; inserted: number; updated: number; skipped: number; deleted: number; tables: TransferTable[] }
export async function exportDb(attachments: boolean): Promise<Blob> {
  const path = `/admin/export?attachments=${attachments}`
  let r = await raw(path)
  if (r.status === 401 && await refresh()) r = await raw(path)
  if (!r.ok) throw await errorOf(r)
  return r.blob()
}
export const importDb = (file: File, mode: 'merge' | 'replace', overwrite: boolean, dryRun: boolean) =>
  call<TransferResult>(`/admin/import?mode=${mode}&overwrite=${overwrite}&dryRun=${dryRun}`, { method: 'POST', body: file })

// Work orders, invoices, credit memos
export const convertDocument = (id: number, to: 'W' | 'I' | 'C', opts: { lineSeqs?: number[]; force?: boolean } = {}) =>
  call<{ id: number; number: string; type: string; total: number }>(`/documents/${id}/convert`, { method: 'POST', body: JSON.stringify({ to, ...opts }) })
export const markPrinted = (id: number) => call<void>(`/documents/${id}/printed`, { method: 'POST' })

// Receivables and payments
export interface ReceivableCustomer { id: number; number: string; name: string; balance: number; lastActivity: string | null }
export interface OpenDocument { id: number; type: string; number: string; date: string; poNumber: string | null; voided: boolean; total: number; paid: number; balance: number }
export interface PaymentRow { id: number; paidAt: string; customerId: number; customerName: string | null; amount: number; type: string; category: string | null; description: string | null; voided: boolean; applied: number }
export interface Account { customerId: number; number: string; name: string; balance: number; onAccount: number; documents: OpenDocument[]; payments: PaymentRow[] }
export interface PaymentInput { customerId: number; amount: number; type: 'P' | 'D'; category: string | null; description: string | null; applications: { documentId: number; amount: number }[]; autoApply?: boolean }
export const getReceivables = (q: string, credit: boolean) => call<ReceivableCustomer[]>(`/receivables?${new URLSearchParams({ q, credit: String(credit) })}`)
export const getAccount = (customerId: number) => call<Account>(`/customers/${customerId}/account`)
export const createPayment = (p: PaymentInput) => call<{ id: number; applied: number; onAccount: number }>('/payments', { method: 'POST', body: JSON.stringify(p) })
export const voidPayment = (id: number) => call<void>(`/payments/${id}/void`, { method: 'POST' })

export interface CompanyInfo { id: number; name: string; address1: string | null; address2: string | null; city: string | null; state: string | null; zip: string | null; phone: string | null; fax: string | null; email: string | null; invoiceNote: string | null; disclaimer: string | null; useDisclaimer: boolean }
export const getCompany = () => call<CompanyInfo>('/company')
