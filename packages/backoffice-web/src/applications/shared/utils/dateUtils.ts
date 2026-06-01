import { format, isValid, parseISO } from 'date-fns';
import { sv } from 'date-fns/locale';

/**
 * Swedish date formatting utilities
 * Uses date-fns with Swedish locale for consistent formatting across the app
 */

/**
 * Format datetime as Swedish format with time: "16 sep 2024, 14:30"
 * @param dateInput - Date object, ISO string, or null/undefined
 * @returns Formatted Swedish datetime string or empty string if invalid
 */
export const formatSwedishDateTime = (dateInput: Date | string | null | undefined): string => {
  if (!dateInput) return '';

  try {
    const date = typeof dateInput === 'string' ? parseISO(dateInput) : dateInput;
    if (!isValid(date)) return '';

    return format(date, 'dd MMM yyyy, HH:mm', { locale: sv });
  } catch {
    return '';
  }
};
