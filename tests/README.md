# Tests

ATAS only ships its SDK with the Windows platform, so these projects build
`FvgReactionLiquiditySweep.cs` against **stand-ins for the ATAS API** and exercise it
without an ATAS install:

* `AtasApiStubs` — the slice of the ATAS SDK the indicator uses (`Indicator`,
  `ValueDataSeries`, `IndicatorCandle`, `RenderContext`, `PenSettings`, ...), with the
  signatures the official [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators)
  sources use. It also records draw calls and lets the harness feed bars and ticks.
  Never reference it from the real indicator build.
* `IndicatorTests` — a console runner (exit code 0 = everything passed).

```
dotnet run -c Release --project tests/IndicatorTests
```

Requires the .NET 8 SDK. What it checks:

* bracket resolution (TP, SL, both in one bar, gaps through a level) and the
  random-walk hitting probabilities, including a Monte Carlo cross-check;
* the smoothing arithmetic of the probability model;
* scripted markets: a gap's own candle is not a retest, an FVG retest gives a BUY that
  hits TP 80 ticks later, a sweep of highs gives a SHORT, a signal only appears once its
  bar has closed, absorption confirms only on the right side and at the right end of the
  bar, a reacted zone still dies when price closes through it;
* randomised markets (thousands of bars, historical and tick by tick), compared with an
  independent re-implementation of the rules: every signal, trigger arrow, FVG zone and
  trade outcome, no look-ahead in any estimate, one trade at a time, cooldowns, expiry,
  session ends, panel totals, alerts only in real time, identical results between a
  tick-by-tick stream and a historical load, and deterministic recalculation;
* rendering: labels, levels, panel, hover tooltip, cluster mode.

ATAS's platform color type differs by edition: classic ATAS uses WPF's
`System.Windows.Media.Color`, ATAS X uses `System.Drawing.Color`. By default the stubs use a
WPF-like stand-in. Two switches cover the real models:

```
dotnet run -c Release --project tests/IndicatorTests -p:CrossColor=true   # ATAS X model, runs all checks
dotnet build -c Release tests/IndicatorTests -p:WpfColor=true              # real WPF type, compile only
```
