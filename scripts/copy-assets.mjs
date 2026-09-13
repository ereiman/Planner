import { copyFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const assets = [
  ["node_modules/sortablejs/Sortable.min.js", "src/Planner.Web/wwwroot/vendor/sortable.min.js"],
  ["node_modules/fullcalendar/all/global.js", "src/Planner.Web/wwwroot/vendor/fullcalendar.js"],
  ["node_modules/fullcalendar/skeleton.css", "src/Planner.Web/wwwroot/vendor/fullcalendar-skeleton.css"],
  ["node_modules/fullcalendar/themes/classic/global.js", "src/Planner.Web/wwwroot/vendor/fullcalendar-classic.js"],
  ["node_modules/fullcalendar/themes/classic/theme.css", "src/Planner.Web/wwwroot/vendor/fullcalendar-classic.css"],
  ["node_modules/fullcalendar/themes/classic/palette.css", "src/Planner.Web/wwwroot/vendor/fullcalendar-classic-palette.css"]
];

for (const [source, destination] of assets) {
  await mkdir(dirname(destination), { recursive: true });
  await copyFile(source, destination);
}
