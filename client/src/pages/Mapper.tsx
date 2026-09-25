import { useMemo, useState } from 'react'
import type {
  B1Entity, B1Property, FieldNote, FieldSpec, MappingSpec, SourceSchema, TransformKind,
} from '../types'

const TRANSFORMS: TransformKind[] = [
  'Direct', 'Constant', 'Expression', 'DateFormat', 'StaticLookup', 'B1Lookup',
]

export default function Mapper({ schema, entity, spec, setSpec, notes }: {
  schema: SourceSchema
  entity: B1Entity
  spec: MappingSpec
  setSpec: (s: MappingSpec) => void
  notes: FieldNote[]
}) {
  const [tab, setTab] = useState<string>('header')
  const [onlyMapped, setOnlyMapped] = useState(false)
  const cols = schema.columns.map(c => c.name)
  const noteFor = useMemo(
    () => Object.fromEntries(notes.map(n => [n.targetField, n])), [notes])

  const usedCols = new Set(
    [...spec.header, ...spec.lines.flatMap(l => l.fields)]
      .map(f => f.sourceColumn).filter(Boolean) as string[])

  // --- header field helpers ---
  const get = (list: FieldSpec[], target: string): FieldSpec =>
    list.find(f => f.targetField === target) ?? {
      targetField: target, transform: 'Direct', required: false, skipRowIfEmpty: false,
    }

  const patchHeader = (target: string, patch: Partial<FieldSpec>) => {
    const header = [...spec.header]
    const i = header.findIndex(f => f.targetField === target)
    if (i >= 0) header[i] = { ...header[i], ...patch }
    else header.push({ ...get(header, target), ...patch })
    setSpec({ ...spec, header })
  }

  const patchLine = (coll: string, target: string, patch: Partial<FieldSpec>) => {
    const lines = spec.lines.map(l => {
      if (l.targetCollection !== coll) return l
      const fields = [...l.fields]
      const i = fields.findIndex(f => f.targetField === target)
      if (i >= 0) fields[i] = { ...fields[i], ...patch }
      else fields.push({ ...get(fields, target), ...patch })
      return { ...l, fields }
    })
    // Collection not in the spec yet — add it.
    if (!lines.some(l => l.targetCollection === coll))
      lines.push({
        targetCollection: coll, skipEmptyLines: true,
        fields: [{ targetField: target, transform: 'Direct', required: false, skipRowIfEmpty: false, ...patch }],
      })
    setSpec({ ...spec, lines })
  }

  const activeColl = entity.collections.find(c => c.name === tab)
  const activeList = activeColl
    ? spec.lines.find(l => l.targetCollection === activeColl.name)?.fields ?? []
    : spec.header
  const activeProps = activeColl ? activeColl.properties : entity.properties
  const patch = activeColl
    ? (t: string, p: Partial<FieldSpec>) => patchLine(activeColl.name, t, p)
    : patchHeader

  const mappedCount = (props: B1Property[], list: FieldSpec[]) =>
    props.filter(p => isMapped(get(list, p.name))).length

  const shown = activeProps
    .filter(p => !onlyMapped || isMapped(get(activeList, p.name)))
    .sort((a, b) => rank(a, get(activeList, a.name)) - rank(b, get(activeList, b.name)))

  return (
    <>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <div className="tabs" style={{ border: 'none', margin: 0 }}>
          <div className={`tab ${tab === 'header' ? 'on' : ''}`} onClick={() => setTab('header')}>
            {entity.name} <span className="chip">{mappedCount(entity.properties, spec.header)}</span>
          </div>
          {entity.collections.map(c => (
            <div key={c.name} className={`tab ${tab === c.name ? 'on' : ''}`} onClick={() => setTab(c.name)}>
              {c.name}
              <span className="chip">
                {mappedCount(c.properties, spec.lines.find(l => l.targetCollection === c.name)?.fields ?? [])}
              </span>
            </div>
          ))}
        </div>
        <label className="inline small">
          <input type="checkbox" checked={onlyMapped} onChange={e => setOnlyMapped(e.target.checked)} />
          Mapped only
        </label>
      </div>

      {activeColl && (
        <div className="alert info">
          Line fields are built per source row. Set <b>Group rows by</b> below so several rows
          collapse into one document with many <b>{activeColl.name}</b>.
        </div>
      )}

      <div className="mapper">
        <div className="srccol">
          <div className="hd">Source columns ({cols.length})</div>
          <ul>
            {schema.columns.map(c => (
              <li key={c.name} className={usedCols.has(c.name) ? 'used' : ''}>
                {c.name}
                <span className="s">
                  {usedCols.has(c.name) ? '✓ mapped' : c.dataType}
                  {' · '}
                  {String(schema.previewRows[0]?.[c.name] ?? '').slice(0, 22)}
                </span>
              </li>
            ))}
          </ul>
        </div>

        <div className="scroll tall">
          <table>
            <thead><tr>
              <th style={{ width: '24%' }}>B1 field</th>
              <th style={{ width: '11%' }}>Type</th>
              <th style={{ width: '15%' }}>Transform</th>
              <th style={{ width: '25%' }}>Source / value</th>
              <th style={{ width: '25%' }}>Options</th>
            </tr></thead>
            <tbody>
              {shown.map(p => {
                const f = get(activeList, p.name)
                const note = noteFor[p.name]
                const mandatory = !p.nullable && !p.isKey
                return (
                  <tr key={p.name} className="maprow">
                    <td>
                      <b>{p.name}</b>
                      {mandatory && <span className="chip req">req</span>}
                      {p.isKey && <span className="chip key">key</span>}
                      {p.isUdf && <span className="chip udf">UDF</span>}
                      {note && <div className="small" style={{ marginTop: 3 }}>
                        <span className={`conf ${note.confidence}`}>{note.confidence}</span>{' '}
                        <span className="muted">{note.reason}</span>
                      </div>}
                    </td>
                    <td className="muted small">
                      {p.type.replace('Edm.', '')}{p.maxLength ? `(${p.maxLength})` : ''}
                    </td>
                    <td>
                      <select value={f.transform}
                        onChange={e => patch(p.name, { transform: e.target.value as TransformKind })}>
                        {TRANSFORMS.map(t => <option key={t}>{t}</option>)}
                      </select>
                    </td>
                    <td>
                      {f.transform === 'Constant' ? (
                        p.enumMembers?.length ? (
                          <select value={f.constantValue ?? ''}
                            onChange={e => patch(p.name, { constantValue: e.target.value })}>
                            <option value="">— none —</option>
                            {p.enumMembers.map(m => <option key={m}>{m}</option>)}
                          </select>
                        ) : (
                          <input value={f.constantValue ?? ''} placeholder="fixed value"
                            onChange={e => patch(p.name, { constantValue: e.target.value })} />
                        )
                      ) : f.transform === 'Expression' ? (
                        <input value={f.expression ?? ''} placeholder="{First} {Last|upper}"
                          onChange={e => patch(p.name, { expression: e.target.value })} />
                      ) : (
                        <select value={f.sourceColumn ?? ''}
                          onChange={e => patch(p.name, { sourceColumn: e.target.value || null })}>
                          <option value="">— not mapped —</option>
                          {cols.map(c => <option key={c} value={c}>{c}</option>)}
                        </select>
                      )}
                    </td>
                    <td>
                      {f.transform === 'DateFormat' && (
                        <input value={f.dateFormat ?? ''} placeholder="dd/MM/yyyy"
                          onChange={e => patch(p.name, { dateFormat: e.target.value })} />
                      )}
                      {f.transform === 'B1Lookup' && (
                        <div className="row" style={{ gap: 4 }}>
                          <input style={{ flex: 1 }} value={f.b1Lookup?.entity ?? ''} placeholder="Warehouses"
                            onChange={e => patch(p.name, { b1Lookup: lk(f, { entity: e.target.value }) })} />
                          <input style={{ flex: 1 }} value={f.b1Lookup?.matchField ?? ''} placeholder="WarehouseName"
                            onChange={e => patch(p.name, { b1Lookup: lk(f, { matchField: e.target.value }) })} />
                          <input style={{ flex: 1 }} value={f.b1Lookup?.returnField ?? ''} placeholder="WarehouseCode"
                            onChange={e => patch(p.name, { b1Lookup: lk(f, { returnField: e.target.value }) })} />
                        </div>
                      )}
                      {f.transform === 'StaticLookup' && (
                        <textarea rows={2} placeholder={'from=to, one per line\nCustomer=cCustomer'}
                          value={dictToText(f.staticLookup)}
                          onChange={e => patch(p.name, { staticLookup: textToDict(e.target.value) })} />
                      )}
                      {f.transform === 'Direct' && (
                        <input value={f.defaultValue ?? ''} placeholder="default if empty"
                          onChange={e => patch(p.name, { defaultValue: e.target.value })} />
                      )}
                      {p.enumMembers?.length ? (
                        <div className="muted small" title={p.enumMembers.join(', ')}>
                          enum: {p.enumMembers.slice(0, 3).join(', ')}…
                        </div>
                      ) : null}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>
    </>
  )
}

const lk = (f: FieldSpec, patch: Partial<NonNullable<FieldSpec['b1Lookup']>>) => ({
  entity: '', matchField: '', returnField: '', failIfMissing: true,
  ...(f.b1Lookup ?? {}), ...patch,
})

const isMapped = (f: FieldSpec) =>
  !!f.sourceColumn || (f.transform === 'Constant' && !!f.constantValue) ||
  (f.transform === 'Expression' && !!f.expression)

/** Mandatory-unmapped first, then mapped, then the rest. */
function rank(p: B1Property, f: FieldSpec) {
  const mandatory = !p.nullable && !p.isKey
  if (mandatory && !isMapped(f)) return 0
  if (mandatory) return 1
  if (isMapped(f)) return 2
  return 3
}

const dictToText = (d?: Record<string, string> | null) =>
  d ? Object.entries(d).map(([k, v]) => `${k}=${v}`).join('\n') : ''

const textToDict = (t: string) => {
  const out: Record<string, string> = {}
  for (const line of t.split('\n')) {
    const i = line.indexOf('=')
    if (i > 0) out[line.slice(0, i).trim()] = line.slice(i + 1).trim()
  }
  return Object.keys(out).length ? out : null
}
