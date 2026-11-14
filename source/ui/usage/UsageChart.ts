import { Chart, LineController, LineElement, PointElement, LinearScale, Tooltip } from "chart.js";

Chart.register(LineController, LineElement, PointElement, LinearScale, Tooltip);

export { Chart };
