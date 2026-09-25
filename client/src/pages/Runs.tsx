import { useEffect, useState } from 'react'
import { api } from '../api'
import { Empty, Status, duration, msg, when } from '../lib'
import type { View } from '../App'
import type { Run } from '../types'

export default function Runs({ go }: { go: (v: View) => void }) {
  const [list, setList] = useState<Run[]>([])
  const [err, setErr] = useState('')

  useEffect(() => {
    const load = () => api.runs(undefined, 100).then(setList).catch(e => setErr(msg(e)))
    load()
    const t = setInterval(load, 5000)
    return () => clearInterval(t)
  }, [])

  return (
    <>
      <div className="page-head">
        <div><h1>Runs</h1><p>Every execution, with per-row results kept for re-running failures.</p></div>
      </div>

      {err && <div className="alert err">{err}</div>}

      {!list.length ? <Empty title="No runs yet" /> : (
        <div className="panel" style={{ padding: 0 }}>
          <div className="scroll tall">
            <table>
              <thead><tr>
                <th>Scenario</th><th>Status</th><th>Trigger</th>
                <th className="right">Total</th><th className="right">Ok</th><th className="right">Failed</th>
                <th>Duration</th><th>Started</th>
              </tr></thead>
              <tbody>
                {list.map(r => (
                  <tr key={r.id} className="click" onClick={() => go({ p: 'run', id: r.id })}>
                    <td>
                      {r.scenarioName}
                      {r.dryRun && <span className="chip grey">dry</span>}
                      {r.retryOfRunId && <span className="chip">retry</span>}
                    </td>
                    <td><Status s={r.status} /></td>
                    <td className="muted">{r.triggeredBy}</td>
                    <td className="right">{r.total}</td>
                    <td className="right">{r.succeeded}</td>
                    <td className="right" style={{ color: r.failed ? 'var(--err)' : undefined }}>{r.failed}</td>
                    <td className="muted">{duration(r)}</td>
                    <td className="muted">{when(r.queuedUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </>
  )
}
