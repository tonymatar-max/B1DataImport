import { useEffect, useState } from 'react'
import { api } from '../api'
import { Empty, msg, when } from '../lib'
import type { ConnectionDef } from '../types'

const kindName = (k: ConnectionDef['kind']) =>
  typeof k === 'number' ? ['SapB1', 'SqlServer', 'File'][k] ?? 'Unknown' : k

const blank = {
  name: '', kind: 'SapB1' as const, baseUrl: 'https://localhost:50000/b1s/v1',
  companyDB: '', userName: 'manager', password: '', ignoreSslErrors: true,
}

export default function Connections() {
  const [list, setList] = useState<ConnectionDef[]>([])
  const [form, setForm] = useState<typeof blank & { id?: string } | null>(null)
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState('')

  const load = () => api.connections().then(setList).catch(e => setErr(msg(e)))
  useEffect(() => { load() }, [])

  const save = async () => {
    if (!form) return
    setBusy('save'); setErr('')
    try {
      if (form.id) await api.updateConnection(form.id, form)
      else await api.createConnection(form)
      setForm(null); load()
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const test = async (id: string) => {
    setBusy(id); setErr('')
    try { await api.testConnection(id) } catch (e) { setErr(msg(e)) }
    finally { setBusy(''); load() }
  }

  const remove = async (id: string) => {
    setBusy(id)
    try { await api.deleteConnection(id); load() } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const isB1 = form?.kind === 'SapB1'

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Connections</h1>
          <p>SAP B1 companies and source systems. Credentials are encrypted at rest.</p>
        </div>
        {!form && <button className="primary" onClick={() => setForm({ ...blank })}>Add connection</button>}
      </div>

      {err && <div className="alert err">{err}</div>}

      {form && (
        <div className="panel">
          <h2>{form.id ? 'Edit' : 'New'} connection</h2>
          <div className="row">
            <div className="field grow"><label>Name</label>
              <input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })}
                placeholder="Production B1 / Legacy SQL" /></div>
            <div className="field"><label>Type</label>
              <select value={form.kind} disabled={!!form.id}
                onChange={e => setForm({ ...form, kind: e.target.value as 'SapB1' })}>
                <option value="SapB1">SAP Business One</option>
                <option value="SqlServer">SQL Server</option>
              </select></div>
          </div>

          {isB1 ? (
            <>
              <div className="row">
                <div className="field grow"><label>Service Layer URL</label>
                  <input value={form.baseUrl} onChange={e => setForm({ ...form, baseUrl: e.target.value })} /></div>
                <div className="field"><label>Company DB</label>
                  <input value={form.companyDB} onChange={e => setForm({ ...form, companyDB: e.target.value })}
                    placeholder="SBODEMOGB" /></div>
              </div>
              <div className="row">
                <div className="field grow"><label>User</label>
                  <input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })} /></div>
                <div className="field grow"><label>Password {form.id && <span className="muted">(blank = keep)</span>}</label>
                  <input type="password" value={form.password}
                    onChange={e => setForm({ ...form, password: e.target.value })} /></div>
              </div>
              <label className="inline"><input type="checkbox" checked={form.ignoreSslErrors}
                onChange={e => setForm({ ...form, ignoreSslErrors: e.target.checked })} />
                Ignore SSL certificate errors (self-signed Service Layer cert)</label>
            </>
          ) : (
            <>
              <div className="row">
                <div className="field grow"><label>Server</label>
                  <input value={form.baseUrl} onChange={e => setForm({ ...form, baseUrl: e.target.value })}
                    placeholder="host\instance or host,1433" /></div>
                <div className="field"><label>Database</label>
                  <input value={form.companyDB} onChange={e => setForm({ ...form, companyDB: e.target.value })}
                    placeholder="Legacy" /></div>
              </div>
              <div className="row">
                <div className="field grow"><label>User</label>
                  <input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })}
                    placeholder="sa" /></div>
                <div className="field grow"><label>Password {form.id && <span className="muted">(blank = keep)</span>}</label>
                  <input type="password" value={form.password}
                    onChange={e => setForm({ ...form, password: e.target.value })} /></div>
              </div>
            </>
          )}

          <div className="actions">
            <button className="ghost" onClick={() => setForm(null)}>Cancel</button>
            <button className="primary" disabled={!form.name || busy === 'save'} onClick={save}>
              {busy === 'save' ? 'Saving…' : 'Save'}
            </button>
          </div>
        </div>
      )}

      {!list.length && !form ? (
        <Empty title="No connections yet">Add your SAP B1 company first, then any source systems.</Empty>
      ) : (
        <div className="cards">
          {list.map(c => (
            <div key={c.id} className="card">
              <div className="t">{c.name}</div>
              <div className="muted small">{kindName(c.kind)}</div>
              <div className="muted small mono" style={{ marginTop: 6, wordBreak: 'break-all' }}>
                {kindName(c.kind) === 'SapB1' ? `${c.baseUrl} · ${c.companyDB}` : 'SQL Server'}
              </div>
              {c.lastTestedUtc && (
                <div className={`small ${c.lastTestResult === 'ok' ? '' : 'alert err'}`}
                     style={{ marginTop: 8, marginBottom: 0 }}>
                  {c.lastTestResult === 'ok'
                    ? <span className="st Succeeded">Tested {when(c.lastTestedUtc)}</span>
                    : c.lastTestResult}
                </div>
              )}
              <div className="actions left" style={{ marginTop: 10 }}>
                <button className="ghost sm" disabled={busy === c.id} onClick={() => test(c.id)}>
                  {busy === c.id ? 'Testing…' : 'Test'}
                </button>
                <button className="ghost sm" onClick={() => setForm({
                  ...blank, id: c.id, name: c.name, kind: kindName(c.kind) as 'SapB1',
                  baseUrl: c.baseUrl ?? '', companyDB: c.companyDB ?? '', userName: c.userName ?? '',
                  ignoreSslErrors: c.ignoreSslErrors, password: '',
                })}>Edit</button>
                <button className="danger sm" onClick={() => remove(c.id)}>Delete</button>
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  )
}
