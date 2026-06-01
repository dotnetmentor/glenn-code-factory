/**
 * Script to create all timeslots for Styrsö Hafsbad's Bastu schedule
 *
 * Prerequisites:
 * 1. Backend must be running with the Label migration applied
 * 2. Schedule "Bastu 2026 Schedule" must exist
 *
 * Run with: npx ts-node scripts/create-styrso-timeslots.ts
 * Or from browser console after logging in as superadmin
 */

// The schedule data - each day has hourly slots from 6:00 to 23:00
const scheduleData: Record<string, Record<string, string>> = {
  "monday": {
    "06:00": "Familje-bastu",
    "07:00": "Familje-bastu",
    "08:00": "Familje-bastu",
    "09:00": "Städning",
    "10:00": "Städning",
    "11:00": "Städning",
    "12:00": "Herrar",
    "13:00": "Herrar",
    "14:00": "Herrar",
    "15:00": "Herrar",
    "16:00": "Herrar",
    "17:00": "Herrar",
    "18:00": "Herrar",
    "19:00": "Herrar",
    "20:00": "Sambastu",
    "21:00": "Sambastu",
    "22:00": "Sambastu"
  },
  "tuesday": {
    "06:00": "Familje-bastu",
    "07:00": "Familje-bastu",
    "08:00": "Familje-bastu",
    "09:00": "Familje-bastu",
    "10:00": "Familje-bastu",
    "11:00": "Familje-bastu",
    "12:00": "Damer",
    "13:00": "Damer",
    "14:00": "Damer",
    "15:00": "Damer",
    "16:00": "Damer",
    "17:00": "Damer",
    "18:00": "Damer",
    "19:00": "Damer",
    "20:00": "Damer",
    "21:00": "Sambastu",
    "22:00": "Sambastu"
  },
  "wednesday": {
    "06:00": "Familje-bastu",
    "07:00": "Familje-bastu",
    "08:00": "Familje-bastu",
    "09:00": "Familje-bastu",
    "10:00": "Familje-bastu",
    "11:00": "Familje-bastu",
    "12:00": "Herrar",
    "13:00": "Herrar",
    "14:00": "Herrar",
    "15:00": "Herrar",
    "16:00": "Herrar",
    "17:00": "Herrar",
    "18:00": "Herrar",
    "19:00": "Herrar",
    "20:00": "Herrar",
    "21:00": "Herrar",
    "22:00": "Herrar"
  },
  "thursday": {
    "06:00": "Familje-bastu",
    "07:00": "Familje-bastu",
    "08:00": "Familje-bastu",
    "09:00": "Familje-bastu",
    "10:00": "Familje-bastu",
    "11:00": "Familje-bastu",
    "12:00": "Sambastu",
    "13:00": "Sambastu",
    "14:00": "Sambastu",
    "15:00": "Sambastu",
    "16:00": "Sambastu",
    "17:00": "Familje-bastu",
    "18:00": "Familje-bastu",
    "19:00": "Familje-bastu",
    "20:00": "Familje-bastu",
    "21:00": "Familje-bastu",
    "22:00": "Familje-bastu"
  },
  "friday": {
    "06:00": "Familje-bastu",
    "07:00": "Familje-bastu",
    "08:00": "Familje-bastu",
    "09:00": "Städning",
    "10:00": "Städning",
    "11:00": "Städning",
    "12:00": "Damer",
    "13:00": "Damer",
    "14:00": "Damer",
    "15:00": "Damer",
    "16:00": "Damer",
    "17:00": "Damer",
    "18:00": "Damer",
    "19:00": "Damer",
    "20:00": "Damer",
    "21:00": "Sambastu",
    "22:00": "Sambastu"
  },
  "saturday": {
    "06:00": "Damer",
    "07:00": "Damer",
    "08:00": "Damer",
    "09:00": "Damer",
    "10:00": "Damer",
    "11:00": "Damer",
    "12:00": "Herrar",
    "13:00": "Herrar",
    "14:00": "Arbete",
    "15:00": "Arbete",
    "16:00": "Herrar",
    "17:00": "Herrar",
    "18:00": "Herrar",
    "19:00": "Herrar",
    "20:00": "Herrar",
    "21:00": "Sambastu",
    "22:00": "Sambastu"
  },
  "sunday": {
    "06:00": "Herrar",
    "07:00": "Herrar",
    "08:00": "Herrar",
    "09:00": "Herrar",
    "10:00": "Herrar",
    "11:00": "Herrar",
    "12:00": "Damer",
    "13:00": "Damer",
    "14:00": "Damer",
    "15:00": "Damer",
    "16:00": "Damer",
    "17:00": "Damer",
    "18:00": "Damer",
    "19:00": "Damer",
    "20:00": "Damer",
    "21:00": "Damer",
    "22:00": "Damer"
  }
};

// Day of week mapping (0 = Sunday in .NET)
const dayOfWeekMap: Record<string, number> = {
  "sunday": 0,
  "monday": 1,
  "tuesday": 2,
  "wednesday": 3,
  "thursday": 4,
  "friday": 5,
  "saturday": 6
};

// Labels that are bookable
const bookableLabels = ["Familje-bastu"];

interface TimeSlotRequest {
  scheduleId: string;
  dayOfWeek: number;
  startTime: string;
  endTime: string;
  isAvailable: boolean;
  label: string;
}

async function createTimeSlots(scheduleId: string, authToken: string) {
  const baseUrl = "http://localhost:5338/api/schedule-time-slots";
  const requests: TimeSlotRequest[] = [];

  // Build all timeslot requests
  for (const [day, slots] of Object.entries(scheduleData)) {
    const dayOfWeek = dayOfWeekMap[day];

    for (const [startTime, label] of Object.entries(slots)) {
      // Calculate end time (1 hour later)
      const [hours] = startTime.split(":");
      const endHour = (parseInt(hours) + 1).toString().padStart(2, "0");
      const endTime = `${endHour}:00`;

      requests.push({
        scheduleId,
        dayOfWeek,
        startTime,
        endTime,
        isAvailable: bookableLabels.includes(label),
        label
      });
    }
  }

  console.log(`Creating ${requests.length} timeslots...`);

  let created = 0;
  let failed = 0;

  for (const request of requests) {
    try {
      const response = await fetch(baseUrl, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${authToken}`
        },
        body: JSON.stringify(request)
      });

      if (response.ok) {
        created++;
        console.log(`✓ Created: Day ${request.dayOfWeek}, ${request.startTime}-${request.endTime}, ${request.label}`);
      } else {
        failed++;
        const error = await response.text();
        console.error(`✗ Failed: Day ${request.dayOfWeek}, ${request.startTime}-${request.endTime}: ${error}`);
      }
    } catch (err) {
      failed++;
      console.error(`✗ Error: Day ${request.dayOfWeek}, ${request.startTime}-${request.endTime}:`, err);
    }
  }

  console.log(`\nDone! Created: ${created}, Failed: ${failed}`);
}

// Export for use in browser console
if (typeof window !== "undefined") {
  (window as any).createTimeSlotsForBastu = async (scheduleId: string) => {
    // In browser, we can use the existing auth from cookies/localStorage
    const token = localStorage.getItem("authToken") || "";
    await createTimeSlots(scheduleId, token);
  };
  console.log("Run: createTimeSlotsForBastu('SCHEDULE_ID_HERE')");
}

export { createTimeSlots, scheduleData, dayOfWeekMap, bookableLabels };
