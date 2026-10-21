import type { PlayView } from '@/api/plays'
import { defaultPageSize, type PageSize } from '@/components/ui/paging'

/** The Plays list view, search, page and size, kept in shell memory while a Play is open. */
export interface PlaysListState { view: PlayView; q: string; page: number; pageSize: PageSize }
export const initialPlaysList: PlaysListState = { view: 'progress', q: '', page: 1, pageSize: defaultPageSize }

export const playViewTabs: readonly { view: PlayView; label: string; description: string }[] = [
  { view: 'drafts', label: 'Drafts', description: 'Saved drafts, most recently updated first' },
  { view: 'progress', label: 'In progress', description: 'Planned, paused and open plays, most recently updated first' },
  { view: 'review', label: 'Needs review', description: 'Closed, cancelled or unresolved plays whose review is not complete, most recently ended first' },
  { view: 'reviewed', label: 'Reviewed', description: 'Closed, cancelled or unresolved plays with a completed review, most recently ended first' },
  { view: 'closed', label: 'Closed', description: 'Closed plays, most recently ended first' },
  { view: 'cancelled', label: 'Cancelled', description: 'Cancelled plays, most recently ended first' },
  { view: 'all', label: 'All', description: 'Every saved play, most recently updated first' },
]
/** How long typing waits before the search reaches the server. */
export const playSearchDelay = 250
