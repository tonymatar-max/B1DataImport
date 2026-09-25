import { useEffect, useState } from 'react'
import { api } from '../api'
import { Empty, msg, when } from '../lib'
import type { View } from '../App'
import type { Scenario } from '../types'

const trigger = (t: Scenario['trigger']) =>
  typeof t === 'number' ? ['Manual', 'Schedule'][t] ?? '?' : t

export default function Scenarios({ go }: { go: (v: View) => void }) {
  const [list, setList] = useState<Scenario[]>([])
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState('')

  const load = () => api.scenarios().then(setList).catch(e => setErr(msg(e)))
  useEffect(() => { load() }, [])

  const run = async (s: Scenario, dry: boolean) => {
    setBusy(s.id); setErr('')
    try {
      const r = await api.runScenario(s.id, dry)
      go({ p: 'run', id: r.id })
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Scenarios</h1>
          <p>Each scenario pulls from a source, maps it, and writes to a target object.</p>
        </div>
        <button className="primary" onClick={() => go({ p: 'scenario', id: 'new' })}>New scenario</button>
      </div>

      {err && <div className="alert err">{err}</div>}

      {!list.length ? (
        <Empty title="No scenarios yet">
          A scenario is one integration: source → mapping → target object, run manually or on a schedule.
        </Empty>
      ) : (
        <div className="panel" style={{ padding: 0 }}>
          <div className="scroll tall">
            <table>
              <thead><tr>
                <th>Name</th><th>Target</th><th>Trigger</th><th>Last run</th><th>Next</th><th></th>
              </tr></thead>
              <tbody>
                {list.map(s => (
                  <tr key={s.id}>
                    <td className="click" onClick={() => go({ p: 'scenario', id: s.id })}>
                      <b>{s.name}</b>
                      {!s.enabled && <span className="chip grey">disabled</span>}
                      <div className="muted small">{s.description}</div>
                    </td>
                    <td>{s.targetEntity}<div className="muted small">{s.sourceKind}</div></td>
                    <td>
                      {trigger(s.trigger)}
                      {trigger(s.trigger) === 'Schedule' && <div className="mono small muted">{s.cronExpression}</div>}
                    </td>
                    <td className="muted">
                      {s.lastRunUtc ? <>{when(s.lastRunUtc)}<div className="small">{s.lastRunStatus}</div></> : '—'}
                    </td>
                    <td className="muted">{s.nextRunUtc ? new Date(s.nextRunUtc).toLocaleString() : '—'}</td>
                    <td className="right nowrap">
                      <button className="ghost sm" disabled={busy === s.id} onClick={() => run(s, true)}>Dry run</button>
                      {' '}
                      <button className="primary sm" disabled={busy === s.id} onClick={() => run(s, false)}>Run</button>
                    </td>
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
