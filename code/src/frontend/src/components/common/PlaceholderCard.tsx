import { OutlinedNumeral } from "./OutlinedNumeral";
import "./PlaceholderCard.css";

type PlaceholderCardProps = {
  /** The module's name as it appears in the navigation. */
  module: string;
  title: string;
  description: string;
  /** Planned phase from the project plan, e.g. 3. */
  phase: number;
  bullets?: string[];
};

/**
 * The state shown on routes for modules that are planned but not built yet.
 * States what the module will do and when, without pretending to work.
 */
export function PlaceholderCard({ module, title, description, phase, bullets = [] }: PlaceholderCardProps) {
  return (
    <section className="later-module" aria-labelledby="later-module-title">
      <div className="later-module__text">
        <span className="section-label later-module__eyebrow">
          {module} · planned for phase {phase}
        </span>
        <h2 id="later-module-title" className="later-module__title display">
          {title}
        </h2>
        <p className="later-module__description">{description}</p>
        {bullets.length > 0 ? (
          <ul className="later-module__list">
            {bullets.map((bullet) => (
              <li key={bullet}>{bullet}</li>
            ))}
          </ul>
        ) : null}
      </div>
      <OutlinedNumeral value={String(phase)} className="later-module__numeral" opacity={0.6} />
    </section>
  );
}
