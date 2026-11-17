/** Loads this page again from the server. Its own module, so tests can replace what jsdom cannot do. */
export function reloadPage() {
  window.location.reload()
}
