import {
  Chart,
  BarController,
  BarElement,
  CategoryScale,
  Filler,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  Tooltip,
} from "chart.js";

Chart.register(
  BarController,
  BarElement,
  CategoryScale,
  Filler,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  Tooltip,
);

export { Chart };
