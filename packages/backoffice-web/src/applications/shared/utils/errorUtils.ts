import { AxiosError } from 'axios'
import { ProblemDetails } from '@/api/queries-commands'

export function getErrorMessage(error: AxiosError<ProblemDetails>): string {
  return error.response?.data?.detail ?? error.response?.data?.title ?? error.message ?? 'An error occurred'
}
