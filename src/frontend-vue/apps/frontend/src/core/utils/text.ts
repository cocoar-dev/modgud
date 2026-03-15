export function parseLines(val: string): string[] {
  return val.split('\n').map((s) => s.trim()).filter(Boolean);
}
