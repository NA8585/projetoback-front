# React + Vite

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Babel](https://babeljs.io/) for Fast Refresh
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/) for Fast Refresh

## Expanding the ESLint configuration

If you are developing a production application, we recommend using TypeScript with type-aware lint rules enabled. Check out the [TS template](https://github.com/vitejs/vite/tree/main/packages/create-vite/template-react-ts) for information on how to integrate TypeScript and [`typescript-eslint`](https://typescript-eslint.io) in your project.

## Raw Telemetry Decoder

This repository includes a small example script that decodes simplified raw
telemetry values from a text file and exposes them on the same WebSocket used by
the overlays.

1. Edit `sample-raw.txt` adding lines formatted as `Key:Value` pairs separated by
   spaces (for example: `Speed:120 RPM:6500 FuelLevel:45 FuelPerLap:2.5`).
2. Run the decoder with `node tools/raw-decoder.js sample-raw.txt`.
3. Overlays can connect to `ws://localhost:5221/ws` to receive the parsed data.

The decoder also computes a few derived values:

- `CurrentFuelPerLap` – the latest `FuelPerLap` value.
- `AvgFuelPerLap` – running average of fuel used each lap whenever a new `Lap`
  value is detected.
- `LapsRemainingCur` – estimated laps left using the current consumption.
- `LapsRemainingAvg` – estimated laps left using the average consumption.

If the raw lines contain fields like `S1`, `S2`, `S3`, they are exposed as a
`SectorTimes` array for use by the overlays.
