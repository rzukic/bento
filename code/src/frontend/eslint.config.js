import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: ["dist/", "coverage/", "node_modules/", "*.js", "*.d.ts"]
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["src/**/*.{ts,tsx}", "vite.config.ts", "vitest.setup.ts"],
    plugins: {
      "react-hooks": reactHooks
    },
    languageOptions: {
      ecmaVersion: 2022,
      globals: {
        ...globals.browser,
        ...globals.node
      }
    },
    rules: {
      // Classic hook rules only. The React Compiler oriented rules shipped in
      // eslint-plugin-react-hooks v6+ (set-state-in-effect, immutability, ...)
      // flag design patterns rather than bugs and are deliberately not enabled.
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" }
      ],
      "no-console": ["warn", { allow: ["warn", "error"] }]
    }
  },
  {
    files: ["src/test/**/*.{ts,tsx}"],
    rules: {
      "no-console": "off"
    }
  }
);
