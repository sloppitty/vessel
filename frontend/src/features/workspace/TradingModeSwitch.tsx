import type { Ref } from 'react'
import { useRef } from 'react'
import { ArrowLeftRight } from 'lucide-react'
import type { TradingMode } from '@/api/paper'
import { PaperBoatMark } from '@/components/brand/PaperBoatMark'
import { VesselMark } from '@/components/brand/VesselMark'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { tradingModeDiscardMessage, tradingModeNames, tradingModeSwitchName } from './trading-mode'
import './trading-mode.css'

function ModeMark({ mode, size }: { mode: TradingMode; size: number }) {
  return mode === 'paper' ? <PaperBoatMark size={size - 2} /> : <VesselMark size={size} />
}

/** The brand tile: the hull for Real, the paper boat for Paper. After a switch the old mark sinks and the new one rises. */
export function ShellMonogram({ mode, arrivedFrom }: { mode: TradingMode; arrivedFrom: TradingMode | null }) {
  const arriving = arrivedFrom !== null && arrivedFrom !== mode
  return <span className={`shell-monogram${arriving ? ' is-arriving' : ''}`} aria-hidden="true">
    <span className="mode-mark"><ModeMark mode={mode} size={24} /></span>
    {arriving && <span className="mode-mark is-leaving" data-mode={arrivedFrom}><ModeMark mode={arrivedFrom} size={24} /></span>}
  </span>
}

type ControlProps = { mode: TradingMode; pending: boolean; onSwitch: (control: HTMLButtonElement) => void; ref?: Ref<HTMLButtonElement> }

/** The brand subtitle: names the current mode and switches to the other one. */
export function TradingModeSubtitle({ mode, pending, onSwitch, ref }: ControlProps) {
  const name = tradingModeSwitchName(mode)
  return <button ref={ref} type="button" className="shell-mode-switch" aria-label={name} title={name} data-pending={pending || undefined}
    onClick={event => onSwitch(event.currentTarget)}>
    <span>{tradingModeNames[mode]}</span><ArrowLeftRight size={12} aria-hidden="true" />
  </button>
}

/** The collapsed rail's form of the same control, under the mark. */
export function TradingModeRailSwitch({ mode, pending, onSwitch, ref }: ControlProps) {
  const name = tradingModeSwitchName(mode)
  return <button ref={ref} type="button" className="shell-mode-swap" aria-label={name} title={name} data-pending={pending || undefined}
    onClick={event => onSwitch(event.currentTarget)}>
    <ArrowLeftRight size={16} aria-hidden="true" />
  </button>
}

/** Paper mode's header mark on narrow screens, where the sidebar is an off-canvas sheet. Decorative: the notice says it. */
export function TradingModeHeaderMark() {
  return <span className="header-mode" aria-hidden="true"><PaperBoatMark size={16} /><span>Paper</span></span>
}

/**
 * Asks before a switch discards unsaved or in-flight Play work. Keep editing is focused first; Escape keeps editing.
 * Clicks outside do nothing, as for any alert dialog. `onClosed` runs after the dialog has gone, for focus return.
 */
export function TradingModeConfirmDialog({ target, onKeep, onConfirm, onClosed }: {
  target: TradingMode; onKeep: () => void; onConfirm: () => void; onClosed: () => void
}) {
  const keep = useRef<HTMLButtonElement>(null)
  return <Dialog open onOpenChange={open => { if (!open) onKeep() }}>
    <DialogContent role="alertdialog" className="workspace-dialog trading-mode-dialog" showCloseButton={false}
      onOpenAutoFocus={event => { event.preventDefault(); keep.current?.focus() }}
      onCloseAutoFocus={event => { event.preventDefault(); onClosed() }}
      onInteractOutside={event => event.preventDefault()}>
      <span className="trading-mode-dialog-icon" data-mode={target} aria-hidden="true"><ModeMark mode={target} size={22} /></span>
      <DialogHeader><DialogTitle>Switch to {tradingModeNames[target]}?</DialogTitle>
        <DialogDescription>{tradingModeDiscardMessage}</DialogDescription></DialogHeader>
      <DialogFooter><Button ref={keep} type="button" variant="outline" onClick={onKeep}>Keep editing</Button>
        <Button type="button" variant="destructive" onClick={onConfirm}>Discard and switch</Button></DialogFooter>
    </DialogContent>
  </Dialog>
}
