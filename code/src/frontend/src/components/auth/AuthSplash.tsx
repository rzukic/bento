import { Sky } from "../common/Atmosphere";
import { Icon } from "../common/Icon";
import "./RequireAuth.css";

/**
 * Full-screen placeholder shown while the app works out where to send the
 * user (installation check, silent session resume). Uses the same sky as the
 * login and setup pages so the hand-off to either feels continuous.
 */
export function AuthSplash({ label }: { label: string }) {
  return (
    <div className="auth-splash" role="status" aria-live="polite">
      <Sky />
      <div className="auth-splash__body">
        <span className="auth-splash__mark" aria-hidden="true">
          <Icon name="layers" size={17} />
        </span>
        <span className="auth-splash__track" aria-hidden="true">
          <span className="auth-splash__bar" />
        </span>
        <span className="auth-splash__label">{label}</span>
      </div>
    </div>
  );
}
