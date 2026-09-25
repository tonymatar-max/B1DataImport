import type { Run, RunStatus } from './types'

/** The API serialises enums as either names or ordinals depending on the shape — normalise. */
const RUN_STATUS = ['Queued', 'Running', 'Succeeded', 'PartiallyFailed', 'Failed', 'Cancelled']
const ITEM_STATUS = ['Pending', 'Ok', 'Failed', 'Skipped']

export const runStatus = (s: RunStatus): string =>
  typeof s === 'number' ? RUN_STATUS[s] ?? String(s) : s

export const itemStatus = (s: unknown): string =>
  typeof s === 'number' ? ITEM_STATUS[s] ?? String(s) : String(s)

export const Status = ({ s }: { s: RunStatus }) => {
  const label = runStatus(s)
  return <span className={`st ${label}`}>{label}</span>
}

export const when = (iso?: string) => {
  if (!iso) return '—'
  const d = new Date(iso)
  const mins = Math.round((Date.now() - d.getTime()) / 60000)
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins}m ago`
  if (mins < 1440) return `${Math.round(mins / 60)}h ago`
  return d.toLocaleDateString()
}

export const duration = (r: Run) => {
  if (!r.startedUtc) return '—'
  const end = r.finishedUtc ? new Date(r.finishedUtc).getTime() : Date.now()
  const s = Math.round((end - new Date(r.startedUtc).getTime()) / 1000)
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`
}

export const Empty = ({ title, children }: { title: string; children?: React.ReactNode }) => (
  <div className="empty"><h3>{title}</h3><div className="small">{children}</div></div>
)

export const msg = (e: unknown) => e instanceof Error ? e.message : String(e)
