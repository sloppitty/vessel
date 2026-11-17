/**
 * One dispatched server-sent event. `event` defaults to `message`; multi-line data is joined with `\n`. `id` is the
 * last event ID in effect when it was dispatched (empty when the stream never set one).
 */
export interface SseEvent {
  event: string
  data: string
  id: string
}

export class SseOverflowError extends Error {
  constructor() {
    super('The event stream exceeded its buffer limit.')
    this.name = 'SseOverflowError'
  }
}

export class SseIdleError extends Error {
  constructor() {
    super('The event stream sent nothing for too long.')
    this.name = 'SseIdleError'
  }
}

/**
 * Both servers send a keepalive comment at least every 15 seconds, so this much silence means a half-open
 * connection (for example after a network change) rather than a quiet stream.
 */
export const sseIdleTimeout = 45_000

/**
 * Incremental `text/event-stream` parser (WHATWG rules for `event`, `data` and `id`; `retry` is ignored).
 * Chunks may split lines, CRLF pairs or events anywhere. Pending text is bounded so a peer that never
 * ends a line or an event cannot grow memory without limit.
 */
export class SseParser {
  private buffer = ''
  private data: string[] = []
  private dataSize = 0
  private type = ''
  // The last event ID persists across events until the stream changes it.
  private lastId = ''

  constructor(private readonly onEvent: (event: SseEvent) => void, private readonly maxBuffer = 1024 * 1024) {}

  push(chunk: string) {
    this.buffer += chunk
    let start = 0
    for (let index = 0; index < this.buffer.length; index++) {
      const char = this.buffer[index]
      if (char !== '\n' && char !== '\r') continue
      // A trailing CR may be the first half of a CRLF split across chunks.
      if (char === '\r' && index === this.buffer.length - 1) break
      this.line(this.buffer.slice(start, index))
      if (char === '\r' && this.buffer[index + 1] === '\n') index++
      start = index + 1
    }
    this.buffer = this.buffer.slice(start)
    if (this.buffer.length + this.dataSize > this.maxBuffer) {
      this.reset()
      throw new SseOverflowError()
    }
  }

  /** The stream ended. A trailing incomplete event is discarded, as the specification requires. */
  end() {
    if (this.buffer.endsWith('\r')) this.push('\n')
    this.reset()
    this.lastId = ''
  }

  private reset() {
    this.buffer = ''
    this.data = []
    this.dataSize = 0
    this.type = ''
  }

  private line(line: string) {
    if (line === '') {
      if (this.data.length) this.onEvent({ event: this.type || 'message', data: this.data.join('\n'), id: this.lastId })
      this.data = []
      this.dataSize = 0
      this.type = ''
      return
    }
    if (line.startsWith(':')) return
    const colon = line.indexOf(':')
    const field = colon < 0 ? line : line.slice(0, colon)
    let value = colon < 0 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') this.type = value
    // An ID containing NULL is ignored, as the specification requires.
    else if (field === 'id') { if (!value.includes('\0')) this.lastId = value }
    else if (field === 'data') {
      this.data.push(value)
      this.dataSize += value.length + 1
    }
  }
}

/**
 * Read an authenticated fetch response using the same bounded decoder for every SSE channel. Any chunk, keepalive
 * comments included, resets the idle watchdog; `idleTimeout` without one rejects with {@link SseIdleError} so the
 * caller reconnects exactly as it would after any other interruption.
 */
export async function readSseStream(body: ReadableStream<Uint8Array>, signal: AbortSignal, onEvent: (event: SseEvent) => void,
  { maxBuffer, idleTimeout = sseIdleTimeout }: { maxBuffer?: number; idleTimeout?: number } = {}) {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  const parser = new SseParser(event => { if (!signal.aborted) onEvent(event) }, maxBuffer)
  const stop = () => { void reader.cancel().catch(() => undefined) }
  const read = () => new Promise<ReadableStreamReadResult<Uint8Array>>((resolve, reject) => {
    const idle = setTimeout(() => reject(new SseIdleError()), idleTimeout)
    reader.read().then(resolve, reject).finally(() => clearTimeout(idle))
  })
  signal.addEventListener('abort', stop, { once: true })
  try {
    if (signal.aborted) return
    for (;;) {
      const chunk = await read()
      if (signal.aborted) return
      if (chunk.done) {
        parser.push(decoder.decode())
        parser.end()
        return
      }
      parser.push(decoder.decode(chunk.value, { stream: true }))
    }
  } finally {
    signal.removeEventListener('abort', stop)
    await reader.cancel().catch(() => undefined)
    reader.releaseLock()
  }
}
