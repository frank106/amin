# Tests

ATAS only ships its SDK with the Windows platform, so these projects build
`FvgReactionLiquiditySweep.cs` against **stand-ins for the ATAS API** and exercise it
without an ATAS install:

* `AtasApiStubs` — the slice of the ATAS SDK the indicator uses (`Indicator`,
  `ValueDataSeries`, `IndicatorCandle`, `MarketDataArg`, `MarketDepthInfo`, `RenderContext`,
  `PenSettings`, ...), with the signatures the official
  [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators) sources use, and the
  trading calls (`ITradingManager`, `Order`, `MyTrade`, `Position`) as ATAS X 8.0.15 has them. It
  also records draw calls and lets the harness feed bars, ticks, order-book updates, prints and
  the broker's answers to orders. Never reference it from the real indicator build.
* `IndicatorTests` — a console runner (exit code 0 = everything passed).
* `ExecutionTests` — the same for the execution side (see [below](#execution)).
* `LogReport` — sums the executor's logs up (see [below](#logreport)).

```
dotnet run -c Release --project tests/IndicatorTests
```

Requires the .NET 8 SDK. With only a newer .NET installed (the .NET 10 SDK that builds for ATAS X,
say), let it run on that one: `DOTNET_ROLL_FORWARD=Major dotnet run ...`. What it checks:

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
* **filters**: a sweep needs the minimum depth in ATRs (a 1-tick poke isn't one, 3 ticks is), a
  gap the minimum size in ATRs, and with confirmed swings only the sweep of a real swing counts,
  once, and never after the swing was broken. Key levels end the same way whatever the depth;
* **brackets**: ATR multiples, the stop beyond the signal bar with its reward : risk, the Min /
  Max stop bounds that keep the ratio, the break-even shares, and the prior odds of each trade's
  own bracket;
* **limit entries**: an order that fills on a pullback (and one that fills on a gap through it,
  at the limit price), one that doesn't fill before it expires (a *No fill* chip, no tag, left out
  of the statistics), the bracket counted from the limit price, and the panel's waiting line;
* **randomised markets** (thousands of bars, historical and tick by tick), compared with an
  independent re-implementation of the rules:
  * every signal, trigger, FVG zone and its end, footprint fill and its reaction, and trade
    outcome, break-even stop moves included;
  * every key level (where it started, how and where it ended) and the level each key sweep
    took, with the new defaults, around the clock and under each hours choice. The oracle has its
    own New York clock from the time-zone database, and its own big-fill size;
  * the filters, brackets and entries of the latest version: ATR-sized sweeps and gaps, confirmed
    swings, custom windows (one over midnight), ATR and signal-bar brackets, limit entries with
    expiry and session ends (also tick by tick), and the odds split by time of day, volatility
    and trend. The oracle computes its own ATR, brackets, fills and odds groups;
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

Older ATAS versions (classic 8.0.14) give the chart's time zone as `InstrumentInfo.TimeZone`, in
whole hours, instead of `TimeZoneOffset`; the real build reads which one the installed ATAS has. To
run every check on that version of the code:

```
dotnet run -c Release --project tests/IndicatorTests -p:TimeZoneHours=true
dotnet run -c Release --project tests/ExecutionTests -p:TimeZoneHours=true
```

## Execution

`ExecutionTests` checks what the indicator does with *Execute signals* on. It leaves the signal
checks above alone, and it can't reach a real account: paper orders never leave the indicator, and
the live checks use a fake broker that only records the order calls, each check playing the
broker's answers itself.

```
dotnet run -c Release --project tests/ExecutionTests
```

What it checks:

* **defaults**: off, paper on, one contract, and nothing at all while off (no log, no panel line,
  no order call even when armed for live);
* **paper trades**, tick by tick, word for word in the log: the market entry a tick of slippage
  above the signal's close, the signal's own stop and take profit (the stop first), the stop moved
  at the break-even trigger, and every ending - a take profit traded through, one only touched
  (closed at market with the chart's trade), the break-even stop and the stop loss with slippage,
  a SHORT, 3 contracts, limit entries filled a tick through or cancelled with the chart's order,
  *Max bars in trade* and the session end;
* **the daily loss limit**: the breaker trips on the losing close, the rest of the day's signals
  are skipped, the next trading day (18:00 New York) starts from zero in a file of its own; a
  trade whose stop could breach the limit is skipped; a restarted indicator reads the day back
  from the log; paper and live count apart;
* **what the chart decides**: no signal outside the signal hours, hidden signals skipped (*One
  trade at a time*), one position at a time even when the chart shows overlapping signals, nothing
  from history, and a recalculation that even removes the signal leaves the open position and
  its orders to finish as they would have;
* **the panel and alerts**: the executor's lines, and alerts on fills and results (or not);
* **live orders** against the fake broker: not armed without the chart's account, a daily loss
  limit, a connection or the right tick size, or when the account already holds a position; the
  calls themselves (a market BUY for the chart's account, no confirmation dialog, the stop before
  the take profit in one OCO group, ModifyOrderAsync to break-even, the leftover leg cancelled);
  fills reported before or after the order's state, twice, or for someone else's order; partial
  fills resizing the bracket; a take profit filling while the executor cancels it; a rejected stop
  (closed at market, halted); a stop cancelled outside the indicator (closed and halted a few
  seconds on, but not when its take profit's fill cancelled it); a position closed outside the
  indicator (halted);
* **the safety settings**: one live trade per account across charts, given back when the position is
  done or its chart removed, and never holding paper back; live, the account's closed P&L reaching
  the limit or leaving too little room for a trade's stop, and not with the setting off or on
  paper; the flat-by time closing a position at market (the chart's trade carrying on), cancelling a
  waiting entry, skipping later signals until 18:00, and closing at the new day's first price when
  it falls in the daily break; time in force on the live stop, take profit and limit entry only,
  kept when the stop moves; the break-even stop counted from the average fill, on the tick grid;
* **the daily profit target**: made on the close that reaches it (a `TARGET` row, an alert, the
  panel), the rest of the day's signals skipped, still made after a restart, and the next day from
  zero;
* **the instrument's costs**: an MNQ chart's $0.50 a tick and $1.50 commission, in a trade's
  result and in its risk; the product under every feed's name; the settings with it off, on an
  instrument the executor doesn't know, or when the tick size isn't the contract's;
* **the day's trend** as the executor sees it: the scripted BUY on a day going up is taken, the
  scripted SHORT on a day with no clear trend is no signal, and the panel says which way the day goes;
* **the log**: a day's file an earlier version started keeps its columns and is still read back;
  a new one has every column; the log report sums a scripted day up;
* **random markets**, streamed tick by tick to one indicator executing and one not, under six
  settings: the chart's trades and panel counts must come out identical, and the log must
  reconcile with the chart - one decision row per real-time signal, each with its label's and its
  worst-case EV, orders only from shown signals at their prices, one position at a time, each trade
  copy ending as the chart's trade did, each day's results adding up, the breaker tripping on the
  first close at or below the limit and the profit target on the first close that makes it, and at
  no other time, and the log report adding up to the log, table by table. With *Filter trades by
  worst-case odds* on, every trade taken passes on both odds and the signals only the worst case
  failed are skipped.

`IndicatorTests` checks the worst-case odds themselves: with the chart settling the worst case too,
they equal the labels' trade by trade; with the default rule they differ and lean lower. It also
checks the day's trend on a random market with volume: with *Only trade with the day's trend* on,
every signal goes the day's way, worked out bar by bar with the system's own New York clock.

Every one of 24 deliberate bugs put into the execution code as first written (a hidden signal traded, the breaker
late, the stop placed after the take profit, slippage the wrong way, fills counted twice...) makes
at least one check fail.

## LogReport

`LogReport` sums up the executor's CSV logs - weeks of paper trading, or the live account's:

```
dotnet run -c Release --project tests/LogReport
dotnet run -c Release --project tests/LogReport -- D:\logs --mode live --from 2026-10-01 --to 2026-10-31
```

Without a folder it reads the indicator's default one (`%APPDATA%\ATAS\FvgExecution` on Windows,
`~/Library/Application Support/ATAS/FvgExecution` on a Mac), paper logs unless `--mode live` or `all`.
It reports the closed trades - all, buys and shorts, then by setup, number of confirmations, each
confirmation (trend, delta, order flow, pattern), the EV on the label and the New York hour - each
with its TP / BE / SL and other endings, ticks a trade, dollars after commission, the label's and the
worst case's average EV, and what the executor made against the chart's own trades (slippage, exits
at market). It says how many standard errors the average is from zero, and why signals were skipped.

The backtest's bars have no footprint, so this is where the Fill signals and the delta and order-flow
confirmations get judged. A row with a handful of trades is noise: wait for a few dozen.

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
* **Costs**: `--commission` ticks a round trip (1) and `--slippage` ticks on each market order
  (1): the entry at the close and every exit. A trade entered at the close costs 3 ticks, one with
  a limit entry 2.
* **Ticks**: `--ticks file.csv` (time and price, `.gz` too, repeatable) replays each bar that has
  ticks tick by tick, as ATAS feeds a live chart, so trades follow the real path inside those bars
  instead of an assumed one. Those bars are rebuilt from their ticks. The report then puts the
  result on the ticks next to the same history on the bars alone (as assumed, and worst case), and
  says how much of the trades' time the ticks covered. Give ticks for the whole stretch you test
  (`--from` / `--to`): where there are none, trades are settled on the bars as before.
* **Output**:
  * TP / BE / SL / expired, ticks per trade before and after costs, net ticks and dollars, and
    how many standard errors the average is from zero;
  * the largest drawdown, the months up and the longest run of stops;
  * **the same history replayed with the worst-case high / low order** inside each bar, next to
    the result. OHLC bars can't show the path inside a bar, so the truth usually lies between the
    two; when they are far apart, the bars can't judge that bracket;
  * tables by year, month, setup, key level, hour, weekday, pattern and the expected ticks on the
    labels;
  * the odds check, and a check that the totals match the panel's;
  * `--split <date>` reports the signals before and from a date apart: pick settings on the first
    stretch and judge them once on the second;
  * `--summary` prints all of it as one line of JSON instead, for scripted searches;
  * `--trades file.csv` lists every signal. The top of `Program.cs` lists all options.
* **What it can't test**: plain OHLC bars have no footprint, so it can't test the Fill signals or
  the order-flow and delta confirmations.
* **Checked**:
  * two years of 1-minute bars run in about 12 seconds, the worst-case replay included;
  * the six common file layouts read into identical bars;
  * on driftless noise a plain 80 / 80 bracket ends 50.0% TP with a net of 0, so there is no
    look-ahead;
  * fed PathCheck's simulated market as bars and ticks, the tick replay settles all 2,952 signals
    of 40 days exactly as PathCheck's path does.
* **The high / low order**: with the break-even stop on, the path inside a bar matters. On
  simulated tick paths (60 steps a minute) with NQ's 1-minute ranges, settling on the bars with the
  default rule (open to the nearer extreme first) overstated the default 80 / 80 bracket
  (break-even +40 → +20) by 1–2 ticks a trade, and a 40 / 40 bracket with break-even at +20 → +10
  by 4–5 ticks: price that reaches the trigger inside a bar often comes back to the moved stop
  before the close. Brackets without break-even and ATR-sized ones came out within about half a
  tick. *Worst case* was about 9 ticks too pessimistic for the default bracket and 1–2 for the
  40 / 40 one. With 240 steps a minute the overstatement grew from 4.1 to 5.2 ticks, so the finer
  real ticks likely widen it further.

## PathCheck

`PathCheck` measures how far a backtest on 1-minute bars is off for given settings. It builds
driftless noise on the CME schedule step by step (60 steps a minute), then settles the same
signals on the path inside the bars (as a live chart does) and on the finished bars with each
high / low order rule:

```
dotnet run -c Release --project tests/PathCheck -- --set TakeProfitTicks=40 --set StopLossTicks=40 \
    --set BreakEvenTriggerTicks=20 --set BreakEvenStopTicks=10
```

It prints the TP / BE / SL shares and ticks a trade of each, how many signals ended another way
than on the path, and the ticks a trade each rule adds. `--volatility` scales the moves (1.4, the
default, gives bars of about 50 ticks in regular hours, like NQ in 2024–2026), `--steps` the
steps a minute, `--days` and `--seed` the sample, and `--set` takes any setting. The figures
under *The high / low order* above come from it (seeds 1 and 7, 60 to 250 days, volatility 1.4
and 1.7). Real prices come back inside a minute more often than a random walk does: on the ticks
of 71 days of the Nasdaq-100 CFD (the runner's `--ticks`), the bars overstated the default bracket
by 2.6 ticks a trade and the 40 / 40 one by 4–5, more than simulated. Treat its figures as a
floor.

