import { useEffect, useState } from 'react'
import { api } from '../api'
import { Empty, Status, duration, msg, when } from '../lib'
import type { View } from '../App'
import type { Dashboard as Dash } from '../types'

export default function Dashboard({ go }: { go: (v: View) => void }) {
  const [d, setD] = useState<Dash | null>(null)
  const [err, setErr] = useState('')

  const load = () => api.dashboard().then(setD).catch(e => setErr(msg(e)))

  // Poll while anything is running so progress is visible without a refresh.
  useEffect(() => {
    load()
    const t = setInterval(load, 4000)
    return () => clearInterval(t)
  }, [])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Dashboard</h1>
          <p>Integration health across all scenarios.</p>
        </div>
        <button className="primary" onClick={() => go({ p: 'scenario', id: 'new' })}>New scenario</button>
      </div>

      {err && <div className="alert err">{err}</div>}

      <div className="stats">
        <div className="stat"><div className="n">{d?.scenarios ?? '—'}</div><div className="l">Scenarios</div></div>
        <div className="stat brand"><div className="n">{d?.scheduled ?? '—'}</div><div className="l">Scheduled</div></div>
        <div className="stat brand"><div className="n">{d?.activeRuns ?? 0}</div><div className="l">Running now</div></div>
        <div className="stat ok"><div className="n">{d?.last7Days.records ?? '—'}</div><div className="l">Records / 7d</div></div>
        <div className="stat err"><div className="n">{d?.last7Days.failures ?? '—'}</div><div className="l">Failures / 7d</div></div>
      </div>

      <div className="panel">
        <h2>Recent runs</h2>
        {!d?.latest.length ? (
          <Empty title="No runs yet">Create a scenario and run it — results appear here.</Empty>
        ) : (
          <div className="scroll">
            <table>
              <thead><tr>
                <th>Scenario</th><th>Status</th><th>Trigger</th>
                <th className="right">Ok</th><th className="right">Failed</th>
                <th>Duration</th><th>When</th>
              </tr></thead>
              <tbody>
                {d.latest.map(r => (
                  <tr key={r.id} className="click" onClick={() => go({ p: 'run', id: r.id })}>
                    <td>{r.scenarioName}{r.dryRun && <span className="chip grey">dry run</span>}</td>
                    <td><Status s={r.status} /></td>
                    <td className="muted">{r.triggeredBy}</td>
                    <td className="right">{r.succeeded}</td>
                    <td className="right" style={{ color: r.failed ? 'var(--err)' : undefined }}>{r.failed}</td>
                    <td className="muted">{duration(r)}</td>
                    <td className="muted">{when(r.queuedUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
