import { useParams } from "react-router-dom";
import { PlaceholderCard } from "../components/common/PlaceholderCard";

export function TablePage() {
  const { tableId } = useParams();

  return (
    <PlaceholderCard
      title="Table route placeholder"
      description={`Dynamic table route is active and already feeds the shell breadcrumb trail. Current table id: ${tableId ?? "unknown"}.`}
      bullets={["This route will host schema-driven table browsing in Step 2.2."]}
    />
  );
}
