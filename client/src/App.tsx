import { useEffect, useState } from 'react'
import { api } from './api'
import Dashboard from './pages/Dashboard'
import Connections from './pages/Connections'
import Scenarios from './pages/Scenarios'
import ScenarioEditor from './pages/ScenarioEditor'
import Runs from './pages/Runs'
import RunDetail from './pages/RunDetail'

export type View =
  | { p: 'dashboard' }
  | { p: 'connections' }
  | { p: 'scenarios' }
  | { p: 'scenario'; id: string | 'new' }
  | { p: 'runs' }
  | { p: 'run'; id: string }

const NAV = [
  { p: 'dashboard', icon: '◧', label: 'Dashboard' },
  { p: 'scenarios', icon: '⇄', label: 'Scenarios' },
  { p: 'runs', icon: '≡', label: 'Runs' },
  { p: 'connections', icon: '⚯', label: 'Connections' },
] as const

export default function App() {
  const [view, setView] = useState<View>({ p: 'dashboard' })
  const [aiOn, setAiOn] = useState(false)

  useEffect(() => { api.health().then(h => setAiOn(h.aiConfigured)).catch(() => {}) }, [])

  return (
    <div className="shell">
      <aside className="side">
        <div className="brand">
          <div className="logo">B1</div>
          <div><b>B1 Integrator</b><span>Service Layer pipelines</span></div>
        </div>
        <nav>
          {NAV.map(n => (
            <a key={n.p} className={view.p === n.p ? 'on' : ''}
               onClick={() => setView({ p: n.p } as View)}>
              <span>{n.icon}</span> {n.label}
            </a>
          ))}
        </nav>
        <div className="foot">{aiOn ? 'AI mapping ready' : 'AI mapping off — set OPENROUTER_API_KEY'}</div>
      </aside>

      <main className="main">
        {view.p === 'dashboard' && <Dashboard go={setView} />}
        {view.p === 'connections' && <Connections />}
        {view.p === 'scenarios' && <Scenarios go={setView} />}
        {view.p === 'scenario' && <ScenarioEditor id={view.id} go={setView} aiOn={aiOn} />}
        {view.p === 'runs' && <Runs go={setView} />}
        {view.p === 'run' && <RunDetail id={view.id} go={setView} />}
      </main>
    </div>
  )
}
