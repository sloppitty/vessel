/** Puts the process time zone back: assigning undefined would set the zone to the string "undefined". */
export function restoreZone(zone: string | undefined) {
  if (zone === undefined) delete process.env.TZ
  else process.env.TZ = zone
}
