# 2025 Validation Summary

This run compares `bacinger/f1-circuits` 2025 GPS circuit LineStrings against
FastF1 race-lap `X/Y` shapes.

The FastF1 traces are transformed into GPS meter-space with similarity fitting:
scale, rotation, and translation. The metrics are therefore **shape-fit** errors,
not absolute car GPS errors.

## Summary

| Metric | Value |
|---|---:|
| Circuits processed | 24 |
| Best single-lap mean RMSE | 7.0 m |
| Best single-lap median RMSE | 7.0 m |
| Worst best-lap RMSE | 14.7 m |
| Average-of-5 mean RMSE | 7.8 m |
| Average-of-5 median RMSE | 7.3 m |
| Averaging improved | 10 / 24 |
| Averaging worsened | 14 / 24 |
| Polyline simplification tolerance | 1.0 m |
| Worst simplification error | <= 1.0 m |

## Full Table

| Round | Event | Circuit | Best RMSE | Best p95 | Avg-5 RMSE | Polyline pts | Chars | Max simpl. err |
|---:|---|---|---:|---:|---:|---:|---:|---:|
| 1 | Australian GP | Albert Park Circuit | 7.8 m | 13.8 m | 7.4 m | 108 | 335 | 0.99 m |
| 2 | Chinese GP | Shanghai International Circuit | 9.5 m | 15.9 m | 11.5 m | 117 | 337 | 0.94 m |
| 3 | Japanese GP | Suzuka International Racing Course | 4.8 m | 7.1 m | 5.2 m | 148 | 451 | 0.96 m |
| 4 | Bahrain GP | Bahrain International Circuit | 7.2 m | 12.2 m | 11.5 m | 84 | 244 | 0.68 m |
| 5 | Saudi Arabian GP | Jeddah Corniche Circuit | 5.4 m | 10.4 m | 6.2 m | 129 | 394 | 0.99 m |
| 6 | Miami GP | Miami International Autodrome | 5.5 m | 10.4 m | 6.1 m | 96 | 296 | 0.80 m |
| 7 | Emilia Romagna GP | Autodromo Enzo e Dino Ferrari | 4.5 m | 7.4 m | 7.4 m | 75 | 228 | 0.53 m |
| 8 | Monaco GP | Circuit de Monaco | 7.2 m | 11.6 m | 6.0 m | 117 | 323 | 0.99 m |
| 9 | Spanish GP | Circuit de Barcelona-Catalunya | 5.3 m | 8.9 m | 4.3 m | 111 | 315 | 0.99 m |
| 10 | Canadian GP | Circuit Gilles-Villeneuve | 5.0 m | 8.7 m | 14.6 m | 84 | 242 | 0.95 m |
| 11 | Austrian GP | Red Bull Ring | 3.9 m | 6.7 m | 4.6 m | 75 | 237 | 0.98 m |
| 12 | British GP | Silverstone Circuit | 7.5 m | 12.5 m | 9.1 m | 121 | 380 | 0.88 m |
| 13 | Belgian GP | Circuit de Spa-Francorchamps | 7.2 m | 14.6 m | 7.3 m | 133 | 417 | 0.89 m |
| 14 | Hungarian GP | Hungaroring | 4.4 m | 9.2 m | 5.7 m | 110 | 305 | 1.00 m |
| 15 | Dutch GP | Circuit Zandvoort | 8.6 m | 13.0 m | 7.7 m | 111 | 336 | 0.96 m |
| 16 | Italian GP | Autodromo Nazionale Monza | 6.1 m | 13.3 m | 4.0 m | 90 | 277 | 0.98 m |
| 17 | Azerbaijan GP | Baku City Circuit | 5.1 m | 9.5 m | 4.9 m | 79 | 254 | 0.96 m |
| 18 | Singapore GP | Marina Bay Street Circuit | 14.7 m | 36.6 m | 9.8 m | 90 | 254 | 0.95 m |
| 19 | United States GP | Circuit of the Americas | 7.1 m | 11.8 m | 8.0 m | 131 | 361 | 0.98 m |
| 20 | Mexico City GP | Autodromo Hermanos Rodriguez | 7.0 m | 12.2 m | 6.5 m | 87 | 237 | 0.90 m |
| 21 | Sao Paulo GP | Autodromo Jose Carlos Pace - Interlagos | 11.0 m | 20.7 m | 10.2 m | 121 | 334 | 0.99 m |
| 22 | Las Vegas GP | Las Vegas Street Circuit | 7.1 m | 12.7 m | 11.7 m | 88 | 274 | 0.98 m |
| 23 | Qatar GP | Losail International Circuit | 6.2 m | 10.8 m | 4.6 m | 101 | 289 | 0.77 m |
| 24 | Abu Dhabi GP | Yas Marina Circuit | 9.9 m | 16.6 m | 12.5 m | 106 | 302 | 0.96 m |

## Biggest Deviations

Using the best single FastF1 lap:

| Rank | Event | RMSE | p95 | Likely reason |
|---:|---|---:|---:|---|
| 1 | Singapore GP | 14.7 m | 36.6 m | Street circuit, tight corners, racing line diverges from mapped centerline. |
| 2 | Sao Paulo GP | 11.0 m | 20.7 m | FastF1 racing line and repo centerline differ most in high-curvature sections. |
| 3 | Abu Dhabi GP | 9.9 m | 16.6 m | Complex modern layout; likely small centerline/racing-line differences. |
| 4 | Chinese GP | 9.5 m | 15.9 m | Long-radius turns amplify small phase and centerline differences. |
| 5 | Dutch GP | 8.6 m | 13.0 m | Banked corners and racing-line variation. |

Using the average of five fitted FastF1 laps:

| Rank | Event | Avg-5 RMSE | Why it can worsen |
|---:|---|---:|---|
| 1 | Canadian GP | 14.6 m | Different good laps use visibly different racing lines; averaging pulls away from centerline. |
| 2 | Abu Dhabi GP | 12.5 m | Averaged racing lines do not equal the repository centerline. |
| 3 | Las Vegas GP | 11.7 m | Street-circuit line choice varies around braking and corner exits. |
| 4 | Chinese GP | 11.5 m | Large-radius turns make progress alignment sensitive. |
| 5 | Bahrain GP | 11.5 m | Multiple valid racing lines through braking/traction zones. |

## Averaging Result

Averaging helped on 10 circuits and hurt on 14 circuits. The conclusion is not
"always average"; it is:

- Use the **best representative lap** for validating repository GPS shape.
- Use the **averaged fitted lap** as a secondary diagnostic.
- Treat averaging improvements as evidence that one lap was noisy.
- Treat averaging degradation as evidence of racing-line variation.

