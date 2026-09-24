# FVG Reaction + Liquidity Sweep — ATAS indicator

A custom indicator for the [ATAS](https://atas.net) platform that marks Fair Value Gap
reactions, liquidity sweeps and footprint absorption, checks the signal bar for the
candlestick patterns of [TraderLion's cheat sheet](https://traderlion.com/technical-analysis/candlestick-patterns-cheat-sheet/),
and turns it all into **BUY / SHORT signals with the probability of each way the trade can
end**: the take profit (80 ticks), the break-even stop (the stop moves to +20 ticks once
the trade is 40 ticks in profit) or the stop loss (80 ticks).

![Layout preview](docs/preview.png)

*Layout preview produced by the test harness from synthetic data. It is not an ATAS
screenshot: ATAS draws the candles and arrows itself, with its own fonts and theme.*

## What it draws

| Element | Meaning |
|---|---|
| Green / red boxes | Unfilled bullish / bearish Fair Value Gaps (3-candle imbalances), extended to the right until price closes through them |
| Small arrows | Triggers: FVG reaction (lime / red) and liquidity sweep of lows / highs (aqua / magenta) |
| Orange / blue cells | Absorption heatmap from footprint data: orange = aggressive buying absorbed (often resistance), blue = aggressive selling absorbed (often support) |
| **Large arrows + label** | **BUY / SHORT signal** with its TP, break-even and SL probabilities, expected ticks and candlestick pattern |
| Shaded boxes | Each signal's trade: entry → TP shaded green, entry → SL shaded red, from the signal bar to the bar that settled it |
| Amber lines | Dotted: the break-even trigger (+40 ticks). Solid: the stop after it moved to +20 ticks |
| Panel | Results, sample size, the labels' track record and the live odds of the open trade |

## Signals

A signal is decided when a bar **closes** (it never repaints) and needs a trigger:

* **FVG** – price came back into an unfilled gap and closed back out of it in the gap's direction, with a candle in that direction.
* **Sweep** – the bar wicked beyond the high / low of the last *Swing Lookback* bars and closed back inside (a stop run).
* **Sweep+FVG** – an FVG reaction within *Sweep → FVG window* bars after a sweep on the same side: liquidity is taken, then price reverses out of an imbalance.

Each signal also counts up to four confirmations: **absorption** of the opposite side
near the signal bar's extreme (sellers absorbed near the low for a buy, buyers absorbed
near the high for a short), **trend** (close above / below the EMA), **delta** (bar
delta in the signal's direction) and a **candlestick pattern** pointing the signal's way
(below). Any of them can be made mandatory.

Entry is the signal bar's close; TP and SL are *Take profit* / *Stop loss* ticks away.
With the default 80 / 80 on NQ that is 20 points each way ($400 per NQ contract, $40 per
MNQ). Once the trade is *Break-even trigger* ticks in profit (40), its stop moves to
*Break-even stop* ticks in profit (+20), so from then on it ends at +80 or +20. Set the
trigger to 0 to trade the plain 80 / 80 bracket.

### Candlestick patterns

The fourth confirmation uses the patterns of TraderLion's candlestick cheat sheet. The
signal bar has to *complete* one of them, as the last candle of a two- or three-candle
pattern, and it has to point the signal's way: bullish patterns for buys, bearish ones
for shorts.

| Buy | Short | Candles | Rule |
|---|---|---|---|
| Hammer, dragonfly doji | Shooting star, gravestone doji | 1 | long wick below (above) at least 2× the body, the other wick at most a tenth of the range; a doji when the body is at most a tenth of the range |
| Inverted hammer | Hanging man | 1 | the same shapes the other way up |
| Bullish engulfing | Bearish engulfing | 2 | a bigger body, at least an average one, covering the whole body of the opposite candle before it |
| Piercing line | Dark cloud cover | 2 | after a long opposite candle, opens at or beyond its close and closes past the middle of its body, but not past its open |
| Bullish harami | Bearish harami | 2 | a long candle, then a small opposite one whose body stays inside it |
| Tweezer bottom | Tweezer top | 2 | a bearish then a bullish candle with the same low, within *Tweezer match* ticks (tops: bullish then bearish, same high) |
| Morning star | Evening star | 3 | a long candle, a small star (often a doji) no further than the middle of its body, then a long opposite candle closing past that middle |
| Three white soldiers | Three black crows | 3 | three long candles the same way, each opening inside the body before it, closing further and in the last quarter of its range |
| Bullish marubozu | Bearish marubozu | 1 | a long candle with (almost) no wicks. Not every cheat sheet lists it; switch it off to keep to the reversal patterns |

"Long" and "small" bodies are measured against the average body of the 14 candles before
the pattern (*Average body*).

* **Context.** The cheat sheet reads bullish patterns after a decline and bearish ones after
  a rally. The signal supplies that: a buy comes after a dip into an FVG or through a swing
  low, a short after a rally. That is also what tells a hammer from a hanging man, and an
  inverted hammer from a shooting star, since those are the same shapes read by where they appear.
* **Futures rarely gap between bars**, so "opens below the previous close" is read as "at or
  below", and the star of a morning / evening star needs no gap.
* A plain **doji** or **spinning top** is indecision and points neither way, so it doesn't
  count on its own. It shows up as the star of a morning / evening star.
* A bar can complete several patterns. The label names one of them (three-candle patterns
  first, e.g. `Morning star +1`) and the tooltip lists them all.
* All the patterns together count as one confirmation. If pattern-confirmed signals do
  better on your chart, the odds of their higher "conf" groups show it.

### Where the probability comes from

Every signal on the chart is followed forward until it ends: at the TP, at the break-even
stop or at the SL. When a new signal fires, its odds are the shares of **earlier, already
finished** signals of the same kind that ended each way — so the numbers on an old signal
are exactly what the indicator would have shown live (no look-ahead).

"The same kind" is layered: same direction → same trigger → same number of
confirmations. A narrow group with only a few past trades is blended with its broader
parent group (`p = (count + k·p_parent) / (trades + k)` for each ending, *k* =
*Probability smoothing*). The starting point is the odds of a coin-flip market: for 80 / 80
with the stop moving to +20 at +40 that is **TP 22%, BE 45%, SL 33%**, worth 0 ticks on
average (without break-even: 50 / 50). A fresh chart shows those numbers everywhere, and
they only move as real outcomes accumulate. Load more history for more reliable estimates.

**EV** (expected value) combines the three: `TP% × 80 + BE% × 20 − SL% × 80` ticks. A
positive EV means that kind of signal has paid on this chart so far.

### Reading the chart

```
BUY  TP 31% | BE 41% | SL 28%                   [OPEN]   <- odds of each ending (they add up to 100), result badge
Sweep+FVG | Hammer | conf 3/4 | n=14 | EV +11t           <- trigger, candlestick pattern (if any), confirmations,
                                                            past trades of this exact setup, expected ticks
```

The badge turns **TP** (green), **BE** (amber, stopped at +20), **SL** (red) or **EXP**
(expired) once the trade settles. Hover a label for the full breakdown: prices, the
break-even levels, the TP / BE / SL counts behind the estimate at each level, which
confirmations were present (with every candlestick pattern the bar completed), when the
stop moved and how the trade ended.

By default every signal is shown, weak ones included — a label with a negative EV tells you
that setup has cost ticks so far. Once the panel's track record shows the labels holding up
on your chart, set **Min expected ticks to show** (e.g. 5) to keep only the better setups on
screen; hidden signals are still followed, so the statistics keep learning from them.

The panel shows:

* TP / BE / SL counts and net ticks for longs, shorts and total (signals shown on the chart);
* open, expired and hidden signals, and how many settled trades the model has learned from;
* **track record** — the average ticks actually made by signals labelled with a positive EV and by the rest, so you can see whether the labels have been reliable on this chart;
* for an open trade, **live odds** from the current price, e.g. `Live BUY +44t, stop +20t: TP 38% | BE 62% | SL 0%`. Each leg (reaching +40 before the stop, then TP before the break-even stop) is a random walk with the drift implied by the entry odds.

### Honest limits

* The probability is a frequency from the history loaded on the chart, not a guarantee. Markets change; small samples stay close to 50% by design.
* Fills are idealised: entry at the close, exact TP / SL, no slippage or commission.
* On historical bars the order of the high and low inside one bar is unknown, and it matters whenever a bar holds both the TP and the SL, or reaches +40 and also trades back to +20. By default the indicator assumes price went from the open to the **nearer extreme first** (the assumption TradingView's strategy tester makes). *Worst case* assumes the order that hurts the trade — with break-even on, that means any bar that reaches +40 from an open below +20 counts as stopped at break-even, which is very pessimistic on 1-minute charts. Live bars follow the trades as they happen, so reloading the chart can occasionally settle such a trade differently.
* Signals close together often ride the same move; the cooldown limits that, but *n* can still overstate the independent evidence.

## Install

### Mac (ATAS X)

On a Mac, ATAS runs as **ATAS X**. You build the indicator once with Microsoft's free .NET
tools, then copy one file into ATAS X.

1. Install **ATAS X** for macOS if you haven't already, in the usual Applications folder.
2. Install the **.NET 10 SDK** from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0)
   using the macOS installer: **Arm64** for Apple Silicon (M1–M4), **x64** for an Intel Mac.
   (Apple menu → About This Mac shows which chip you have.)
3. On this repository's GitHub page, click **Code → Download ZIP** and double-click the ZIP to unzip it.
4. Open **Terminal**, type `cd ` (with a space), drag the unzipped folder onto the Terminal
   window, press Return, then run:

   ```
   dotnet build FvgReactionLiquiditySweep.csproj -c Release
   ```

   It finds the ATAS X libraries in `/Applications/ATAS X.app/Contents/MonoBundle`. If ATAS X is
   somewhere else, add `-p:ATAS_BASE="/path/to/ATAS X.app/Contents/MonoBundle"`.
5. Copy the result into ATAS X's indicator folder:

   ```
   mkdir -p ~/Library/Application\ Support/ATAS/Indicators
   cp bin/Release/net10.0/FvgReactionLiquiditySweep.dll ~/Library/Application\ Support/ATAS/Indicators/
   ```

6. ATAS X loads new or updated indicator files automatically. Add **FVG Reaction + Liquidity
   Sweep** to a chart from the *My Indicators* group.

### Windows (classic ATAS Platform)

1. Build the DLL (needs ATAS installed and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

   ```
   dotnet build FvgReactionLiquiditySweep.csproj -c Release
   ```

   If ATAS is not in `C:\Program Files (x86)\ATAS Platform`, add `-p:ATAS_BASE="D:\path\to\ATAS Platform"`.
   Or drop `FvgReactionLiquiditySweep.cs` into an indicator project you already have.
2. Copy `bin\Release\net10.0-windows\FvgReactionLiquiditySweep.dll` into `%APPDATA%\ATAS\Indicators`
   (or `Documents\ATAS\Indicators`). ATAS builds that still run on .NET 8 need the DLL from
   `bin\Release\net8.0-windows` instead.
3. Restart ATAS and add **FVG Reaction + Liquidity Sweep** from the *My Indicators* group.

According to the ATAS docs, this Windows DLL also loads in ATAS X, since the indicator has no
custom WPF editors. On Windows you can also build a native ATAS X DLL with `-p:AtasX=true`.

The absorption heatmap and the absorption / delta confirmations read footprint data, so they
need a feed with tick data (e.g. Rithmic).

## Settings

Your original settings keep their names and defaults.

| Group | Setting | Default | |
|---|---|---|---|
| Liquidity Sweep | Swing Lookback (bars) | 10 | bars whose high / low a sweep must take out |
| Fair Value Gap | Min FVG Size (ticks) | 4 | |
| | Zone Max Age (bars) | 150 | lower it (or raise the min size) if zones pile up in strong trends |
| | Require reaction close beyond zone | on | |
| | Draw FVG Zones, zone colors | on | |
| Absorption | Show Absorption Heatmap | on | |
| | Min Level Volume / Volume Multiplier / Dominant Side Ratio / Heatmap Max Age | 150 / 3.0 / 0.6 / 100 | |
| Signals | Buy signals, Short signals | on | |
| | Signal source | FVG reaction or liquidity sweep | or FVG only, sweep only, sweep-then-FVG only |
| | Sweep → FVG window (bars) | 10 | |
| | Trend EMA period (0 = off) | 50 | |
| | Only trade with the trend / Require delta / Require absorption / Require candlestick pattern | off | |
| | Cooldown between signals (bars) | 3 | per direction |
| | One trade at a time | on | new signals while a trade is open are hidden but still learned from |
| | Min TP probability to show (%) | 0 | hide signals with lower TP odds; they are still learned from |
| | Min expected ticks to show (0 = off) | 0 | hide signals with a lower EV; they are still learned from |
| Candlestick Patterns | Use candlestick patterns | on | a pattern on the signal bar counts as the fourth confirmation |
| | Hammer / Shooting star, Inverted hammer / Hanging man, Engulfing, Piercing line / Dark cloud cover, Harami, Tweezer bottom / top, Morning star / Evening star, Three white soldiers / black crows, Marubozu | on | one switch per pattern pair |
| | Hammer wick / body (min) | 2 | for hammers, shooting stars, inverted hammers and hanging men |
| | Average body (bars) | 14 | the yardstick for long and small bodies |
| | Tweezer match (ticks) | 1 | how far apart a tweezer's two lows (highs) may be |
| Take Profit / Stop Loss | Take profit (ticks) / Stop loss (ticks) | 80 / 80 | |
| | Break-even trigger (ticks, 0 = off) | 40 | profit at which the stop moves |
| | Break-even stop (ticks in profit) | 20 | where it moves to (0 = the entry price); kept below the trigger |
| | Max bars in trade (0 = no limit) | 0 | expired trades are left out of the probabilities |
| | Close trades at session end | off | also takes no new signal on a session's last bar |
| | Order of high and low inside a bar | Open to the nearer extreme first | or worst case for the trade, or candle direction (O-L-H-C / O-H-L-C) |
| | Probability smoothing (virtual trades) | 10 | higher = steadier numbers that need more history to move |
| Display | labels, TP / SL levels, panel and its corner, label offset, font, TP / SL / break-even line style | | |
| Alerts | Alert on new signal / Alert on TP / SL / break-even / sound file | off / off / alert1 | the second also fires when a stop moves to break-even; only in real time, never while history loads |

The signal arrows are regular data series (*Buy Signal*, *Short Signal*), so ATAS can also
use them for alerts or automation.

## What changed from the original file

* **Compiles against the current ATAS API**: the file lacked the usings for `[Display]`
  (`System.ComponentModel.DataAnnotations`), `RenderContext` (`OFT.Rendering.Context`) and
  `RenderPen` (`OFT.Rendering.Tools`), and set series colors through WPF-only
  `System.Windows.Media.Colors`; colors now go through `.Convert()`, which works on classic
  ATAS and ATAS X.
* **No repainting or duplicate zones**: detection ran on every tick of the forming bar, so
  each tick re-added the same FVG, and arrows could appear mid-bar and stay even when the
  bar closed differently. Everything now runs once, on the closed bar.
* **The candle that completes a gap no longer counts as its own retest** (its low sits on
  the zone edge, so it was almost always flagged as a reaction).
* **Reacted zones are still invalidated** when price later closes through them, instead of
  staying on the chart until they age out.
* **Thread-safe rendering**: `OnRender` runs on a different thread than `OnCalculate`; shared
  data is now locked and copied before drawing, as in ATAS's own FairValueGap indicator.
* **Heatmap cells cover the whole bar** (they stopped at the bar's middle), sit on the right
  price row (they were half a tick high) and stay at least 3 px tall when zoomed out.
* Draws only the visible bars, redraws on every chart render (needed for the hover tooltip),
  resets through `OnRecalculate`, rejects zero-width "gaps" and keeps arrow series off the
  price axis.

## Tests

`tests/` builds the indicator against stand-ins for the ATAS API and checks it on scripted
and randomised markets — see [tests/README.md](tests/README.md). They are not part of the
indicator build.
