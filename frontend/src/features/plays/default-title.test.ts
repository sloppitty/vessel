import { describe, expect, it } from 'vitest'
import { defaultPlayTitle, maxPlayTitleLength, titleDate, titleForSave } from './default-title'

// Local time on purpose: the title shows the owner's calendar day, not the UTC one.
const day = new Date(2026, 9, 15, 23, 30)

describe('default Play title', () => {
  it('names a Play by asset, direction and local date', () => {
    expect(defaultPlayTitle({ instrument: 'BTC', direction: 'long' }, day)).toBe('BTC Long · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: '1000PEPE', direction: 'short' }, new Date(2027, 0, 3))).toBe('1000PEPE Short · 3 Jan 2027')
    expect(defaultPlayTitle({ instrument: 'BTC', direction: 'long' }, new Date(2026, 8, 30))).toBe('BTC Long · 30 Sep 2026')
  })

  it('can name the UTC day instead of the local one, as Activity shows trades', () => {
    // 02:00 UTC on 13 October is still 12 October in New York; the retrospective preview uses Activity's UTC day.
    const zone = process.env.TZ
    process.env.TZ = 'America/New_York'
    try {
      const at = new Date('2026-10-13T02:00:00Z')
      expect(defaultPlayTitle({ instrument: 'BTC', direction: 'long' }, at)).toBe('BTC Long · 12 Oct 2026')
      expect(defaultPlayTitle({ instrument: 'BTC', direction: 'long' }, at, 'utc')).toBe('BTC Long · 13 Oct 2026')
    } finally {
      if (zone === undefined) delete process.env.TZ
      else process.env.TZ = zone
    }
  })
  it('uses a manual label as entered, with spaces tidied', () => {
    expect(defaultPlayTitle({ instrument: '  BTC-PERP  ', direction: 'long' }, day)).toBe('BTC-PERP Long · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: 'Gold   spot', direction: 'short' }, day)).toBe('Gold spot Short · 15 Oct 2026')
  })

  it('leaves out missing parts without stray separators', () => {
    expect(defaultPlayTitle({ instrument: '', direction: 'short' }, day)).toBe('Short · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: null, direction: 'long' }, day)).toBe('Long · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: '   ', direction: 'long' }, day)).toBe('Long · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: 'BTC' }, day)).toBe('BTC · 15 Oct 2026')
    expect(defaultPlayTitle({ instrument: 'BTC', direction: 'sideways' }, day)).toBe('BTC · 15 Oct 2026')
    expect(defaultPlayTitle({}, day)).toBe('15 Oct 2026')
  })

  it('keeps the whole title within the limit by shortening the asset', () => {
    const title = defaultPlayTitle({ instrument: 'A'.repeat(500), direction: 'short' }, day)
    expect(title).toHaveLength(maxPlayTitleLength)
    expect(title.endsWith('A Short · 15 Oct 2026')).toBe(true)
    // A label cut between two halves of a surrogate pair drops the stray half instead of keeping it.
    const emoji = defaultPlayTitle({ instrument: '😀'.repeat(300), direction: 'long' }, day)
    expect(emoji.length).toBeLessThanOrEqual(maxPlayTitleLength)
    expect(emoji).not.toMatch(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/)
    expect(emoji.endsWith(' Long · 15 Oct 2026')).toBe(true)
  })

  it('formats the date with fixed English month names', () => {
    const months = Array.from({ length: 12 }, (_, month) => titleDate(new Date(2026, month, 1)))
    expect(months).toEqual(['1 Jan 2026', '1 Feb 2026', '1 Mar 2026', '1 Apr 2026', '1 May 2026', '1 Jun 2026',
      '1 Jul 2026', '1 Aug 2026', '1 Sep 2026', '1 Oct 2026', '1 Nov 2026', '1 Dec 2026'])
  })
})

describe('title sent when saving', () => {
  const draft = { instrument: 'ETH', direction: 'long' }

  it('never replaces a name the owner typed', () => {
    expect(titleForSave({ ...draft, title: 'Range reclaim' }, undefined, day)).toBe('Range reclaim')
    expect(titleForSave({ ...draft, title: '  Padded  ' }, 'planned', day)).toBe('  Padded  ')
  })

  it('names a blank or whitespace title while the Play can still change it', () => {
    for (const status of [undefined, 'draft', 'planned', 'paused', 'open'] as const) {
      expect(titleForSave({ ...draft, title: '' }, status, day)).toBe('ETH Long · 15 Oct 2026')
      expect(titleForSave({ ...draft, title: ' \t ' }, status, day)).toBe('ETH Long · 15 Oct 2026')
    }
  })

  it('leaves the title of a closed or cancelled Play alone, because the API rejects a changed one', () => {
    expect(titleForSave({ ...draft, title: '' }, 'closed', day)).toBe('')
    expect(titleForSave({ ...draft, title: '' }, 'cancelled', day)).toBe('')
  })
})
