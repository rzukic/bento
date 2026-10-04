import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider } from "react-router-dom";

import "@fontsource-variable/public-sans";
import "@fontsource/jetbrains-mono/400.css";
import "@fontsource/jetbrains-mono/500.css";
import "@fontsource/jetbrains-mono/700.css";
import "@fontsource/space-grotesk/500.css";
import "@fontsource/space-grotesk/700.css";

import { OverlayScrollbars } from "./components/common/OverlayScrollbars";
import { SESSION_SUSHI, applyFavicon } from "./components/brand";
import { router } from "./router";
import "./styles.css";

// The tab shows the same sushi as the app (a different one each load).
applyFavicon(SESSION_SUSHI);

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <RouterProvider router={router} />
    <OverlayScrollbars />
  </React.StrictMode>
);
