import { useLayoutEffect, useMemo, useRef, useState, type DragEvent } from 'react'
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
  const [view, setView] = useState<'table' | 'visual'>('table')
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
        <div className="row" style={{ gap: 12, alignItems: 'center' }}>
          <label className="inline small">
            <input type="checkbox" checked={onlyMapped} onChange={e => setOnlyMapped(e.target.checked)} />
            Mapped only
          </label>
          <div className="viewtoggle">
            <button className={view === 'table' ? 'on' : ''} onClick={() => setView('table')}>Table</button>
            <button className={view === 'visual' ? 'on' : ''} onClick={() => setView('visual')}>Visual</button>
          </div>
        </div>
      </div>

      {activeColl && (
        <div className="alert info">
          Line fields are built per source row. Set <b>Group rows by</b> below so several rows
          collapse into one document with many <b>{activeColl.name}</b>.
        </div>
      )}

      {view === 'visual' && (
        <VisualView key={tab} props={activeProps} list={activeList} patch={patch}
          cols={cols} schema={schema} noteFor={noteFor} onlyMapped={onlyMapped} />
      )}

      {view === 'table' && (
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
      )}
    </>
  )
}

// ---- Visual (connect-the-lines) view: same MappingSpec, drag a source column onto a target field ----

type Line = { key: string; x1: number; y1: number; x2: number; y2: number; field: string; col: string }

function VisualView({ props, list, patch, cols, schema, noteFor, onlyMapped }: {
  props: B1Property[]
  list: FieldSpec[]
  patch: (target: string, patch: Partial<FieldSpec>) => void
  cols: string[]
  schema: SourceSchema
  noteFor: Record<string, FieldNote>
  onlyMapped: boolean
}) {
  const [selected, setSelected] = useState<string | null>(null)
  const [lines, setLines] = useState<Line[]>([])
  const [dims, setDims] = useState({ w: 0, h: 0 })
  const [dragOver, setDragOver] = useState<string | null>(null)
  const [hoverCol, setHoverCol] = useState<string | null>(null)
  const [hoverField, setHoverField] = useState<string | null>(null)

  const wrap = useRef<HTMLDivElement>(null)
  const srcRefs = useRef<Map<string, HTMLElement>>(new Map())
  const tgtRefs = useRef<Map<string, HTMLElement>>(new Map())

  const getF = (target: string): FieldSpec =>
    list.find(f => f.targetField === target) ??
    { targetField: target, transform: 'Direct', required: false, skipRowIfEmpty: false }

  const shownProps = props.filter(p => !onlyMapped || isMapped(getF(p.name)))

  // Measure chip positions (relative to the wrapper) and build the connector lines.
  useLayoutEffect(() => {
    const compute = () => {
      const box = wrap.current
      if (!box) return
      setDims({ w: box.offsetWidth, h: box.offsetHeight })
      const next: Line[] = []
      for (const p of shownProps) {
        const f = getF(p.name)
        if (!f.sourceColumn) continue
        const s = srcRefs.current.get(f.sourceColumn)
        const t = tgtRefs.current.get(p.name)
        if (!s || !t) continue
        next.push({
          key: `${f.sourceColumn}->${p.name}`,
          x1: s.offsetLeft + s.offsetWidth, y1: s.offsetTop + s.offsetHeight / 2,
          x2: t.offsetLeft, y2: t.offsetTop + t.offsetHeight / 2,
          field: p.name, col: f.sourceColumn,
        })
      }
      setLines(next)
    }
    compute()
    const ro = new ResizeObserver(compute)
    if (wrap.current) ro.observe(wrap.current)
    window.addEventListener('resize', compute)
    return () => { ro.disconnect(); window.removeEventListener('resize', compute) }
  }, [list, props, cols, onlyMapped, selected])

  const drop = (target: string, e: DragEvent) => {
    e.preventDefault()
    setDragOver(null)
    const col = e.dataTransfer.getData('text/col')
    if (!col) return
    const f = getF(target)
    // A dropped column always feeds a column-based transform; Constant/Expression don't use one.
    const transform: TransformKind =
      f.transform === 'Constant' || f.transform === 'Expression' ? 'Direct' : f.transform
    patch(target, { sourceColumn: col, transform })
    setSelected(target)
  }

  const selProp = props.find(p => p.name === selected) ?? null

  const mappedCount = shownProps.filter(p => isMapped(getF(p.name))).length

  return (
    <div className="vmap-outer">
      <div className="vmap-legend small muted">
        Drag a source column onto a target field to map it. Click a field to edit its transform.
        <span className="vmap-count">{mappedCount}/{shownProps.length} mapped</span>
      </div>
      <div className="vmap" ref={wrap}>
        <svg className="vmap-svg" width={dims.w} height={dims.h}>
          {lines.map(l => {
            const hot = selected === l.field || hoverField === l.field || hoverCol === l.col
            const dim = (hoverField || hoverCol || selected) && !hot
            const cx = (l.x1 + l.x2) / 2
            return (
              <g key={l.key} className={`vmap-edge ${hot ? 'hot' : ''} ${dim ? 'dim' : ''}`}>
                <path d={`M ${l.x1} ${l.y1} C ${cx} ${l.y1}, ${cx} ${l.y2}, ${l.x2} ${l.y2}`} />
                <circle cx={l.x1} cy={l.y1} r={3} />
                <circle cx={l.x2} cy={l.y2} r={3} />
              </g>
            )
          })}
        </svg>

        <div className="vmap-col">
          <div className="hd">Source columns ({cols.length})</div>
          {schema.columns.map(c => (
            <div key={c.name} className={`vchip src ${hoverCol === c.name ? 'hot' : ''}`} draggable
              ref={el => { if (el) srcRefs.current.set(c.name, el); else srcRefs.current.delete(c.name) }}
              onDragStart={e => { e.dataTransfer.setData('text/col', c.name); e.dataTransfer.effectAllowed = 'link' }}
              onMouseEnter={() => setHoverCol(c.name)} onMouseLeave={() => setHoverCol(null)}
              title={String(schema.previewRows[0]?.[c.name] ?? '')}>
              <span className="nm">{c.name}</span>
              <span className="s">{String(schema.previewRows[0]?.[c.name] ?? c.dataType).slice(0, 20)}</span>
            </div>
          ))}
        </div>

        <div className="vmap-col">
          <div className="hd">Target fields ({shownProps.length})</div>
          {shownProps.map(p => {
            const f = getF(p.name)
            const note = noteFor[p.name]
            const mandatory = !p.nullable && !p.isKey
            const mapped = isMapped(f)
            return (
              <div key={p.name}
                ref={el => { if (el) tgtRefs.current.set(p.name, el); else tgtRefs.current.delete(p.name) }}
                className={`vchip tgt ${mapped ? 'mapped' : ''} ${selected === p.name ? 'sel' : ''} ${dragOver === p.name ? 'over' : ''} ${mandatory && !mapped ? 'need' : ''}`}
                onClick={() => setSelected(p.name)}
                onMouseEnter={() => setHoverField(p.name)} onMouseLeave={() => setHoverField(null)}
                onDragOver={e => { e.preventDefault(); setDragOver(p.name) }}
                onDragLeave={() => setDragOver(d => d === p.name ? null : d)}
                onDrop={e => drop(p.name, e)}>
                <span className="nm">
                  {p.name}
                  {mandatory && <span className="chip req">req</span>}
                  {p.isKey && <span className="chip key">key</span>}
                  {note && <span className={`conf ${note.confidence}`}>{note.confidence}</span>}
                </span>
                <span className="s">
                  {f.transform === 'Constant' ? `= ${f.constantValue ?? '…'}`
                    : f.transform === 'Expression' ? f.expression || 'expression…'
                    : f.sourceColumn
                      ? `${f.sourceColumn}${f.transform !== 'Direct' ? ` · ${f.transform}` : ''}`
                      : <span className="muted">drop a column</span>}
                </span>
                {mapped && (
                  <button className="x" title="Clear"
                    onClick={ev => { ev.stopPropagation(); patch(p.name, { sourceColumn: null, transform: 'Direct', constantValue: null, expression: null }) }}>×</button>
                )}
              </div>
            )
          })}
        </div>
      </div>

      {selProp && (
        <FieldEditor prop={selProp} f={getF(selProp.name)} cols={cols}
          patch={p => patch(selProp.name, p)} onClose={() => setSelected(null)} />
      )}
    </div>
  )
}

// Detail editor for the selected target field — same transforms as the table view.
function FieldEditor({ prop, f, cols, patch, onClose }: {
  prop: B1Property
  f: FieldSpec
  cols: string[]
  patch: (patch: Partial<FieldSpec>) => void
  onClose: () => void
}) {
  return (
    <div className="panel vmap-editor">
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <h3 style={{ margin: 0 }}>
          {prop.name}{' '}
          <span className="muted small">{prop.type.replace('Edm.', '')}{prop.maxLength ? `(${prop.maxLength})` : ''}</span>
        </h3>
        <button className="ghost sm" onClick={onClose}>Close</button>
      </div>
      <div className="row">
        <div className="field"><label>Transform</label>
          <select value={f.transform} onChange={e => patch({ transform: e.target.value as TransformKind })}>
            {TRANSFORMS.map(t => <option key={t}>{t}</option>)}
          </select></div>
        {f.transform === 'Constant' ? (
          <div className="field grow"><label>Value</label>
            {prop.enumMembers?.length ? (
              <select value={f.constantValue ?? ''} onChange={e => patch({ constantValue: e.target.value })}>
                <option value="">— none —</option>
                {prop.enumMembers.map(m => <option key={m}>{m}</option>)}
              </select>
            ) : (
              <input value={f.constantValue ?? ''} placeholder="fixed value"
                onChange={e => patch({ constantValue: e.target.value })} />
            )}</div>
        ) : f.transform === 'Expression' ? (
          <div className="field grow"><label>Expression</label>
            <input value={f.expression ?? ''} placeholder="{First} {Last|upper}"
              onChange={e => patch({ expression: e.target.value })} /></div>
        ) : (
          <div className="field grow"><label>Source column</label>
            <select value={f.sourceColumn ?? ''} onChange={e => patch({ sourceColumn: e.target.value || null })}>
              <option value="">— not mapped —</option>
              {cols.map(c => <option key={c} value={c}>{c}</option>)}
            </select></div>
        )}
      </div>
      {f.transform === 'DateFormat' && (
        <div className="field"><label>Date format</label>
          <input value={f.dateFormat ?? ''} placeholder="dd/MM/yyyy"
            onChange={e => patch({ dateFormat: e.target.value })} /></div>
      )}
      {f.transform === 'B1Lookup' && (
        <div className="row">
          <div className="field grow"><label>Lookup entity</label>
            <input value={f.b1Lookup?.entity ?? ''} placeholder="Warehouses"
              onChange={e => patch({ b1Lookup: lk(f, { entity: e.target.value }) })} /></div>
          <div className="field grow"><label>Match field</label>
            <input value={f.b1Lookup?.matchField ?? ''} placeholder="WarehouseName"
              onChange={e => patch({ b1Lookup: lk(f, { matchField: e.target.value }) })} /></div>
          <div className="field grow"><label>Return field</label>
            <input value={f.b1Lookup?.returnField ?? ''} placeholder="WarehouseCode"
              onChange={e => patch({ b1Lookup: lk(f, { returnField: e.target.value }) })} /></div>
        </div>
      )}
      {f.transform === 'StaticLookup' && (
        <div className="field"><label>Value map (from=to, one per line)</label>
          <textarea rows={3} placeholder={'Customer=cCustomer\nVendor=cSupplier'}
            value={dictToText(f.staticLookup)}
            onChange={e => patch({ staticLookup: textToDict(e.target.value) })} /></div>
      )}
      <label className="inline small" style={{ marginTop: 8 }}>
        <input type="checkbox" checked={f.required ?? false}
          onChange={e => patch({ required: e.target.checked })} /> Required
      </label>
    </div>
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
