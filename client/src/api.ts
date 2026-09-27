import type {
  B1Entity, ConnectionDef, ConnectorsResponse, Dashboard, EntitySummary, FieldNote,
  MappingSpec, Run, RunItem, Scenario, SourceSchema,
} from './types'

async function req<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init)
  if (!r.ok) {
    const body = await r.json().catch(() => ({}))
    throw new Error(body.message || `HTTP ${r.status}`)
  }
  return r.status === 204 ? (undefined as T) : r.json()
}

const json = (method: string, body: unknown): RequestInit => ({
  method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
})

export const api = {
  health: () => req<{ ok: boolean; aiConfigured: boolean; aiProvider?: string }>('/api/health'),

  // Settings (AI provider + keys; keys are never returned)
  getSettings: () => req<{
    provider: string; model: string; hasOpenRouterKey: boolean; hasAnthropicKey: boolean
    aiConfigured: boolean; activeProvider: string
  }>('/api/settings'),
  saveSettings: (body: {
    provider?: string; model?: string; openRouterApiKey?: string; anthropicApiKey?: string
  }) => req<{ ok: boolean }>('/api/settings', json('PUT', body)),
  dashboard: () => req<Dashboard>('/api/dashboard'),

  // Connections
  connectors: () => req<ConnectorsResponse>('/api/connectors'),
  connections: () => req<ConnectionDef[]>('/api/connections'),
  createConnection: (c: unknown) => req<ConnectionDef>('/api/connections', json('POST', c)),
  updateConnection: (id: string, c: unknown) => req<ConnectionDef>(`/api/connections/${id}`, json('PUT', c)),
  deleteConnection: (id: string) => req<void>(`/api/connections/${id}`, { method: 'DELETE' }),
  testConnection: (id: string) => req<{ ok: boolean; message: string }>(`/api/connections/${id}/test`, { method: 'POST' }),
  entities: (id: string) => req<EntitySummary[]>(`/api/connections/${id}/entities`),
  entity: (id: string, name: string) => req<B1Entity>(`/api/connections/${id}/entities/${name}`),
  tables: (id: string) => req<string[]>(`/api/connections/${id}/tables`),

  // Sources
  upload: async (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    const r = await fetch('/api/upload', { method: 'POST', body: fd })
    if (!r.ok) throw new Error((await r.json().catch(() => ({}))).message || `HTTP ${r.status}`)
    return r.json() as Promise<{
      path: string; kind: string; fileName: string; schema: SourceSchema; sheets: string[]
    }>
  },
  preview: (body: { sourceKind: string; connectionId?: string; query?: string; object?: string }) =>
    req<SourceSchema>('/api/scenarios/preview', json('POST', body)),

  // Scenarios
  scenarios: () => req<Scenario[]>('/api/scenarios'),
  scenario: (id: string) => req<Scenario>(`/api/scenarios/${id}`),
  saveScenario: (s: Scenario) => req<Scenario>('/api/scenarios', json('POST', s)),
  deleteScenario: (id: string) => req<void>(`/api/scenarios/${id}`, { method: 'DELETE' }),
  runScenario: (id: string, dryRun: boolean) =>
    req<Run>(`/api/scenarios/${id}/run?dryRun=${dryRun}`, { method: 'POST' }),

  aiPropose: (body: { connectionId: string; targetEntity: string; source: SourceSchema }) =>
    req<{ mapping: MappingSpec; notes: FieldNote[]; summary: string }>(
      '/api/scenarios/ai-propose', json('POST', body)),

  // Runs
  runs: (scenarioId?: string, limit = 50) =>
    req<Run[]>(`/api/runs?limit=${limit}${scenarioId ? `&scenarioId=${scenarioId}` : ''}`),
  run: (id: string) => req<{ run: Run; isRunning: boolean }>(`/api/runs/${id}`),
  runItems: (id: string, status?: string, q?: string) => {
    const p = new URLSearchParams()
    if (status) p.set('status', status)
    if (q) p.set('q', q)
    const qs = p.toString()
    return req<RunItem[]>(`/api/runs/${id}/items${qs ? `?${qs}` : ''}`)
  },
  failureSummary: (id: string) =>
    req<{ error: string; count: number; rows: number[] }[]>(`/api/runs/${id}/failure-summary`),
  cancelRun: (id: string) => req<unknown>(`/api/runs/${id}/cancel`, { method: 'POST' }),
  retryFailed: (id: string, rows?: number[]) =>
    req<Run>(`/api/runs/${id}/retry-failed${rows ? `?rows=${rows.join(',')}` : ''}`, { method: 'POST' }),
  exportUrl: (id: string, status?: string) =>
    `/api/runs/${id}/export${status ? `?status=${status}` : ''}`,
}
