import { PlaceholderCard } from "../components/common/PlaceholderCard";

export function ModelPage() {
  return (
    <PlaceholderCard
      title="Schema Designer placeholder"
      description="The shell and route are now stable. Steps 2.4 and 2.5 will turn this into the JSON workbench and visual ERD designer, including the reusable column inspector that no longer lives on the Data Explorer page."
      bullets={["Stable route: /model", "Planned: reusable column / field inspector panel"]}
    />
  );
}
