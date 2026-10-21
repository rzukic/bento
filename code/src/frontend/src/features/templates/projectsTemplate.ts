import { daysAgo } from "./names";
import { at, createAll, idColumn, type SimpleTemplate } from "./types";

/** Projects and the hours spent on them: billable value, planned hours a month, a "Log hours" form. */
export const projectsTemplate: SimpleTemplate = {
  id: "projects-hours",
  title: "Projects & hours",
  description:
    "Projects with a client, an hourly rate and planned hours a month, and the time spent on them. A dashboard with hours per week, billable value, the share of billable hours and each project against its plan; a form, Log hours, for quick entry.",
  base: { projects: "projects", time_entries: "time_entries" },
  prefixes: ["work_", "my_"],
  tables: (n) => [
    {
      name: n.projects,
      displayName: "Projects",
      columns: [
        idColumn,
        {
          name: "name",
          displayName: "Name",
          type: "text",
          nullable: false,
          unique: true,
        },
        { name: "client", displayName: "Client", type: "text" },
        {
          name: "status",
          displayName: "Status",
          type: "enum",
          values: ["active", "paused", "done"],
          default: "active",
        },
        { name: "hourly_rate", displayName: "Hourly rate", type: "currency" },
        {
          name: "planned_hours",
          displayName: "Planned hours a month",
          type: "float",
        },
        {
          name: "hours",
          displayName: "Hours",
          type: "computed",
          formula: `SUM(${n.time_entries}.hours)`,
        },
      ],
    },
    {
      name: n.time_entries,
      displayName: "Time entries",
      columns: [
        idColumn,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        {
          name: "project_id",
          displayName: "Project",
          type: "reference",
          references: n.projects,
          nullable: false,
        },
        { name: "hours", displayName: "Hours", type: "float", nullable: false },
        {
          name: "status",
          displayName: "Status",
          type: "enum",
          values: ["billable", "internal"],
          default: "billable",
        },
        { name: "note", displayName: "Note", type: "text" },
        {
          name: "value",
          displayName: "Billable value",
          type: "computed",
          formula: `IF(status = "billable", hours * COALESCE(project_id.hourly_rate, 0), 0)`,
        },
      ],
    },
  ],
  relations: (n) => [
    {
      name: `${n.projects}_${n.time_entries}`,
      type: "1:N",
      from: n.projects,
      to: n.time_entries,
      column: "project_id",
    },
  ],
  dashboards: (n) => {
    const t = n.time_entries;
    return [
      {
        name: "Hours",
        dashboard: {
          description: "Where the time goes",
          dateRange: { preset: "last-3-months" },
          filters: [{ column: "project_id", label: "Project" }],
          widgets: [
            {
              id: "hours",
              type: "kpi",
              title: "Hours",
              position: at(0, 0, 3, 2),
              data: {
                query: {
                  source: t,
                  measures: [{ name: "hours", fn: "sum", column: "hours" }],
                },
                dateColumn: "date",
              },
              display: {
                format: "number",
                compare: true,
                higherIsBetter: true,
              },
            },
            {
              id: "billable-value",
              type: "kpi",
              title: "Billable value",
              position: at(3, 0, 3, 2),
              data: {
                query: {
                  source: t,
                  measures: [{ name: "value", fn: "sum", column: "value" }],
                },
                dateColumn: "date",
              },
              display: {
                format: "currency",
                compare: true,
                higherIsBetter: true,
              },
            },
            {
              id: "billable-share",
              type: "kpi",
              title: "Billable share",
              position: at(6, 0, 3, 2),
              data: {
                query: {
                  source: t,
                  measures: [
                    {
                      name: "share",
                      formula: `SUM(hours WHERE status = "billable") / SUM(hours)`,
                    },
                  ],
                },
                dateColumn: "date",
              },
              display: {
                format: "percent",
                compare: true,
                higherIsBetter: true,
              },
            },
            {
              id: "active-projects",
              type: "kpi",
              title: "Active projects",
              position: at(9, 0, 3, 2),
              data: {
                query: {
                  source: n.projects,
                  measures: [
                    {
                      name: "active",
                      formula: `COUNT(WHERE status = "active")`,
                    },
                  ],
                },
              },
              display: { format: "integer", compare: false },
            },
            {
              id: "hours-per-week",
              type: "bar",
              title: "Hours per week",
              position: at(0, 2, 8, 5),
              data: {
                query: {
                  source: t,
                  groupBy: [{ column: "date", bucket: "week", as: "week" }, { column: "status" }],
                  measures: [{ name: "hours", fn: "sum", column: "hours" }],
                },
                dateColumn: "date",
              },
              display: { format: "number" },
            },
            {
              id: "hours-by-project",
              type: "donut",
              title: "Hours by project",
              position: at(8, 2, 4, 5),
              data: {
                query: {
                  source: t,
                  groupBy: [{ column: "project_id" }],
                  measures: [{ name: "hours", fn: "sum", column: "hours" }],
                },
                dateColumn: "date",
              },
              display: { format: "number" },
            },
            {
              id: "plan",
              type: "target",
              title: "This month against the plan",
              position: at(0, 7, 6, 5),
              data: {
                target: {
                  source: t,
                  value: "SUM(hours)",
                  date: "date",
                  group: "project_id",
                  target: "planned_hours",
                  per: "month",
                  period: "current",
                  kind: "goal",
                  format: "number",
                },
              },
            },
            {
              id: "recent",
              type: "table",
              title: "Latest entries",
              position: at(6, 7, 6, 5),
              data: {
                query: { source: t },
                rows: {
                  columns: ["date", "project_id", "hours", "status", "note"],
                  sort: "-date",
                  limit: 25,
                },
                dateColumn: "date",
              },
            },
          ],
        },
      },
    ];
  },
  forms: (n) => [
    {
      name: "Log hours",
      form: {
        table: n.time_entries,
        pinned: true,
        fields: [
          { column: "hours", label: "Hours" },
          { column: "project_id", label: "Project", default: { kind: "last" } },
          { column: "date", label: "Date", default: { kind: "today" } },
          { column: "status", label: "Status", default: { kind: "last" } },
          { section: "More" },
          { column: "note", label: "What you worked on" },
        ],
      },
    },
  ],
  seed: async (n, createRow, today) => {
    const [website, app, internal] = await createAll(createRow, n.projects, [
      {
        name: "Website relaunch",
        client: "Acme",
        status: "active",
        hourly_rate: 90,
        planned_hours: 40,
      },
      {
        name: "Mobile app",
        client: "Northwind",
        status: "active",
        hourly_rate: 100,
        planned_hours: 30,
      },
      { name: "Internal", status: "active", planned_hours: 8 },
    ]);
    // Six weeks of workdays: the website Mon/Wed/Fri, the app Tue/Thu, an internal hour on Fridays.
    for (let back = 41; back >= 0; back--) {
      const date = daysAgo(today, back);
      const day = new Date(`${date}T00:00:00Z`).getUTCDay();
      const entries: [unknown, number, string, string][] =
        day === 1 || day === 3
          ? [[website, 3, "billable", "Design and build"]]
          : day === 2 || day === 4
            ? [[app, 4, "billable", "Features"]]
            : day === 5
              ? [
                  [website, 2, "billable", "Review"],
                  [internal, 1, "internal", "Planning"],
                ]
              : [];
      for (const [project_id, hours, status, note] of entries)
        await createRow(n.time_entries, {
          date,
          project_id,
          hours,
          status,
          note,
        });
    }
  },
};
