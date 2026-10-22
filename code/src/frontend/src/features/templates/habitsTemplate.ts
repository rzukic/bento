import { daysAgo } from "./names";
import { at, createAll, idColumn, type SimpleTemplate } from "./types";

/** Habits with a weekly goal and their check-ins, plus body measurements (weight, sleep, mood). */
export const habitsTemplate: SimpleTemplate = {
  id: "habits-health",
  title: "Habits & health",
  description:
    "Habits with a goal per week and a log of check-ins, plus measurements — weight, sleep, mood. A dashboard with this week's goals, check-ins per week and weight and sleep over time; two forms, Check in and Log measurement, for the phone.",
  base: {
    habits: "habits",
    habit_log: "habit_log",
    measurements: "measurements",
  },
  prefixes: ["health_", "my_"],
  tables: (n) => [
    {
      name: n.habits,
      displayName: "Habits",
      columns: [
        idColumn,
        {
          name: "name",
          displayName: "Name",
          type: "text",
          nullable: false,
          unique: true,
        },
        {
          name: "goal_per_week",
          displayName: "Goal per week",
          type: "integer",
        },
        {
          name: "check_ins",
          displayName: "Check-ins",
          type: "computed",
          formula: `COUNT(${n.habit_log})`,
        },
      ],
    },
    {
      name: n.habit_log,
      displayName: "Habit log",
      columns: [
        idColumn,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        {
          name: "habit_id",
          displayName: "Habit",
          type: "reference",
          references: n.habits,
          nullable: false,
        },
        { name: "note", displayName: "Note", type: "text" },
      ],
    },
    {
      name: n.measurements,
      displayName: "Measurements",
      columns: [
        idColumn,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        { name: "weight", displayName: "Weight (kg)", type: "float" },
        { name: "sleep_hours", displayName: "Sleep (hours)", type: "float" },
        { name: "mood", displayName: "Mood (1–5)", type: "integer" },
        { name: "note", displayName: "Note", type: "text" },
      ],
    },
  ],
  relations: (n) => [
    {
      name: `${n.habits}_${n.habit_log}`,
      type: "1:N",
      from: n.habits,
      to: n.habit_log,
      column: "habit_id",
    },
  ],
  dashboards: (n) => {
    const log = n.habit_log;
    const m = n.measurements;
    const perWeek = { column: "date", bucket: "week" as const, as: "week" };
    return [
      {
        name: "Habits & health",
        dashboard: {
          description: "Goals, check-ins and how you feel",
          dateRange: { preset: "last-3-months" },
          widgets: [
            {
              id: "check-ins",
              type: "kpi",
              title: "Check-ins",
              position: at(0, 0, 3, 2),
              data: {
                query: {
                  source: log,
                  measures: [{ name: "check_ins", fn: "count" }],
                },
                dateColumn: "date",
              },
              display: {
                format: "integer",
                compare: true,
                higherIsBetter: true,
              },
            },
            {
              id: "weight",
              type: "kpi",
              title: "Weight",
              position: at(3, 0, 3, 2),
              data: {
                query: {
                  source: m,
                  measures: [{ name: "weight", formula: "LAST(weight, date)" }],
                },
                dateColumn: "date",
              },
              display: { format: "number", compare: true },
            },
            {
              id: "sleep",
              type: "kpi",
              title: "Average sleep",
              position: at(6, 0, 3, 2),
              data: {
                query: {
                  source: m,
                  measures: [{ name: "sleep", fn: "avg", column: "sleep_hours" }],
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
              id: "mood",
              type: "kpi",
              title: "Average mood",
              position: at(9, 0, 3, 2),
              data: {
                query: {
                  source: m,
                  measures: [{ name: "mood", fn: "avg", column: "mood" }],
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
              id: "goals",
              type: "target",
              title: "This week's goals",
              position: at(0, 2, 5, 5),
              data: {
                target: {
                  source: log,
                  value: "COUNT()",
                  date: "date",
                  group: "habit_id",
                  target: "goal_per_week",
                  per: "week",
                  period: "current",
                  kind: "goal",
                  format: "integer",
                },
              },
            },
            {
              id: "check-ins-per-week",
              type: "bar",
              title: "Check-ins per week",
              position: at(5, 2, 7, 5),
              data: {
                query: {
                  source: log,
                  groupBy: [perWeek, { column: "habit_id" }],
                  measures: [{ name: "check_ins", fn: "count" }],
                },
                dateColumn: "date",
              },
              display: { format: "integer" },
            },
            {
              id: "weight-over-time",
              type: "line",
              title: "Weight",
              position: at(0, 7, 6, 4),
              data: {
                query: {
                  source: m,
                  groupBy: [perWeek],
                  measures: [{ name: "weight", fn: "avg", column: "weight" }],
                },
                dateColumn: "date",
              },
              // Measurements: a week without one is no 0 kg.
              display: { format: "number", yAxis: { min: "auto" }, emptyPeriods: "connect" },
            },
            {
              id: "sleep-over-time",
              type: "line",
              title: "Sleep and mood",
              position: at(6, 7, 6, 4),
              data: {
                query: {
                  source: m,
                  groupBy: [perWeek],
                  measures: [
                    { name: "sleep", fn: "avg", column: "sleep_hours" },
                    { name: "mood", fn: "avg", column: "mood" },
                  ],
                },
                dateColumn: "date",
              },
              display: { format: "number", emptyPeriods: "connect" },
            },
          ],
        },
      },
    ];
  },
  forms: (n) => [
    {
      name: "Check in",
      form: {
        table: n.habit_log,
        pinned: true,
        fields: [
          { column: "habit_id", label: "Habit" },
          { column: "date", label: "Date", default: { kind: "today" } },
          { column: "note", label: "Note" },
        ],
      },
    },
    {
      name: "Log measurement",
      form: {
        table: n.measurements,
        pinned: true,
        fields: [
          { column: "date", label: "Date", default: { kind: "today" } },
          { column: "weight", label: "Weight (kg)" },
          { column: "sleep_hours", label: "Sleep (hours)" },
          { column: "mood", label: "Mood (1–5)" },
          { section: "More" },
          { column: "note", label: "Note" },
        ],
      },
    },
  ],
  seed: async (n, createRow, today) => {
    const [run, read, meditate] = await createAll(createRow, n.habits, [
      { name: "Run", goal_per_week: 3 },
      { name: "Read", goal_per_week: 5 },
      { name: "Meditate", goal_per_week: 7 },
    ]);
    // Four weeks: runs Mon/Wed/Sat, reading on weekdays, meditation most days; a measurement every other day.
    for (let back = 27; back >= 0; back--) {
      const date = daysAgo(today, back);
      const day = new Date(`${date}T00:00:00Z`).getUTCDay();
      if (day === 1 || day === 3 || day === 6) await createRow(n.habit_log, { date, habit_id: run });
      if (day >= 1 && day <= 5) await createRow(n.habit_log, { date, habit_id: read });
      if (back % 3 !== 0) await createRow(n.habit_log, { date, habit_id: meditate });
      if (back % 2 === 0)
        await createRow(n.measurements, {
          date,
          weight: Math.round((78 + back * 0.05) * 10) / 10,
          sleep_hours: [7, 6.5, 8, 7.5][back % 4],
          mood: [4, 3, 5, 4][back % 4],
        });
    }
  },
};
