import { useEffect, useState } from 'react'
import { api } from '../api'
import { msg } from '../lib'

export default function Settings({ onSaved }: { onSaved?: () => void }) {
  const [provider, setProvider] = useState('auto')
  const [model, setModel] = useState('')
  const [orKey, setOrKey] = useState('')
  const [anthKey, setAnthKey] = useState('')
  const [status, setStatus] = useState<{ hasOpenRouterKey: boolean; hasAnthropicKey: boolean
    aiConfigured: boolean; activeProvider: string } | null>(null)
  const [err, setErr] = useState('')
  const [ok, setOk] = useState('')
  const [busy, setBusy] = useState(false)
  const [testMsg, setTestMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = () => api.getSettings().then(s => {
    setProvider(s.provider); setModel(s.model); setStatus(s)
  }).catch(e => setErr(msg(e)))
  useEffect(() => { load() }, [])

  const save = async () => {
    setBusy(true); setErr(''); setOk('')
    try {
      await api.saveSettings({
        provider, model,
        openRouterApiKey: orKey || undefined,   // blank = keep existing
        anthropicApiKey: anthKey || undefined,
      })
      setOrKey(''); setAnthKey(''); setOk('Saved.')
      await load()
      onSaved?.()
    } catch (e) { setErr(msg(e)) } finally { setBusy(false) }
  }

  const test = async () => {
    setBusy(true); setErr(''); setTestMsg(null)
    try {
      const r = await api.testAi()
      setTestMsg({ ok: true, text: r.message })
    } catch (e) { setTestMsg({ ok: false, text: msg(e) }) } finally { setBusy(false) }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Settings</h1>
          <p>AI mapping provider and API keys. Keys are encrypted at rest and never shown again.</p>
        </div>
      </div>

      {err && <div className="alert err">{err}</div>}
      {ok && <div className="alert ok">{ok}</div>}

      <div className="panel" style={{ maxWidth: 640 }}>
        <h2>AI mapping</h2>

        {status && (
          <div className={`alert ${status.aiConfigured ? 'ok' : 'warn'}`}>
            {status.aiConfigured
              ? <>AI mapping is <b>ready</b> — using <span className="mono">{status.activeProvider}</span>.</>
              : <>AI mapping is <b>off</b> — add an API key below.</>}
          </div>
        )}

        <div className="row">
          <div className="field"><label>Provider</label>
            <select value={provider} onChange={e => setProvider(e.target.value)}>
              <option value="auto">Auto (OpenRouter if a key is set)</option>
              <option value="openrouter">OpenRouter</option>
              <option value="anthropic">Anthropic</option>
            </select></div>
        </div>

        <div className="field"><label>Model(s) (OpenRouter)</label>
          <textarea rows={3} value={model} onChange={e => setModel(e.target.value)}
            placeholder={'anthropic/claude-3.5-sonnet\nopenai/gpt-4o-mini\nmeta-llama/llama-3.3-70b-instruct'} />
          <div className="muted small" style={{ marginTop: 4 }}>
            One model per line (or comma-separated), <b>up to 3</b>. The first is primary; if it's
            down, rate-limited, or refuses, OpenRouter falls back to the next. Any beyond 3 are ignored.
            Leave blank for the default (<span className="mono">anthropic/claude-3.5-sonnet</span>).
          </div>
        </div>

        <div className="field"><label>
          OpenRouter API key {status?.hasOpenRouterKey && <span className="chip key">set</span>}
          <span className="muted"> {status?.hasOpenRouterKey ? '(blank = keep current)' : ''}</span>
        </label>
          <input type="password" value={orKey} onChange={e => setOrKey(e.target.value)}
            placeholder="sk-or-…" autoComplete="off" /></div>

        <div className="field"><label>
          Anthropic API key {status?.hasAnthropicKey && <span className="chip key">set</span>}
          <span className="muted"> {status?.hasAnthropicKey ? '(blank = keep current)' : '(optional)'}</span>
        </label>
          <input type="password" value={anthKey} onChange={e => setAnthKey(e.target.value)}
            placeholder="sk-ant-…" autoComplete="off" /></div>

        <div className="muted small" style={{ marginTop: 4 }}>
          Get an OpenRouter key at <span className="mono">openrouter.ai</span>. Environment variables
          (<span className="mono">OPENROUTER_API_KEY</span>) still work as a fallback.
        </div>

        {testMsg && (
          <div className={`alert ${testMsg.ok ? 'ok' : 'err'}`} style={{ marginTop: 10 }}>{testMsg.text}</div>
        )}

        <div className="actions">
          <button className="ghost" disabled={busy} onClick={test}>Test key</button>
          <button className="primary" disabled={busy} onClick={save}>{busy ? 'Saving…' : 'Save'}</button>
        </div>
      </div>
    </>
  )
}
