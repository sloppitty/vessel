import { isFilledText, isRecord } from './guards'

/** The day a reporting week starts on (Phase 2, item 2). */
export type WeekStart = 'monday' | 'sunday'
/**
 * The owner's one reporting setting, stored on the server: the IANA zone and week start of the week page and of the soft
 * loss limits. `isDefault` is true until the owner saves it (UTC, Monday). It is not the display zone, which stays in the
 * browser.
 */
export interface ReportingSettings { timeZone: string; weekStart: WeekStart; isDefault: boolean }
export const reportingSettingsPath = '/api/settings/reporting'
export const isWeekStart = (v: unknown): v is WeekStart => v === 'monday' || v === 'sunday'
export const isReportingSettings = (v: unknown): v is ReportingSettings => isRecord(v) && isFilledText(v.timeZone, 64) &&
  isWeekStart(v.weekStart) && typeof v.isDefault === 'boolean'
