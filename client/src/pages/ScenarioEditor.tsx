import { useEffect, useState } from 'react'
import { api } from '../api'
import { msg } from '../lib'
import Mapper from './Mapper'
import type { View } from '../App'
import type {
  B1Entity, ConnectionDef, EntitySummary, FieldNote, MappingSpec, Scenario, SourceSchema,
} from '../types'

const kindName = (k: ConnectionDef['kind']) =>
  typeof k === 'number' ? ['SapB1', 'SqlServer', 'File', 'Rest'][k] ?? '' : k

const emptySpec: MappingSpec = { header: [], lines: [] }

const newScenario = (): Scenario => ({
  id: crypto.randomUUID().replace(/-/g, ''),
  name: '', enabled: true,
  sourceConnectionId: '', sourceKind: 'sql', b1ConnectionId: '', targetEntity: '',
  writeMode: 'Create', mappingJson: JSON.stringify(emptySpec),
  trigger: 'Manual', continueOnError: true, batchSize: 20, useBatch: true, maxRetries: 2,
})

const TABS = ['Source', 'Target', 'Mapping', 'Schedule'] as const

type Freq = 'instant' | 'minutes' | 'hourly' | 'daily' | 'weekly' | 'custom'
const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
// The scheduler's own poll loop ticks every 30s (SchedulerService.cs) — that's the fastest
// "instant" can mean without a real change-feed on the source.
const INSTANT_CRON = '*/30 * * * * *'

/** Best-effort reverse of buildCron, so editing an existing schedule shows the right preset. */
function parseCron(cron: string | null | undefined): { freq: Freq; minute: number; hour: number; dow: number; interval: number } {
  const fallback = { freq: 'custom' as Freq, minute: 0, hour: 2, dow: 1, interval: 15 }
  if (!cron) return fallback
  if (cron.trim() === INSTANT_CRON) return { ...fallback, freq: 'instant' }
  const f = cron.trim().split(/\s+/)
  if (f.length !== 6 || f[0] !== '0') return fallback
  const [, min, hour, dom, , dow] = f
  if (dom === '*' && dow === '*' && /^\*\/\d+$/.test(min) && hour === '*')
    return { ...fallback, freq: 'minutes', interval: +min.slice(2) }
  if (dom === '*' && dow === '*' && hour === '*' && /^\d+$/.test(min))
    return { ...fallback, freq: 'hourly', minute: +min }
  if (dom === '*' && dow === '*' && /^\d+$/.test(min) && /^\d+$/.test(hour))
    return { ...fallback, freq: 'daily', minute: +min, hour: +hour }
  if (dom === '*' && /^\d+$/.test(dow) && /^\d+$/.test(min) && /^\d+$/.test(hour))
    return { ...fallback, freq: 'weekly', minute: +min, hour: +hour, dow: +dow }
  return fallback
}

function buildCron(freq: Freq, p: { minute: number; hour: number; dow: number; interval: number }): string {
  switch (freq) {
    case 'instant': return INSTANT_CRON
    case 'minutes': return `0 */${p.interval} * * * *`
    case 'hourly': return `0 ${p.minute} * * * *`
    case 'daily': return `0 ${p.minute} ${p.hour} * * *`
    case 'weekly': return `0 ${p.minute} ${p.hour} * * ${p.dow}`
    default: return ''
  }
}

export default function ScenarioEditor({ id, go, aiOn }: {
  id: string | 'new'; go: (v: View) => void; aiOn: boolean
}) {
  const [s, setS] = useState<Scenario | null>(null)
  const [conns, setConns] = useState<ConnectionDef[]>([])
  const [schema, setSchema] = useState<SourceSchema | null>(null)
  const [entities, setEntities] = useState<EntitySummary[]>([])
  const [entity, setEntity] = useState<B1Entity | null>(null)
  const [spec, setSpec] = useState<MappingSpec>(emptySpec)
  const [notes, setNotes] = useState<FieldNote[]>([])
  const [aiSummary, setAiSummary] = useState('')
  const [tables, setTables] = useState<string[]>([])
  const [srcEntities, setSrcEntities] = useState<EntitySummary[]>([])
  const [tab, setTab] = useState<typeof TABS[number]>('Source')
  const [err, setErr] = useState('')
  const [ok, setOk] = useState('')
  const [busy, setBusy] = useState('')
  const [freq, setFreqState] = useState(parseCron(undefined))

  useEffect(() => {
    api.connections().then(setConns).catch(e => setErr(msg(e)))
    if (id === 'new') setS(newScenario())
    else api.scenario(id).then(sc => {
      setS(sc)
      setFreqState(parseCron(sc.cronExpression))
      try { setSpec(JSON.parse(sc.mappingJson || '{}')) } catch { setSpec(emptySpec) }
      // Editing an existing scenario: load its source preview up front so the Mapping tab
      // works immediately instead of showing "pick a source first" until Preview is re-clicked.
      const hasSource = sc.sourceKind === 'sql' ? !!(sc.sourceQuery || sc.sourceObject) : !!sc.sourceObject
      if (hasSource)
        api.preview({
          sourceKind: sc.sourceKind, connectionId: sc.sourceConnectionId || undefined,
          query: sc.sourceQuery, object: sc.sourceObject,
        }).then(setSchema).catch(e => setErr(msg(e)))
    }).catch(e => setErr(msg(e)))
  }, [id])

  // Load B1 objects whenever the target connection changes. Transient errors here (e.g. a
  // flaky login) shouldn't leave a stale banner once a later attempt succeeds.
  useEffect(() => {
    if (!s?.b1ConnectionId) return
    api.entities(s.b1ConnectionId).then(es => { setEntities(es); setErr('') }).catch(e => setErr(msg(e)))
  }, [s?.b1ConnectionId])

  // Load the chosen object's fields.
  useEffect(() => {
    if (!s?.b1ConnectionId || !s.targetEntity) { setEntity(null); return }
    api.entity(s.b1ConnectionId, s.targetEntity).then(e => { setEntity(e); setErr('') }).catch(e => setErr(msg(e)))
  }, [s?.b1ConnectionId, s?.targetEntity])

  // For a REST source, discover the source system's objects to pull from.
  useEffect(() => {
    if (s?.sourceKind !== 'rest' || !s?.sourceConnectionId) { setSrcEntities([]); return }
    api.entities(s.sourceConnectionId).then(es => { setSrcEntities(es); setErr('') }).catch(e => setErr(msg(e)))
  }, [s?.sourceKind, s?.sourceConnectionId])

  if (!s) return <div className="alert info">Loading…</div>
  const set = (patch: Partial<Scenario>) => setS({ ...s, ...patch })
  const setFreq = (patch: Partial<typeof freq>) => {
    const next = { ...freq, ...patch }
    setFreqState(next)
    if (next.freq !== 'custom') set({ cronExpression: buildCron(next.freq, next) })
  }

  // Any writable target: SAP B1 or a manifest-driven REST/OData connector.
  const targetConns = conns.filter(c => ['SapB1', 'Rest'].includes(kindName(c.kind)))
  const sqlConns = conns.filter(c => kindName(c.kind) === 'SqlServer')
  const restConns = conns.filter(c => kindName(c.kind) === 'Rest')

  const loadTables = async () => {
    setBusy('tables'); setErr('')
    try { setTables(await api.tables(s.sourceConnectionId)) }
    catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const preview = async () => {
    setBusy('preview'); setErr('')
    try {
      setSchema(await api.preview({
        sourceKind: s.sourceKind, connectionId: s.sourceConnectionId || undefined,
        query: s.sourceQuery, object: s.sourceObject,
      }))
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const upload = async (file: File) => {
    setBusy('upload'); setErr('')
    try {
      const r = await api.upload(file)
      set({ sourceKind: r.kind, sourceObject: r.path })
      setSchema(r.schema)
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const propose = async () => {
    if (!schema || !entity) return
    setBusy('ai'); setErr(''); setAiSummary('')
    try {
      const r = await api.aiPropose({
        connectionId: s.b1ConnectionId, targetEntity: s.targetEntity, source: schema,
      })
      setSpec(r.mapping); setNotes(r.notes); setAiSummary(r.summary)
      if (r.mapping.groupBy) setOk('Claude detected header + line rows and set a Group by.')
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const save = async (thenRun?: 'dry' | 'live') => {
    setBusy('save'); setErr(''); setOk('')
    try {
      const saved = await api.saveScenario({ ...s, mappingJson: JSON.stringify(spec) })
      setS(saved)
      if (thenRun) {
        const r = await api.runScenario(saved.id, thenRun === 'dry')
        go({ p: 'run', id: r.id })
      } else setOk('Scenario saved.')
    } catch (e) { setErr(msg(e)) } finally { setBusy('') }
  }

  const mandatoryUnmapped = entity
    ? entity.properties.filter(p => !p.nullable && !p.isKey).filter(p => {
        const f = spec.header.find(h => h.targetField === p.name)
        return !f?.sourceColumn && !(f?.transform === 'Constant' && f.constantValue)
      })
    : []

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{s.name || (id === 'new' ? 'New scenario' : 'Untitled scenario')}</h1>
          <p>{s.sourceKind} → {s.targetEntity || 'choose a target object'}</p>
        </div>
        <div className="actions" style={{ marginTop: 0 }}>
          <button className="ghost" onClick={() => go({ p: 'scenarios' })}>← Scenarios</button>
          <button className="ghost" disabled={busy === 'save'} onClick={() => save()}>Save</button>
          <button className="ghost" disabled={!entity || busy === 'save'} onClick={() => save('dry')}>Save & dry run</button>
          <button className="primary" disabled={!entity || busy === 'save'} onClick={() => save('live')}>Save & run</button>
        </div>
      </div>

      {err && <div className="alert err">{err}</div>}
      {ok && <div className="alert ok">{ok}</div>}

      <div className="panel">
        <div className="row">
          <div className="field grow"><label>Scenario name</label>
            <input value={s.name} onChange={e => set({ name: e.target.value })}
              placeholder="Nightly customer sync" /></div>
          <div className="field grow"><label>Description</label>
            <input value={s.description ?? ''} onChange={e => set({ description: e.target.value })} /></div>
          <div className="field"><label>&nbsp;</label>
            <label className="inline"><input type="checkbox" checked={s.enabled}
              onChange={e => set({ enabled: e.target.checked })} /> Enabled</label></div>
        </div>
      </div>

      <div className="tabs">
        {TABS.map(t => (
          <div key={t} className={`tab ${tab === t ? 'on' : ''}`} onClick={() => setTab(t)}>
            {t}
            {t === 'Mapping' && mandatoryUnmapped.length > 0 &&
              <span className="chip req">{mandatoryUnmapped.length}</span>}
          </div>
        ))}
      </div>

      {tab === 'Source' && (
        <div className="panel">
          <div className="row">
            <div className="field"><label>Source type</label>
              <select value={s.sourceKind} onChange={e => { set({ sourceKind: e.target.value }); setSchema(null) }}>
                <option value="sql">SQL Server</option>
                <option value="excel">Excel</option>
                <option value="csv">CSV</option>
                <option value="rest">REST / OData</option>
              </select></div>

            {s.sourceKind === 'sql' && (
              <div className="field grow"><label>Connection</label>
                <select value={s.sourceConnectionId} onChange={e => set({ sourceConnectionId: e.target.value })}>
                  <option value="">— choose —</option>
                  {sqlConns.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select></div>
            )}

            {s.sourceKind === 'rest' && (
              <div className="field grow"><label>Connection</label>
                <select value={s.sourceConnectionId}
                  onChange={e => set({ sourceConnectionId: e.target.value, sourceObject: '' })}>
                  <option value="">— choose —</option>
                  {restConns.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select></div>
            )}
          </div>

          {s.sourceKind === 'sql' && (
            <>
              <div className="actions left">
                <button className="ghost" disabled={!s.sourceConnectionId || busy === 'tables'} onClick={loadTables}>
                  List tables
                </button>
              </div>
              {tables.length > 0 && (
                <div className="field"><label>Table / view</label>
                  <select value={s.sourceObject ?? ''} onChange={e => set({ sourceObject: e.target.value, sourceQuery: '' })}>
                    <option value="">— choose —</option>
                    {tables.map(t => <option key={t} value={t}>{t}</option>)}
                  </select></div>
              )}
              <div className="field"><label>…or a SELECT query</label>
                <textarea rows={3} value={s.sourceQuery ?? ''}
                  onChange={e => set({ sourceQuery: e.target.value, sourceObject: '' })}
                  placeholder="SELECT * FROM dbo.Customers WHERE Active = 1 ORDER BY ModifiedOn" /></div>
            </>
          )}

          {s.sourceKind === 'rest' && (
            <>
              <div className="field"><label>Object to pull ({srcEntities.length} discovered)</label>
                <select value={s.sourceObject ?? ''} onChange={e => set({ sourceObject: e.target.value })}
                  disabled={!s.sourceConnectionId}>
                  <option value="">— choose —</option>
                  {srcEntities.map(e => <option key={e.name} value={e.name}>{e.name} — {e.fieldCount} fields</option>)}
                </select></div>
              <div className="field"><label>…optional OData $filter</label>
                <input value={s.sourceQuery ?? ''} onChange={e => set({ sourceQuery: e.target.value })}
                  placeholder="Rating gt 3 and Price lt 100" /></div>
            </>
          )}

          {(s.sourceKind === 'excel' || s.sourceKind === 'csv') && (
            <div className="field"><label>Upload {s.sourceKind === 'csv' ? '.csv' : '.xlsx / .xls'}</label>
              <input type="file" accept=".xlsx,.xls,.csv" disabled={busy === 'upload'}
                onChange={e => e.target.files?.[0] && upload(e.target.files[0])} />
              {s.sourceObject && <div className="muted small mono" style={{ marginTop: 6 }}>{s.sourceObject}</div>}
            </div>
          )}

          <div className="actions left">
            <button className="ghost" disabled={busy === 'preview'} onClick={preview}>Preview rows</button>
          </div>

          {schema && (
            <>
              <div className="alert info">
                <b>{schema.objectName}</b> — {schema.columns.length} columns
                {schema.totalRows != null && `, ~${schema.totalRows} rows`}.
              </div>
              <div className="scroll scroll-x">
                <table>
                  <thead><tr>{schema.columns.map(c => <th key={c.name}>{c.name}</th>)}</tr></thead>
                  <tbody>
                    {schema.previewRows.slice(0, 8).map((r, i) => (
                      <tr key={i}>{schema.columns.map(c =>
                        <td key={c.name} className="mono nowrap">{String(r[c.name] ?? '')}</td>)}</tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      )}

      {tab === 'Target' && (
        <div className="panel">
          <div className="row">
            <div className="field grow"><label>Target connection</label>
              <select value={s.b1ConnectionId} onChange={e => set({ b1ConnectionId: e.target.value, targetEntity: '' })}>
                <option value="">— choose —</option>
                {targetConns.map(c => (
                  <option key={c.id} value={c.id}>
                    {c.name}{c.companyDB ? ` (${c.companyDB})` : ` · ${kindName(c.kind)}`}
                  </option>
                ))}
              </select></div>
            <div className="field grow"><label>Target object ({entities.length} discovered)</label>
              <select value={s.targetEntity} onChange={e => set({ targetEntity: e.target.value })}>
                <option value="">— choose —</option>
                {entities.map(e => (
                  <option key={e.name} value={e.name}>
                    {e.name} — {e.fieldCount} fields{e.collections.length ? ` + ${e.collections.length} line table(s)` : ''}
                  </option>
                ))}
              </select></div>
          </div>
          <div className="row">
            <div className="field"><label>Write mode</label>
              <select value={String(s.writeMode)} onChange={e => set({ writeMode: e.target.value as Scenario['writeMode'] })}>
                <option value="Create">Create new records</option>
                <option value="Upsert">Upsert (update if key exists)</option>
                <option value="UpdateOnly">Update existing only</option>
              </select></div>
            {String(s.writeMode) !== 'Create' && (
              <div className="field grow"><label>Key field(s) to match on</label>
                <input value={s.keyFields ?? ''} onChange={e => set({ keyFields: e.target.value })}
                  placeholder="CardCode" /></div>
            )}
          </div>
          {entity && (
            <div className="alert info">
              <b>{entity.name}</b>: {entity.properties.length} fields,
              {' '}{entity.properties.filter(p => !p.nullable && !p.isKey).length} mandatory,
              {' '}{entity.properties.filter(p => p.isUdf).length} UDFs
              {entity.collections.length > 0 && <>, line tables: {entity.collections.map(c => c.name).join(', ')}</>}
            </div>
          )}
        </div>
      )}

      {tab === 'Mapping' && (
        <div className="panel">
          {!schema || !entity ? (
            <div className="alert warn">Pick a source (with a preview) and a target object first.</div>
          ) : (
            <>
              <div className="row" style={{ justifyContent: 'space-between', alignItems: 'flex-end' }}>
                <div className="row">
                  <div className="field"><label>Group rows by (header + lines)</label>
                    <select value={spec.groupBy ?? ''}
                      onChange={e => setSpec({ ...spec, groupBy: e.target.value || null })}>
                      <option value="">— one record per row —</option>
                      {schema.columns.map(c => <option key={c.name} value={c.name}>{c.name}</option>)}
                    </select></div>
                  <div className="field"><label>Row label in logs</label>
                    <select value={spec.sourceKeyColumn ?? ''}
                      onChange={e => setSpec({ ...spec, sourceKeyColumn: e.target.value || null })}>
                      <option value="">— row number —</option>
                      {schema.columns.map(c => <option key={c.name} value={c.name}>{c.name}</option>)}
                    </select></div>
                </div>
                <div className="actions" style={{ marginTop: 0 }}>
                  <button className="primary" disabled={!aiOn || busy === 'ai'} onClick={propose}
                    title={aiOn ? 'Let Claude propose the mapping' : 'Set ANTHROPIC_API_KEY to enable'}>
                    {busy === 'ai' ? 'Thinking…' : '✦ Propose mapping with AI'}
                  </button>
                </div>
              </div>

              {aiSummary && <div className="alert info"><b>Claude:</b> {aiSummary}</div>}
              {mandatoryUnmapped.length > 0 && (
                <div className="alert warn">
                  {mandatoryUnmapped.length} mandatory field(s) still unmapped:{' '}
                  {mandatoryUnmapped.slice(0, 8).map(p => p.name).join(', ')}
                  {mandatoryUnmapped.length > 8 && '…'}
                </div>
              )}

              <Mapper schema={schema} entity={entity} spec={spec} setSpec={setSpec} notes={notes} />
            </>
          )}
        </div>
      )}

      {tab === 'Schedule' && (
        <div className="panel">
          <div className="row">
            <div className="field"><label>Trigger</label>
              <select value={String(s.trigger)} onChange={e => {
                const trigger = e.target.value as Scenario['trigger']
                set(trigger === 'Schedule' && !s.cronExpression
                  ? { trigger, cronExpression: buildCron(freq.freq === 'custom' ? 'daily' : freq.freq, freq) }
                  : { trigger })
              }}>
                <option value="Manual">Run instantly (manual)</option>
                <option value="Schedule">On a schedule</option>
              </select></div>

            {String(s.trigger) === 'Schedule' && (
              <div className="field"><label>Frequency</label>
                <select value={freq.freq} onChange={e => setFreq({ freq: e.target.value as Freq })}>
                  <option value="instant">Instantly (as new rows appear)</option>
                  <option value="minutes">Every N minutes</option>
                  <option value="hourly">Hourly</option>
                  <option value="daily">Daily</option>
                  <option value="weekly">Weekly</option>
                  <option value="custom">Custom (cron)</option>
                </select></div>
            )}

            {String(s.trigger) === 'Schedule' && freq.freq === 'minutes' && (
              <div className="field"><label>Every</label>
                <div className="inline">
                  <input type="number" min={1} max={59} style={{ width: 70 }}
                    value={freq.interval} onChange={e => setFreq({ interval: +e.target.value })} /> minutes
                </div></div>
            )}

            {String(s.trigger) === 'Schedule' && freq.freq === 'hourly' && (
              <div className="field"><label>At minute</label>
                <input type="number" min={0} max={59} style={{ width: 70 }}
                  value={freq.minute} onChange={e => setFreq({ minute: +e.target.value })} /></div>
            )}

            {String(s.trigger) === 'Schedule' && (freq.freq === 'daily' || freq.freq === 'weekly') && (
              <div className="field"><label>At (UTC)</label>
                <input type="time" value={`${String(freq.hour).padStart(2, '0')}:${String(freq.minute).padStart(2, '0')}`}
                  onChange={e => {
                    const [h, m] = e.target.value.split(':').map(Number)
                    setFreq({ hour: h, minute: m })
                  }} /></div>
            )}

            {String(s.trigger) === 'Schedule' && freq.freq === 'weekly' && (
              <div className="field"><label>On</label>
                <select value={freq.dow} onChange={e => setFreq({ dow: +e.target.value })}>
                  {WEEKDAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}
                </select></div>
            )}
          </div>

          {String(s.trigger) === 'Schedule' && freq.freq === 'instant' && (
            <div className={`alert ${s.watermarkColumn ? 'info' : 'warn'}`}>
              {s.watermarkColumn
                ? <>Checks the source every 30s and imports only rows newer than <code>{s.watermarkColumn}</code>.</>
                : <>Checks the source every 30s — but with no watermark column set below, every check re-imports
                    the <b>entire</b> source. Set a watermark column (e.g. a modified-date) so only new rows run.</>}
            </div>
          )}

          {String(s.trigger) === 'Schedule' && (
            <div className="row">
              {freq.freq === 'custom' ? (
                <div className="field grow"><label>Cron expression (UTC)</label>
                  <input className="mono" value={s.cronExpression ?? ''}
                    onChange={e => set({ cronExpression: e.target.value })} placeholder="0 */15 * * * *" />
                  <span className="muted small">
                    6 fields = with seconds, 5 = standard. e.g. <code>0 0 2 * * *</code> = 02:00 daily.
                  </span>
                </div>
              ) : (
                <div className="muted small mono">{s.cronExpression}</div>
              )}
            </div>
          )}

          <h3>Incremental sync</h3>
          <div className="row">
            <div className="field grow"><label>Watermark column (only pull newer rows)</label>
              <input value={s.watermarkColumn ?? ''} onChange={e => set({ watermarkColumn: e.target.value })}
                placeholder="ModifiedOn" /></div>
            <div className="field grow"><label>Current watermark</label>
              <input value={s.watermarkValue ?? ''} onChange={e => set({ watermarkValue: e.target.value })}
                placeholder="(advances after a clean run)" /></div>
          </div>

          <h3>Execution</h3>
          <div className="row">
            <div className="field"><label>Batch size</label>
              <input type="number" min={1} max={100} value={s.batchSize}
                onChange={e => set({ batchSize: +e.target.value })} /></div>
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <label className="inline"><input type="checkbox" checked={s.useBatch}
                onChange={e => set({ useBatch: e.target.checked })} /> Use $batch (one request per batch)</label>
            </div>
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <label className="inline"><input type="checkbox" checked={s.continueOnError}
                onChange={e => set({ continueOnError: e.target.checked })} /> Continue on error</label>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
