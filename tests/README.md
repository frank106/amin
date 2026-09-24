# Tests

ATAS only ships its SDK with the Windows platform, so these projects build
`FvgReactionLiquiditySweep.cs` against **stand-ins for the ATAS API** and exercise it
without an ATAS install:

* `AtasApiStubs` — the slice of the ATAS SDK the indicator uses (`Indicator`,
  `ValueDataSeries`, `IndicatorCandle`, `MarketDataArg`, `MarketDepthInfo`, `RenderContext`,
  `PenSettings`, ...), with the signatures the official
  [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators) sources use. It also
  records draw calls and lets the harness feed bars, ticks, order-book updates and prints.
  Never reference it from the real indicator build.
* `IndicatorTests` — a console runner (exit code 0 = everything passed).

```
dotnet run -c Release --project tests/IndicatorTests
```

Requires the .NET 8 SDK. What it checks:

* **trade settlement**:
  * TP, SL, gaps through a level, the break-even trigger and stop, and all three rules for
    ordering a bar's high and low;
  * the random-walk hitting probabilities, including a Monte Carlo cross-check, and the live
    odds of an open trade;
* **the probability model**: the three-outcome (TP / break-even / SL) smoothing over the four
  triggers, the coin-flip prior odds, and label percentages that always add up to 100;
* **candlestick patterns**:
  * a hand-built case for every pattern (the cheat sheet's and the ten new ones) and the near
    misses just outside each rule;
  * each case checked for bullish patterns and again upside down (mirrored) for bearish ones,
    and with its pattern's switch off;
  * the hammer shapes read by context, the strength order, how a bar with patterns both ways
    is decided, and the average-body yardstick;
* **scripted markets**:
  * FVGs: a gap's own candle is not a retest; a hammer at a bullish gap is a bullish reaction
    that gives a BUY hitting TP 80 ticks later; a bearish engulfing at a bullish gap is a
    bearish reaction and a SHORT; a reaction needs a pattern that includes the candle that
    touched the gap; used, filled (all three fill rules) and expired gaps stop being watched;
  * sweeps and trades: a sweep of highs gives a SHORT, a shooting star on the sweep bar confirms
    it, and a trade whose stop moved at +40 ticks exits at +20. Live, a dip below +20 before the
    trigger does not stop the trade, and a signal only appears once its bar has closed;
  * fills: the footprint's busiest price is a fill of the side that was hit, its reaction is
    read over the window, it confirms only on the right side and at the right end of the bar,
    and a reaction to it is a *Fill* signal;
  * order book: levels of 70+ become resting orders; a level that leaves is filled, pulled or
    out of view depending on the prints at its price (including prints that arrive after the
    book change); a filled order marks the footprint fill of the same bar, price and side
    (whichever is seen first, never across sides), or becomes a fill of its own once 150 traded
    against it, gets its reaction, and never makes a signal;
* **randomised markets** (thousands of bars, historical and tick by tick), compared with an
  independent re-implementation of the rules:
  * every signal, trigger, FVG zone and its end, footprint fill and its reaction, and trade
    outcome, break-even stop moves included;
  * the candlestick patterns of every bar in both directions and both contexts. The oracle
    derives the bearish ones by mirroring the candles, and the market has to show every pattern;
  * no look-ahead in any estimate, one trade at a time, cooldowns, filters, expiry and session
    ends; panel totals, and alerts only in real time;
  * identical signals between a tick-by-tick stream and a historical load, while a simulated
    order book trades, refills and pulls around the stream and is checked against the resting
    orders and order-book fills the indicator reports;
  * deterministic recalculation;
* **rendering**: zones (active, and retired ones when switched on), bubbles (solid or faint)
  and rings, order bands with size tags only for orders still waiting, sweeps from their swing,
  reaction markers, the open trade's card and every closed trade's result chip (checked word for
  word, and every card with compact labels off), full and faint trade boxes, price tags on top,
  the panel, every kind of hover tooltip, cluster mode and the display defaults.

ATAS's platform color type differs by edition: classic ATAS uses WPF's
`System.Windows.Media.Color`, ATAS X uses `System.Drawing.Color`. By default the stubs use a
WPF-like stand-in. Two switches cover the real models:

```
dotnet run -c Release --project tests/IndicatorTests -p:CrossColor=true   # ATAS X model, runs all checks
dotnet build -c Release tests/IndicatorTests -p:WpfColor=true              # real WPF type, compile only
```
