import { OutlinedNumeral } from "./OutlinedNumeral";
import "./PlaceholderCard.css";

type PlaceholderCardProps = {
  /** The module's name as it appears in the navigation. */
  module: string;
  title: string;
  description: string;
  /** Short mark drawn as the outlined glyph, e.g. "D". */
  mark: string;
  bullets?: string[];
};

/**
 * The state shown on routes for modules that are planned but not built yet.
 * States what the module will do, without pretending to work.
 */
export function PlaceholderCard({ module, title, description, mark, bullets = [] }: PlaceholderCardProps) {
  return (
    <section className="later-module" aria-labelledby="later-module-title">
      <div className="later-module__text">
        <span className="section-label later-module__eyebrow">
          {module} · coming soon
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
      <OutlinedNumeral value={mark} className="later-module__numeral" opacity={0.6} />
    </section>
  );
}
