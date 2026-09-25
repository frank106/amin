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
* **sessions and key levels**:
  * New York time for every half hour of 2024–2028 against the system's time-zone database,
    the trading day that turns at 18:00, and each *Signal hours* choice at its edges;
  * a scripted two-day market: overnight, opening-range and prior-day levels posted on the right
    bar and swept or broken by the right one. The key-sweep signals are named after the most
    important level, including one where the prior day outranks equal highs posted before it,
    and the hours choices keep only the right signals;
  * equal highs: the match in ticks, a higher bar or another swing in between, the lookback both
    ways, and expiry;
* **adaptive sizes**:
  * a big fill ranks against earlier bars only, as a top share of the lookback, and an
    order-book fill needs the same size;
  * a resting order measured against the book keeps the size it needed, with the fixed 70 while
    the book is thin;
* **scoreboard**: the rows per setup and per pattern and the odds buckets, recounted from the
  trades, shown on hover of the panel or kept open;
* **randomised markets** (thousands of bars, historical and tick by tick), compared with an
  independent re-implementation of the rules:
  * every signal, trigger, FVG zone and its end, footprint fill and its reaction, and trade
    outcome, break-even stop moves included;
  * every key level (where it started, how and where it ended) and the level each key sweep
    took, with the new defaults, around the clock and under each hours choice. The oracle has its
    own New York clock from the time-zone database, and its own big-fill size;
  * the candlestick patterns of every bar in both directions and both contexts. The oracle
    derives the bearish ones by mirroring the candles, and the market has to show every pattern;
  * no look-ahead in any estimate, one trade at a time, cooldowns, filters, expiry and session
    ends; panel totals, and alerts only in real time;
  * identical signals and key levels between a tick-by-tick stream and a historical load, while
    a simulated order book trades, refills and pulls around the stream and is checked against
    the resting orders and order-book fills the indicator reports;
  * deterministic recalculation;
* **rendering**: zones (active, and retired ones when switched on), bubbles (solid or faint)
  and rings, order bands with size tags only for orders still waiting, sweeps from their swing,
  reaction markers, the open trade's card and every closed trade's result chip (checked word for
  word, and every card with compact labels off), full and faint trade boxes, price tags on top,
  the panel, every kind of hover tooltip, cluster mode and the display defaults; key levels
  with a dot per sweep, dashes per break, their tags and tooltips.

Most checks run with the settings of the first rewrite (all hours, fixed sizes, no key levels),
so each feature is tested on its own. The newer defaults are checked in *Defaults of the newer
features*, and one randomised run uses them all.

ATAS's platform color type differs by edition: classic ATAS uses WPF's
`System.Windows.Media.Color`, ATAS X uses `System.Drawing.Color`. By default the stubs use a
WPF-like stand-in. Two switches cover the real models:

```
dotnet run -c Release --project tests/IndicatorTests -p:CrossColor=true   # ATAS X model, runs all checks
dotnet build -c Release tests/IndicatorTests -p:WpfColor=true              # real WPF type, compile only
```

## Backtest

`Backtest` replays bars from a CSV through the same indicator code, on the same stubs, and
reports how its signals ended, as the panel would on a chart loaded with that history:

```
dotnet run -c Release --project tests/Backtest -- nq-1min.csv --tz America/New_York --preset 1min
```

* **Input**: most CSV layouts. It reads a header naming the columns, or time, open, high, low,
  close, volume without one. A date and a time may be two columns, times may be Unix seconds,
  milliseconds or nanoseconds, and files may be `.gz` or `.zip`. `--tz` gives the zone of times
  without an offset, and `--bar-time close` handles files stamped at the bar's close. When a file
  mixes contracts, each trading day keeps its most traded one.
* **Settings**: the indicator's defaults, `--preset 1min` (Swing Lookback 30, Min FVG Size 8,
  cooldown 10), and `--set Name=Value` for any setting.
* **Output**:
  * TP / BE / SL / expired, net ticks and dollars, before and after costs;
  * the largest drawdown and the longest run of stops;
  * tables by year, month, setup, key level, hour, weekday and pattern;
  * the odds check, and a check that the totals match the panel's.
  * `--trades file.csv` lists every signal. The top of `Program.cs` lists all options.
* **What it can't test**: plain OHLC bars have no footprint, so it can't test the Fill signals or
  the order-flow and delta confirmations.
* **Checked**:
  * two years of 1-minute bars run in about 10 seconds;
  * the six common file layouts read into identical bars;
  * on driftless noise a plain 80 / 80 bracket ends 50.0% TP with a net of 0, so there is no
    look-ahead.
* **The high / low order**: with the break-even stop on, the order of the high and low inside a
  bar matters. On noise with NQ-like 1-minute ranges, the default rule (open to the nearer extreme
  first) was within +0.5 ticks per trade of settling each trade on the path inside its bars.
  *Worst case* came out 9 ticks per trade too pessimistic.
