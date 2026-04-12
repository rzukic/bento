import { PlaceholderCard } from "../components/common/PlaceholderCard";

export function DataPage() {
  return (
    <PlaceholderCard
      title="Data Explorer placeholder"
      description="This route now sits inside the real shared shell. Step 2.2 will replace this placeholder with the first browser-based table explorer."
      bullets={["Stable route: /data", "Stable detail route: /data/:tableId"]}
    />
  );
}
