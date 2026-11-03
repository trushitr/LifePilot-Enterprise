import { useCallback, useEffect, useRef, useState } from 'react';
import { formatLocal, type Api } from './recurring';

// Task activity history UI. Talks to GET /api/task-activity, which lists what the user did to their tasks (one-off
// items, recurring schedules and occurrences): created, edited (with old → new values), completed, reopened, deleted.

export type TaskType = 'task' | 'grocery' | 'event' | 'recurring' | 'occurrence';
export type ActivityAction = 'created' | 'edited' | 'completed' | 'reopened' | 'deleted';
export type FieldChange = { field: string; oldValue: string | null; newValue: string | null };
export type TaskActivity = {
  id: number; taskType: TaskType; taskId: number; title: string; detail: string | null; action: ActivityAction;
  actorUserId: number; actorEmail: string | null; occurredUtc: string; changes: FieldChange[];
};
/** Narrows the list to one task (set from a task's "History" button) . */
export type TaskFilter = { taskType: TaskType; taskId: number; title: string };

export const PAGE_SIZE = 50;

export const TYPE_LABELS: Record<TaskType, string> = {
  task: 'Task', grocery: 'Grocery', event: 'Event', recurring: 'Recurring task', occurrence: 'Occurrence',
};
export const ACTION_LABELS: Record<ActivityAction, string> = {
  created: 'Created', edited: 'Edited', completed: 'Completed', reopened: 'Reopened', deleted: 'Deleted',
};
const FIELD_LABELS: Record<string, string> = { title: 'Title', category: 'Category', amount: 'Amount', date: 'Date', active: 'Active' };

/** A stored value as shown in the list: null means "not set", an empty string is shown as such rather than vanishing. */
export const formatValue = (value: string | null): string => value === null ? 'not set' : value === '' ? '(empty)' : value;
export const fieldLabel = (field: string): string => FIELD_LABELS[field] ?? field;
export const describeChange = (c: FieldChange): string => `${fieldLabel(c.field)}: ${formatValue(c.oldValue)} → ${formatValue(c.newValue)}`;

/** Query string for the history endpoint; exported so the paging/filter contract is unit-testable without a DOM. */
export const historyQuery = (opts: { taskType?: TaskType | ''; taskId?: number; action?: ActivityAction | ''; beforeId?: number; limit?: number }): string => {
  const p = new URLSearchParams();
  p.set('limit', String(opts.limit ?? PAGE_SIZE));
  if (opts.taskType) p.set('taskType', opts.taskType);
  if (opts.taskId !== undefined) p.set('taskId', String(opts.taskId));
  if (opts.action) p.set('action', opts.action);
  if (opts.beforeId !== undefined) p.set('beforeId', String(opts.beforeId));
  return '/task-activity?' + p.toString();
};

/** Appends the next page, never repeating a row that is already listed (defensive: the cursor makes overlap impossible
 * server-side, but a duplicate key would break the list if it ever happened). Exported for testing. */
export const appendPage = (existing: TaskActivity[], next: TaskActivity[]): TaskActivity[] => {
  const seen = new Set(existing.map(a => a.id));
  return [...existing, ...next.filter(a => !seen.has(a.id))];
};

export function ActivityHistory({ api, onError, filter, onClearFilter, refreshKey = 0 }:
  { api: Api; onError: (m: string) => void; filter?: TaskFilter | null; onClearFilter?: () => void; refreshKey?: number }) {
  const [rows, setRows] = useState<TaskActivity[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [taskType, setTaskType] = useState<TaskType | ''>('');
  const [action, setAction] = useState<ActivityAction | ''>('');

  // One row more than a page is requested so "Load more" is only offered when there really is more; the extra row is
  // not shown. Paging is by id cursor (the server returns rows older than `beforeId`), so activity that arrives while the
  // user is paging gets a larger id and neither shifts nor duplicates the older pages; it shows up on the next reload.
  const fetchPage = useCallback(async (beforeId?: number): Promise<{ rows: TaskActivity[]; more: boolean }> => {
    const query = filter
      ? historyQuery({ taskType: filter.taskType, taskId: filter.taskId, action, beforeId, limit: PAGE_SIZE + 1 })
      : historyQuery({ taskType, action, beforeId, limit: PAGE_SIZE + 1 });
    const page: TaskActivity[] = await api(query);
    return { rows: page.slice(0, PAGE_SIZE), more: page.length > PAGE_SIZE };
  }, [api, filter, taskType, action]);

  // Reload from the top whenever a filter changes or the parent signals new activity. Every reload starts a new
  // generation; a request from an older generation (an earlier reload, or a "Load more" that was still in flight when
  // the filter changed) drops its result instead of overwriting or appending to the newer list. Leaving the page
  // invalidates in-flight requests the same way.
  const generation = useRef(0);
  useEffect(() => {
    const current = ++generation.current;
    (async () => {
      setLoading(true);
      try {
        const page = await fetchPage();
        if (current !== generation.current) return;
        setRows(page.rows); setHasMore(page.more);
      } catch (e) { if (current === generation.current) onError(String(e).replace(/^Error:\s*/, '')); }
      finally { if (current === generation.current) setLoading(false); }
    })();
    return () => { generation.current++; };
  }, [fetchPage, onError, refreshKey]);

  const loadMore = async () => {
    if (rows.length === 0) return;
    const current = generation.current;
    setLoading(true);
    try {
      const page = await fetchPage(rows[rows.length - 1].id);
      if (current !== generation.current) return;
      setRows(r => appendPage(r, page.rows)); setHasMore(page.more);
    } catch (e) { if (current === generation.current) onError(String(e).replace(/^Error:\s*/, '')); }
    finally { if (current === generation.current) setLoading(false); }
  };

  return (
    <section className="card">
      <div className="section-head">
        <h2>{filter ? `History of “${filter.title}”` : 'Activity history'}</h2>
        <span>{rows.length}{hasMore ? '+' : ''} entries</span>
      </div>
      <div className="filters">
        {filter ? (
          <span className="chip">{TYPE_LABELS[filter.taskType]} #{filter.taskId}
            {onClearFilter && <button className="chip-clear" onClick={onClearFilter} aria-label="Show history of all tasks">✕</button>}
          </span>
        ) : (
          <label>Type
            <select value={taskType} onChange={e => setTaskType(e.target.value as TaskType | '')} aria-label="Filter by task type">
              <option value="">All</option>
              {(Object.keys(TYPE_LABELS) as TaskType[]).map(t => <option key={t} value={t}>{TYPE_LABELS[t]}</option>)}
            </select>
          </label>
        )}
        <label>Action
          <select value={action} onChange={e => setAction(e.target.value as ActivityAction | '')} aria-label="Filter by action">
            <option value="">All</option>
            {(Object.keys(ACTION_LABELS) as ActivityAction[]).map(a => <option key={a} value={a}>{ACTION_LABELS[a]}</option>)}
          </select>
        </label>
      </div>
      {rows.length === 0 ? (
        <div className="empty">{loading ? 'Loading…' : 'No activity yet. Creating, editing, completing, reopening or deleting a task is recorded here.'}</div>
      ) : (
        <div className="rows">
          {rows.map(a => <ActivityRow key={a.id} activity={a} />)}
        </div>
      )}
      {hasMore && <button className="ghost" onClick={loadMore} disabled={loading}>{loading ? 'Loading…' : 'Load more'}</button>}
    </section>
  );
}

export function ActivityRow({ activity: a }: { activity: TaskActivity }) {
  return (
    <div className="row activity">
      <div className="row-content">
        <strong>
          <span className={'badge badge-' + a.action}>{ACTION_LABELS[a.action] ?? a.action}</span>{' '}
          {a.title}
        </strong>
        <span>{TYPE_LABELS[a.taskType] ?? a.taskType} #{a.taskId}{a.detail ? ' · ' + a.detail : ''}</span>
        {a.changes.length > 0 && (
          <ul className="changes">
            {a.changes.map(c => <li key={c.field}>{describeChange(c)}</li>)}
          </ul>
        )}
        <span>{formatLocal(a.occurredUtc)}{a.actorEmail ? ' · by ' + a.actorEmail : ''}</span>
      </div>
    </div>
  );
}
