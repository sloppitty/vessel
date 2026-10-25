import type { SessionAuthApi, SessionDto, UserDto } from '@/api/auth'
import type { SessionCheck } from './session'

/** The session generation and profile epoch an asynchronous profile read started under. */
export interface ProfileRead { generation: number; epoch: number }

/** What the workspace shell gets about the signed-in session. */
export interface ShellIdentity {
  session: SessionDto
  auth: SessionAuthApi
  /** The name the sign-in wallet announced in this tab, e.g. "Rabby Wallet"; unknown after a reload. */
  walletName: string | null
  /** A teardown's notice that no screen has shown yet, e.g. after another person signed in from another tab. */
  notice?: string | null
  /** The shell showed `notice`; it is removed from storage so it shows once. */
  noticePresented?(): void
  /** The shell registers how to drop its unsaved-work exit state, so a forced teardown never prompts. */
  registerExitState(clear: () => void): () => void
  /** Captured when an asynchronous profile read starts (see `onProfile`). */
  profileRead(): ProfileRead
  /**
   * The profile changed: the shell's greeting and owner card follow it, and true is returned. A save's answer at a
   * lower revision is ignored. A `recovery` read after a revision conflict (e.g. after a restore) rebases the profile
   * even to a lower revision, but only if the generation and profile epoch it captured when it started are current.
   */
  onProfile(user: UserDto, recovery?: ProfileRead): boolean
  /**
   * Asks the server which session this tab's cookie holds and follows the answer: a 401 tears down like any other,
   * and this tab's session refreshes `session`. Whether a removed wallet ended this tab is the server's to say.
   */
  checkSession(): Promise<SessionCheck>
  /**
   * Hears this tab adopting a newer session of the same user from another tab. The session identity is refreshed
   * meanwhile; anything prepared under the old one (a removal dialog, a loaded wallet list) is out of date.
   */
  onSessionChange(listener: () => void): () => void
}
