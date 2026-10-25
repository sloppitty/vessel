/** A downloaded file: its name from the server and its body, kept only in memory until the browser saves it. */
export interface DownloadedFile { fileName: string; blob: Blob }

/**
 * The file name a `Content-Disposition: attachment` header gives, reduced to safe characters; otherwise the fallback.
 * Only the plain `filename=` form is read; the server always sends it.
 */
export function attachmentName(header: string | null, fallback: string): string {
  const match = header ? /filename="?([^";]+)"?/i.exec(header) : null
  const name = match?.[1]?.trim().replace(/[^\w.-]/g, '_')
  return name && name.length <= 200 ? name : fallback
}

/** Hands the file to the browser's download, then releases the object URL. Nothing is stored in the browser. */
export function saveFile(file: DownloadedFile) {
  const url = URL.createObjectURL(file.blob)
  const link = document.createElement('a')
  link.href = url
  link.download = file.fileName
  link.rel = 'noopener'
  document.body.append(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}
