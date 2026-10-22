import { daysAgo } from "./names";
import { at, createAll, idColumn, type SimpleTemplate } from "./types";

/** Contacts and appointments with them: what is coming up, what was done or cancelled. */
export const appointmentsTemplate: SimpleTemplate = {
  id: "appointments",
  title: "Appointments",
  description:
    "Contacts and appointments with a start and end, a place and a status. A dashboard with what is coming up, appointments per week, who you meet most and how many are cancelled; a form, New appointment.",
  base: { contacts: "contacts", appointments: "appointments" },
  prefixes: ["crm_", "my_"],
  tables: (n) => [
    {
      name: n.contacts,
      displayName: "Contacts",
      columns: [
        idColumn,
        {
          name: "name",
          displayName: "Name",
          type: "text",
          nullable: false,
          unique: true,
        },
        { name: "email", displayName: "Email", type: "text" },
        { name: "phone", displayName: "Phone", type: "text" },
        { name: "note", displayName: "Note", type: "text" },
        {
          name: "appointment_count",
          displayName: "Number of appointments",
          type: "computed",
          formula: `COUNT(${n.appointments})`,
        },
      ],
    },
    {
      name: n.appointments,
      displayName: "Appointments",
      columns: [
        idColumn,
        { name: "title", displayName: "Title", type: "text", nullable: false },
        {
          name: "starts_at",
          displayName: "Starts",
          type: "datetime",
          nullable: false,
        },
        { name: "ends_at", displayName: "Ends", type: "datetime" },
        {
          name: "contact_id",
          displayName: "Contact",
          type: "reference",
          references: n.contacts,
        },
        { name: "location", displayName: "Location", type: "text" },
        {
          name: "status",
          displayName: "Status",
          type: "enum",
          values: ["planned", "done", "cancelled"],
          default: "planned",
        },
        { name: "note", displayName: "Note", type: "text" },
      ],
    },
  ],
  relations: (n) => [
    {
      name: `${n.contacts}_${n.appointments}`,
      type: "1:N",
      from: n.contacts,
      to: n.appointments,
      column: "contact_id",
    },
  ],
  dashboards: (n) => {
    const a = n.appointments;
    return [
      {
        name: "Appointments",
        dashboard: {
          description: "What is coming up and what was",
          dateRange: { preset: "last-3-months" },
          filters: [{ column: "contact_id", label: "Contact" }],
          widgets: [
            {
              id: "coming-up",
              type: "table",
              title: "Coming up",
              position: at(0, 0, 8, 5),
              data: {
                query: { source: a, filter: { status: "planned" } },
                rows: {
                  columns: ["starts_at", "title", "contact_id", "location"],
                  sort: "starts_at",
                  limit: 20,
                },
                dateColumn: "starts_at",
                range: { preset: "next-12-months" },
              },
            },
            {
              id: "done",
              type: "kpi",
              title: "Done in the range",
              position: at(8, 0, 4, 2),
              data: {
                query: {
                  source: a,
                  measures: [{ name: "done", formula: `COUNT(WHERE status = "done")` }],
                },
                dateColumn: "starts_at",
              },
              display: {
                format: "integer",
                compare: true,
                higherIsBetter: true,
              },
            },
            {
              id: "cancelled",
              type: "kpi",
              title: "Cancelled",
              position: at(8, 2, 4, 3),
              data: {
                query: {
                  source: a,
                  measures: [
                    {
                      name: "cancelled",
                      formula: `COUNT(WHERE status = "cancelled") / COUNT()`,
                    },
                  ],
                },
                dateColumn: "starts_at",
              },
              display: {
                format: "percent",
                compare: true,
                higherIsBetter: false,
              },
            },
            {
              id: "per-week",
              type: "bar",
              title: "Appointments per week",
              position: at(0, 5, 8, 5),
              data: {
                query: {
                  source: a,
                  groupBy: [{ column: "starts_at", bucket: "week", as: "week" }, { column: "status" }],
                  measures: [{ name: "appointments", fn: "count" }],
                },
                dateColumn: "starts_at",
              },
              display: { format: "integer" },
            },
            {
              id: "by-contact",
              type: "donut",
              title: "Who you meet",
              position: at(8, 5, 4, 5),
              data: {
                query: {
                  source: a,
                  groupBy: [{ column: "contact_id", top: 6, other: true }],
                  measures: [{ name: "appointments", fn: "count" }],
                },
                dateColumn: "starts_at",
              },
              display: { format: "integer" },
            },
          ],
        },
      },
    ];
  },
  forms: (n) => [
    {
      name: "New appointment",
      form: {
        table: n.appointments,
        pinned: true,
        fields: [
          { column: "title", label: "Title" },
          { column: "starts_at", label: "Starts" },
          { column: "ends_at", label: "Ends" },
          { column: "contact_id", label: "With" },
          { section: "More" },
          { column: "location", label: "Location" },
          {
            column: "status",
            label: "Status",
            default: { kind: "value", value: "planned" },
          },
          { column: "note", label: "Note" },
        ],
      },
    },
  ],
  seed: async (n, createRow, today) => {
    const contacts = await createAll(createRow, n.contacts, [
      { name: "Alex Meyer", email: "alex@example.com" },
      { name: "Sam Rivera", phone: "+49 30 1234567" },
      { name: "Dr. Kim", note: "Dentist" },
    ]);
    const at = (back: number, hour: number) => `${daysAgo(today, back)}T${String(hour).padStart(2, "0")}:00:00`;
    const appointments: [number, number, string, number, string, string][] = [
      [60, 10, "Kick-off", 0, "Office", "done"],
      [45, 14, "Coffee", 1, "Café Central", "done"],
      [38, 9, "Check-up", 2, "Practice", "done"],
      [30, 11, "Review", 0, "Online", "done"],
      [21, 15, "Coffee", 1, "Café Central", "cancelled"],
      [14, 10, "Planning", 0, "Office", "done"],
      [7, 16, "Lunch", 1, "Downtown", "done"],
      [-3, 10, "Follow-up", 0, "Online", "planned"],
      [-10, 9, "Check-up", 2, "Practice", "planned"],
      [-17, 13, "Lunch", 1, "Downtown", "planned"],
    ];
    for (const [back, hour, title, contact, location, status] of appointments)
      await createRow(n.appointments, {
        title,
        starts_at: at(back, hour),
        ends_at: at(back, hour + 1),
        contact_id: contacts[contact],
        location,
        status,
      });
  },
};
