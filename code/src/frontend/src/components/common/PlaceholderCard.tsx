import { EmptyState } from "../ui/EmptyState";
import { Panel } from "../ui/Panel";

type PlaceholderCardProps = {
  title: string;
  description: string;
  bullets?: string[];
};

export function PlaceholderCard({ title, description, bullets = [] }: PlaceholderCardProps) {
  return (
    <Panel>
      <EmptyState title={title} description={description} bullets={bullets} />
    </Panel>
  );
}
