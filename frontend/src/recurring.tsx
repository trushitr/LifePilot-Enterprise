import React, { useCallback, useEffect, useRef, useState } from 'react';

// Recurring tasks & smart reminders UI. Talks to /api/recurring-tasks, /api/task-occurrences and /api/task-reminders.

export type Api = (path: string, options?: RequestInit) => Promise<any>;
export type Frequency = 'daily' | 'weekly' | 'monthly';

export type Upcoming = { sequence: number; date: string; dueUtc: string };
export type RecurringTask = {
  id: number; title: string; category: string; frequency: Frequency; interval: number; startDate: string; dueTime: string;
  timeZoneId: string; reminderLeadMinutes: number | null; endDate: string | null; isActive: boolean; description: string;
  createdUtc: string; nextDueDate: string | null; nextDueUtc: string | null; upcoming: Upcoming[]; pendingCount: number; completedCount: number;
};
export type Occurrence = {
  id: number; recurringTaskId: number; title: string; category: string; sequenceNumber: number; dueDate: string; dueTime: string;
  timeZoneId: string; dueUtc: string; completed: boolean; completedUtc: string | null; reminderAtUtc: string | null;
  reminderSentUtc: string | null; status: 'upcoming' | 'overdue' | 'completed'; scheduleActive: boolean;
};
export type TaskReminder = {
  id: number; occurrenceId: number; recurringTaskId: number; title: string; message: string; dueUtc: string; createdUtc: string; readUtc: string | null;
};

const LEAD_OPTIONS: { label: string; value: string }[] = [
  { label: 'No reminder', value: '' },
  { label: 'At the due time', value: '0' },
  { label: '5 minutes before', value: '5' },
  { label: '15 minutes before', value: '15' },
  { label: '30 minutes before', value: '30' },
  { label: '1 hour before', value: '60' },
  { label: '2 hours before', value: '120' },
  { label: '1 day before', value: '1440' },
];

export const browserTimeZone = (): string => {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
};

export const formatLocal = (iso: string | null | undefined): string => {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
};

export const describeLead = (minutes: number | null): string => {
  if (minutes === null || minutes === undefined) return 'no reminder';
  if (minutes === 0) return 'reminder at due time';
  if (minutes % 1440 === 0) return `reminder ${minutes / 1440} day${minutes === 1440 ? '' : 's'} before`;
  if (minutes % 60 === 0) return `reminder ${minutes / 60} hour${minutes === 60 ? '' : 's'} before`;
  return `reminder ${minutes} min before`;
};

const todayIso = (): string => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

type Notify = { onError: (message: string) => void; onMessage: (message: string) => void };

// ----- Create form -----

export function RecurringTaskForm({ api, onCreated, onError, onMessage }: { api: Api; onCreated: () => Promise<void> | void } & Notify) {
  const [title, setTitle] = useState('');
  const [category, setCategory] = useState('');
  const [frequency, setFrequency] = useState<Frequency>('daily');
  const [interval, setInterval_] = useState('1');
  const [startDate, setStartDate] = useState(todayIso());
  const [dueTime, setDueTime] = useState('09:00');
  const [lead, setLead] = useState('30');
  const [endDate, setEndDate] = useState('');
  const [preview, setPreview] = useState<{ description: string; upcoming: Upcoming[] } | null>(null);
  const [previewError, setPreviewError] = useState('');
  const [saving, setSaving] = useState(false);
  const timeZoneId = browserTimeZone();
  const maxInterval = frequency === 'daily' ? 365 : frequency === 'weekly' ? 52 : 12;

  const payload = () => ({
    title: title.trim(), category: category.trim() || null, frequency, interval: Number(interval) || 1, startDate, dueTime,
    timeZoneId, reminderLeadMinutes: lead === '' ? null : Number(lead), endDate: endDate || null,
  });

  // Live preview of the next due dates (debounced). The server computes them, so what is shown is what will be scheduled.
  const previewTimer = useRef<number | undefined>(undefined);
  useEffect(() => {
    window.clearTimeout(previewTimer.current);
    if (!startDate || !dueTime) { setPreview(null); return; }
    previewTimer.current = window.setTimeout(async () => {
      try {
        const r = await api('/recurring-tasks/preview', { method: 'POST', body: JSON.stringify({ ...payload(), title: title.trim() || 'Preview' }) });
        setPreview(r); setPreviewError('');
      } catch (e) { setPreview(null); setPreviewError(String(e).replace(/^Error:\s*/, '')); }
    }, 300);
    return () => window.clearTimeout(previewTimer.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [frequency, interval, startDate, dueTime, lead, endDate]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) { onError('Give the recurring task a title.'); return; }
    setSaving(true);
    try {
      await api('/recurring-tasks', { method: 'POST', body: JSON.stringify(payload()) });
      onMessage(`Recurring task "${title.trim()}" created`);
      setTitle(''); setCategory('');
      await onCreated();
    } catch (e) { onError('Could not create recurring task: ' + String(e).replace(/^Error:\s*/, '')); }
    finally { setSaving(false); }
  };

  return (
    <section className="card">
      <h2>New recurring task</h2>
      <form onSubmit={submit}>
        <div className="fields">
          <label>Title<input required maxLength={200} value={title} onChange={e => setTitle(e.target.value)} placeholder="Water the plants" /></label>
          <label>Category<input maxLength={100} value={category} onChange={e => setCategory(e.target.value)} placeholder="Optional" /></label>
          <label>Repeats
            <select value={frequency} onChange={e => { setFrequency(e.target.value as Frequency); setInterval_('1'); }}>
              <option value="daily">Daily</option>
              <option value="weekly">Weekly (on the start date's weekday)</option>
              <option value="monthly">Monthly (on the start date's day of month)</option>
            </select>
          </label>
          <label>Every
            <div className="inline">
              <input type="number" min={1} max={maxInterval} value={interval} onChange={e => setInterval_(e.target.value)} />
              <span className="muted">{frequency === 'daily' ? 'day(s)' : frequency === 'weekly' ? 'week(s)' : 'month(s)'}</span>
            </div>
          </label>
          <label>First due date<input type="date" required value={startDate} onChange={e => setStartDate(e.target.value)} /></label>
          <label>Due time<input type="time" required value={dueTime} onChange={e => setDueTime(e.target.value)} /></label>
          <label>Reminder
            <select value={lead} onChange={e => setLead(e.target.value)}>
              {LEAD_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
          <label>Ends on (optional)<input type="date" value={endDate} min={startDate} onChange={e => setEndDate(e.target.value)} /></label>
        </div>
        <p className="muted small">Times are in your time zone ({timeZoneId}). A monthly task keeps its day of month: one scheduled for the 31st runs on Feb 28, then Mar 31.</p>
        {preview && (
          <div className="preview">
            <strong>{preview.description}</strong>
            <div className="chips">{preview.upcoming.map(u => <span className="chip" key={u.sequence} title={formatLocal(u.dueUtc)}>{u.date}</span>)}</div>
          </div>
        )}
        {previewError && <div className="alert small">{previewError}</div>}
        <button className="primary" type="submit" disabled={saving}>{saving ? 'Saving…' : '+ Add recurring task'}</button>
      </form>
    </section>
  );
}

// ----- Schedules -----

export function RecurringTaskList({ api, tasks, onChanged, onError, onMessage }: { api: Api; tasks: RecurringTask[]; onChanged: () => Promise<void> | void } & Notify) {
  const toggle = async (t: RecurringTask) => {
    try {
      await api(`/recurring-tasks/${t.id}/${t.isActive ? 'disable' : 'enable'}`, { method: 'POST' });
      onMessage(t.isActive ? `"${t.title}" disabled; no new occurrences will be scheduled.` : `"${t.title}" enabled.`);
      await onChanged();
    } catch (e) { onError(String(e).replace(/^Error:\s*/, '')); }
  };
  const remove = async (t: RecurringTask) => {
    if (!confirm(`Delete "${t.title}" and all of its occurrences and reminders?`)) return;
    try { await api(`/recurring-tasks/${t.id}`, { method: 'DELETE' }); await onChanged(); }
    catch (e) { onError(String(e).replace(/^Error:\s*/, '')); }
  };
  return (
    <section className="card">
      <div className="section-head"><h2>Recurring schedules</h2><span>{tasks.length} schedules</span></div>
      {tasks.length === 0 ? <div className="empty">No recurring tasks yet. Create one above.</div> : (
        <div className="rows">
          {tasks.map(t => (
            <div className={'row' + (t.isActive ? '' : ' inactive')} key={t.id}>
              <div className="row-content">
                <strong>{t.title} <span className={'badge ' + (t.isActive ? 'badge-on' : 'badge-off')}>{t.isActive ? 'active' : 'disabled'}</span></strong>
                <span>{t.description}{t.category ? ' · ' + t.category : ''} · {describeLead(t.reminderLeadMinutes)} · {t.timeZoneId}{t.endDate ? ' · ends ' + t.endDate : ''}</span>
                <span>
                  {t.isActive ? (t.nextDueUtc ? <>Next due {formatLocal(t.nextDueUtc)}</> : 'No further occurrences') : 'Disabled'}
                  {t.upcoming.length > 1 && <> · then {t.upcoming.slice(1).map(u => u.date).join(', ')}</>}
                  {' · '}{t.pendingCount} open · {t.completedCount} completed
                </span>
              </div>
              <button className="ghost" onClick={() => toggle(t)} aria-label={(t.isActive ? 'Disable ' : 'Enable ') + t.title}>{t.isActive ? 'Disable' : 'Enable'}</button>
              <button className="delete" onClick={() => remove(t)} aria-label={'Delete ' + t.title}>×</button>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

// ----- Occurrences -----

export function OccurrenceList({ api, onError, title = 'Upcoming occurrences', limit, compact = false, refreshKey = 0, onChanged }:
  { api: Api; onError: (m: string) => void; title?: string; limit?: number; compact?: boolean; refreshKey?: number; onChanged?: () => Promise<void> | void }) {
  const [occurrences, setOccurrences] = useState<Occurrence[]>([]);
  const [showCompleted, setShowCompleted] = useState(false);
  const load = useCallback(async () => {
    try { setOccurrences(await api('/task-occurrences' + (showCompleted ? '?includeCompleted=true' : ''))); }
    catch (e) { onError(String(e).replace(/^Error:\s*/, '')); }
  }, [api, onError, showCompleted]);
  useEffect(() => { load(); }, [load, refreshKey]);

  const setCompleted = async (o: Occurrence, completed: boolean) => {
    try {
      await api(`/task-occurrences/${o.id}`, { method: 'PUT', body: JSON.stringify({ completed }) });
      await load();
      if (onChanged) await onChanged();
    } catch (e) { onError(String(e).replace(/^Error:\s*/, '')); }
  };

  const shown = limit ? occurrences.slice(0, limit) : occurrences;
  return (
    <section className="card">
      <div className="section-head">
        <h2>{title}</h2>
        {!compact && <label className="inline-check"><input type="checkbox" checked={showCompleted} onChange={e => setShowCompleted(e.target.checked)} /> Show completed</label>}
      </div>
      {shown.length === 0 ? <div className="empty">{compact ? 'No recurring occurrences are open.' : 'Nothing scheduled. Each schedule shows its next occurrence here as soon as it is created.'}</div> : (
        <div className="rows">
          {shown.map(o => (
            <div className="row" key={o.id}>
              <input type="checkbox" checked={o.completed} onChange={e => setCompleted(o, e.target.checked)} aria-label={'Complete ' + o.title + ' due ' + o.dueDate} />
              <div className="row-content">
                <strong className={o.completed ? 'done' : ''}>{o.title} <span className={'badge badge-' + o.status}>{o.status}</span></strong>
                <span>Due {formatLocal(o.dueUtc)}{o.category ? ' · ' + o.category : ''}{o.reminderAtUtc ? ' · reminder ' + formatLocal(o.reminderAtUtc) : ''}{o.completedUtc ? ' · completed ' + formatLocal(o.completedUtc) : ''}{o.scheduleActive === false ? ' · schedule disabled' : ''}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

// ----- Reminders (in-app notifications) -----

export function useTaskReminders(api: Api, enabled: boolean, intervalMs = 20000) {
  const [reminders, setReminders] = useState<TaskReminder[]>([]);
  const [unavailable, setUnavailable] = useState(false);
  const load = useCallback(async () => {
    if (!enabled) return;
    try { setReminders(await api('/task-reminders')); setUnavailable(false); }
    catch { setUnavailable(true); }
  }, [api, enabled]);
  useEffect(() => {
    if (!enabled) { setReminders([]); return; }
    load();
    const id = window.setInterval(load, intervalMs);
    return () => window.clearInterval(id);
  }, [enabled, load, intervalMs]);
  const markRead = async (r: TaskReminder) => { await api(`/task-reminders/${r.id}/read`, { method: 'POST' }); await load(); };
  const markAllRead = async () => { await api('/task-reminders/read-all', { method: 'POST' }); await load(); };
  return { reminders, unread: reminders.filter(r => !r.readUtc), unavailable, reload: load, markRead, markAllRead };
}

export function ReminderBell({ reminders, onError }: { reminders: ReturnType<typeof useTaskReminders>; onError: (m: string) => void }) {
  const [open, setOpen] = useState(false);
  const act = async (fn: () => Promise<void>) => { try { await fn(); } catch (e) { onError(String(e).replace(/^Error:\s*/, '')); } };
  return (
    <div className="bell-wrap">
      <button className="bell" onClick={() => setOpen(!open)} aria-label={`Reminders, ${reminders.unread.length} unread`} aria-expanded={open}>
        🔔{reminders.unread.length > 0 && <span className="bell-count">{reminders.unread.length}</span>}
      </button>
      {open && (
        <div className="bell-panel" role="dialog" aria-label="Reminders">
          <div className="section-head"><h2>Reminders</h2>{reminders.unread.length > 0 && <button className="ghost" onClick={() => act(reminders.markAllRead)}>Mark all read</button>}</div>
          {reminders.unavailable && <div className="alert small">Reminders are unavailable right now.</div>}
          {reminders.reminders.length === 0 ? <div className="empty">No reminders yet. They appear here when a recurring task's reminder time arrives.</div> : (
            <div className="rows">
              {reminders.reminders.slice(0, 30).map(r => (
                <div className={'row' + (r.readUtc ? ' read' : '')} key={r.id}>
                  <div className="row-content">
                    <strong>{r.title}</strong>
                    <span>{r.message}</span>
                    <span>Raised {formatLocal(r.createdUtc)}</span>
                  </div>
                  {!r.readUtc && <button className="ghost" onClick={() => act(() => reminders.markRead(r))} aria-label={'Mark reminder for ' + r.title + ' as read'}>Read</button>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ----- Page -----

export function RecurringPanel({ api, onError, onMessage, onActivity }: { api: Api; onActivity?: () => void } & Notify) {
  const [tasks, setTasks] = useState<RecurringTask[]>([]);
  const [refreshKey, setRefreshKey] = useState(0);
  // Reloads the schedules (and their counts); used after an occurrence is completed.
  const loadTasks = useCallback(async () => {
    try { setTasks(await api('/recurring-tasks')); if (onActivity) onActivity(); }
    catch (e) { onError(String(e).replace(/^Error:\s*/, '')); }
  }, [api, onError, onActivity]);
  // Reloads schedules *and* occurrences; used after a schedule is created, enabled, disabled or deleted.
  const loadAll = useCallback(async () => { await loadTasks(); setRefreshKey(k => k + 1); }, [loadTasks]);
  useEffect(() => { loadTasks(); }, [loadTasks]);
  return (
    <>
      <RecurringTaskForm api={api} onCreated={loadAll} onError={onError} onMessage={onMessage} />
      <OccurrenceList api={api} onError={onError} refreshKey={refreshKey} onChanged={loadTasks} />
      <RecurringTaskList api={api} tasks={tasks} onChanged={loadAll} onError={onError} onMessage={onMessage} />
    </>
  );
}
