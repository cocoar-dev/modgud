import type { ScheduledJobDto } from '@/models/ScheduledJob'

export type JobParameterFormValues = Record<string, string | number | boolean | null>

/** Seed the parameter form from persisted Parameters + schema defaults. A saved empty value of
 *  an `EmptyMeansNone` field stays empty (R5): the job reads it as "none", not as the default. */
export function seedParams(j: Pick<ScheduledJobDto, 'ParameterSchema' | 'Parameters'>): JobParameterFormValues {
  const out: JobParameterFormValues = {}
  for (const field of j.ParameterSchema) {
    const v = j.Parameters[field.Key]
    if (field.Type === 'Boolean') {
      out[field.Key] = typeof v === 'boolean' ? v : Boolean(field.Default)
    } else if (field.Type === 'Number') {
      const savedEmpty = field.EmptyMeansNone && field.Key in j.Parameters
      const numberValue = v == null || v === '' ? (savedEmpty ? null : field.Default) : v
      out[field.Key] = numberValue == null || numberValue === '' ? null : Number(numberValue)
    } else {
      out[field.Key] = v == null || v === '' ? '' : String(v)
    }
  }
  return out
}
