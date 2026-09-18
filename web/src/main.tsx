import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";

function App() {
  return (
    <main>
      <p className="eyebrow">TetRail</p>
      <h1>Đặt vé tàu cao điểm</h1>
      <p>Project skeleton is ready. Product features will be delivered through the feature plans.</p>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

