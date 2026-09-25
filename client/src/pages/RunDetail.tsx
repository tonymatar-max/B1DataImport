import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import { Status, duration, itemStatus, msg, runStatus, when } from '../lib'
import type { View } from '../App'
import type { Run, RunItem } from '../types'

export default function RunDetail({ id, go }: { id: string; go: (v: View) => void }) {
  const [run, setRun] = useState<Run | null>(null)
  const [running, setRunning] = useState(false)
  const [items, setItems] = useState<RunItem[]>([])
  const [summary, setSummary] = useState<{ error: string; count: number; rows: number[] }[]>([])
  const [filter, setFilter] = useState<'all' | 'Failed'>('all')
  const [q, setQ] = useState('')
  const [errFocus, setErrFocus] = useState<string | null>(null)
  const [err, setErr] = useState('')
  const [ok, setOk] = useState('')
  const [busy, setBusy] = useState('')
  const [open, setOpen] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const { run: r, isRunning } = await api.run(id)
      setRun(r); setRunning(isRunning)
      const searchText = errFocus ?? q
      setItems(await api.runItems(id, filter === 'all' ? undefined : filter, searchText || undefined))
      if (r.failed > 0) setSummary(await api.failureSummary(id))
      else setSummary([])
    } catch (e) { setErr(msg(e)) }
  }, [id, filter, q, errFocus])

  useEffect(() => {
    load()
    const t = setInterval(() => { if (running) load() }, 2000)
    return () => clearInterval(t)
  }, [load, running])

  const act = async (what: 'cancel' | 'retry', rows?: number[]) => {
    setBusy(what + (rows ? rows.join(',') : '')); setErr(''); setOk('')
    try {
      if (what === 'cancel') { await api.cancelRun(id); load() }
      else {
        const r = await api.retryFailed(id, rows)
        go({ p: 'run', id: r.id })
      }
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const focusError = (error: string) => {
    setErrFocus(error === errFocus ? null : error)
    setFilter('Failed')
    setQ('')
  }

  if (!run) return <div className="alert info">Loading…</div>
  const pct = run.total ? Math.round((run.processed / run.total) * 100) : 0
  const status = runStatus(run.status)

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{run.scenarioName} <Status s={run.status} /></h1>
          <p>
            {run.dryRun && 'Dry run · '}Triggered {run.triggeredBy} · queued {when(run.queuedUtc)} · {duration(run)}
            {run.retryOfRunId && ' · retry of a previous run'}
          </p>
        </div>
        <div className="actions" style={{ marginTop: 0 }}>
          <button className="ghost" onClick={() => go({ p: 'runs' })}>← All runs</button>
          {running && <button className="danger" disabled={busy === 'cancel'} onClick={() => act('cancel')}>Cancel</button>}
          {run.failed > 0 && !running && (
            <button className="primary" disabled={busy.startsWith('retry')} onClick={() => act('retry')}>
              Re-run {run.failed} failed row{run.failed === 1 ? '' : 's'}
            </button>
          )}
        </div>
      </div>

      {err && <div className="alert err">{err}</div>}
      {ok && <div className="alert ok">{ok}</div>}
      {run.error && <div className="alert err"><b>Run aborted:</b> {run.error}</div>}

      {(status === 'Running' || status === 'Queued') && (
        <div className="panel">
          <div className="row" style={{ justifyContent: 'space-between', marginBottom: 8 }}>
            <b className="small">{run.processed} of {run.total || '?'} processed</b>
            <span className="muted small">{pct}%</span>
          </div>
          <div className="progress"><div style={{ width: `${pct}%` }} /></div>
        </div>
      )}

      <div className="stats">
        <div className="stat"><div className="n">{run.total}</div><div className="l">Records</div></div>
        <div className="stat ok"><div className="n">{run.succeeded}</div>
          <div className="l">{run.dryRun ? 'Built' : 'Written'}</div></div>
        <div className="stat err"><div className="n">{run.failed}</div><div className="l">Failed</div></div>
        <div className="stat"><div className="n">{run.skipped}</div><div className="l">Skipped</div></div>
      </div>

      {summary.length > 0 && (
        <div className="panel">
          <h3 style={{ marginTop: 0 }}>Failures by cause</h3>
          <table>
            <thead><tr><th>Error</th><th style={{ width: 70 }}>Rows</th><th style={{ width: 140 }}></th></tr></thead>
            <tbody>
              {summary.map(g => (
                <tr key={g.error} style={errFocus === g.error ? { background: '#f7f9fc' } : undefined}>
                  <td className="mono small click" onClick={() => focusError(g.error)}
                      style={{ color: 'var(--err)', maxWidth: 520, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {g.error}
                  </td>
                  <td>{g.count}</td>
                  <td className="right nowrap">
                    <button className="ghost sm" onClick={() => focusError(g.error)}>
                      {errFocus === g.error ? 'Clear filter' : 'Show rows'}
                    </button>{' '}
                    <button className="ghost sm" disabled={busy.length > 0}
                      onClick={() => act('retry', g.rows)}>
                      Retry these
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="panel" style={{ padding: 0 }}>
        <div className="row" style={{ padding: '10px 14px 0', justifyContent: 'space-between' }}>
          <div className="tabs" style={{ margin: 0 }}>
            <div className={`tab ${filter === 'all' ? 'on' : ''}`}
                 onClick={() => { setFilter('all'); setErrFocus(null) }}>All rows</div>
            <div className={`tab ${filter === 'Failed' ? 'on' : ''}`}
                 onClick={() => { setFilter('Failed'); setErrFocus(null) }}>
              Failures {run.failed > 0 && <span className="chip req">{run.failed}</span>}
            </div>
          </div>
          <div className="row" style={{ gap: 8 }}>
            <input placeholder="Search source key / error…" style={{ width: 220 }}
              value={errFocus ?? q} onChange={e => { setErrFocus(null); setQ(e.target.value) }} />
            <a className="ghost sm" style={{ padding: '6px 10px' }}
               href={api.exportUrl(id, filter === 'Failed' ? 'Failed' : undefined)}>
              Export CSV
            </a>
          </div>
        </div>
        <div className="scroll tall">
          <table>
            <thead><tr>
              <th style={{ width: 60 }}>Row</th><th style={{ width: 80 }}>Status</th>
              <th>Source key</th><th>Result</th><th style={{ width: 90 }}>Stage</th>
              <th style={{ width: 70 }}></th>
            </tr></thead>
            <tbody>
              {items.map(i => {
                const st = itemStatus(i.status)
                return (
                  <tr key={i.id}>
                    <td className={i.payloadJson ? 'click' : ''}
                        onClick={() => i.payloadJson && setOpen(open === i.id ? null : i.id)}>{i.rowNumber}</td>
                    <td><span className={`st ${st}`}>{st}</span></td>
                    <td className="mono">{i.sourceKey ?? '—'}</td>
                    <td className={i.error ? '' : 'mono'}
                        style={{ color: i.error ? 'var(--err)' : undefined, cursor: i.payloadJson ? 'pointer' : undefined }}
                        onClick={() => i.payloadJson && setOpen(open === i.id ? null : i.id)}>
                      {i.error ?? i.targetKey ?? '—'}
                      {open === i.id && i.payloadJson && (
                        <pre className="mono" style={{
                          marginTop: 8, background: '#f7f9fc', padding: 10, borderRadius: 6,
                          maxHeight: 260, overflow: 'auto', whiteSpace: 'pre-wrap', color: 'var(--text)',
                        }}>{pretty(i.payloadJson)}</pre>
                      )}
                    </td>
                    <td className="muted small">{i.errorStage ?? ''}</td>
                    <td className="right">
                      {st === 'Failed' && !running && (
                        <button className="ghost sm" disabled={busy === 'retry' + i.rowNumber}
                          onClick={() => act('retry', [i.rowNumber])}>Retry</button>
                      )}
                    </td>
                  </tr>
                )
              })}
              {!items.length && (
                <tr><td colSpan={6} className="muted" style={{ padding: 20, textAlign: 'center' }}>
                  {q || errFocus ? 'No rows match.' : filter === 'Failed' ? 'No failures.' : 'No rows recorded.'}
                </td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </>
  )
}

function pretty(json: string) {
  try { return JSON.stringify(JSON.parse(json), null, 2) } catch { return json }
}
