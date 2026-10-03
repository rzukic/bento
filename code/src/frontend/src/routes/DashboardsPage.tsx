import { PlaceholderCard } from "../components/common/PlaceholderCard";

export function DashboardsPage() {
  return (
    <PlaceholderCard
      module="Dashboards"
      mark="D"
      title="See your data at a glance."
      description="Build dashboards from your tables with key figures, charts and lists, filter them all at once and watch them update as your data changes."
      bullets={["Spreadsheet-style formulas for calculated values", "Share a dashboard with a link"]}
    />
  );
}
