# FVG Reaction + Liquidity Sweep — ATAS indicator

A custom indicator for the [ATAS](https://atas.net) platform that marks Fair Value Gap
reactions, liquidity sweeps and footprint absorption, and turns them into **BUY / SHORT
signals with the probability of hitting the take profit (80 ticks) before the stop loss
(80 ticks)**.

![Layout preview](docs/preview.png)

*Layout preview produced by the test harness from synthetic data. It is not an ATAS
screenshot: ATAS draws the candles and arrows itself, with its own fonts and theme.*

## What it draws

| Element | Meaning |
|---|---|
| Green / red boxes | Unfilled bullish / bearish Fair Value Gaps (3-candle imbalances), extended to the right until price closes through them |
| Small arrows | Triggers: FVG reaction (lime / red) and liquidity sweep of lows / highs (aqua / magenta) |
| Orange / blue cells | Absorption heatmap from footprint data: orange = aggressive buying absorbed (often resistance), blue = aggressive selling absorbed (often support) |
| **Large arrows + label** | **BUY / SHORT signal** with its TP and SL probabilities |
| Shaded boxes | Each signal's trade: entry → TP shaded green, entry → SL shaded red, from the signal bar to the bar that settled it |
| Panel | Results, sample size, the labels' track record and the live odds of the open trade |

## Signals

A signal is decided when a bar **closes** (it never repaints) and needs a trigger:

* **FVG** – price came back into an unfilled gap and closed back out of it in the gap's direction, with a candle in that direction.
* **Sweep** – the bar wicked beyond the high / low of the last *Swing Lookback* bars and closed back inside (a stop run).
* **Sweep+FVG** – an FVG reaction within *Sweep → FVG window* bars after a sweep on the same side: liquidity is taken, then price reverses out of an imbalance.

Each signal also counts up to three confirmations: **absorption** of the opposite side
near the signal bar's extreme (sellers absorbed near the low for a buy, buyers absorbed
near the high for a short), **trend** (close above / below the EMA) and **delta** (bar
delta in the signal's direction). Any of them can be made mandatory.

Entry is the signal bar's close; TP and SL are *Take profit* / *Stop loss* ticks away.
With the default 80 / 80 on NQ that is 20 points each way ($400 per NQ contract, $40 per
MNQ).

### Where the probability comes from

Every signal on the chart is followed forward until the TP or the SL trades. When a new
signal fires, its probability is the share of **earlier, already finished** signals of the
same kind that hit TP first — so the number on an old signal is exactly what the indicator
would have shown live (no look-ahead).

"The same kind" is layered: same direction → same trigger → same number of
confirmations. A narrow group with only a few past trades is blended with its broader
parent group (`p = (wins + k·p_parent) / (trades + k)`, *k* = *Probability smoothing*).
The starting point is SL / (TP + SL) — the odds of a coin-flip market, **50% for an 80 / 80
bracket** — so a fresh chart shows 50% everywhere and the numbers only move as real
outcomes accumulate. Load more history for more reliable estimates.

### Reading the chart

```
BUY  TP 62% | SL 38%            [OPEN]   <- probability of TP first / SL first, result badge
Sweep+FVG | conf 2/3 | n=14              <- trigger, confirmations, past trades of this exact setup
```

The badge turns **TP** (green), **SL** (red) or **EXP** (expired) once the trade settles.
Hover a label for the full breakdown: prices, the win counts behind the estimate at each
level, which confirmations were present and how the trade ended.

By default every signal is shown, weak ones included — a BUY at 22% tells you that setup
has mostly run into its stop. Once the panel's track record shows the labels holding up on
your chart, set **Min TP probability to show** (e.g. 55–60%) to keep only the better odds on
screen; hidden signals are still followed, so the statistics keep learning from them.

The panel shows:

* wins / losses, win rate and net ticks for longs, shorts and total (signals shown on the chart);
* open, expired and hidden signals, and how many settled trades the model has learned from;
* **track record** — how often signals labelled ≥ 60% and ≤ 40% actually hit TP, so you can see whether the labels have been reliable on this chart;
* for an open trade, **live odds** from the current price (a random walk with the drift implied by the entry probability — at +40 ticks on a 50% signal it reads 75%).

### Honest limits

* The probability is a frequency from the history loaded on the chart, not a guarantee. Markets change; small samples stay close to 50% by design.
* Fills are idealised: entry at the close, exact TP / SL, no slippage or commission.
* On historical bars the order of the high and low inside one bar is unknown. If TP and SL are both inside a bar, the conservative default counts it as a stop loss. Live bars are settled tick by tick, so reloading the chart can occasionally settle such a trade differently.
* Signals close together often ride the same move; the cooldown limits that, but *n* can still overstate the independent evidence.

## Install

1. Build the DLL on Windows (needs ATAS installed and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

   ```
   dotnet build FvgReactionLiquiditySweep.csproj -c Release
   ```

   If ATAS is not in `C:\Program Files (x86)\ATAS Platform`, add `-p:ATAS_BASE="D:\path\to\ATAS Platform"`.
   Or drop `FvgReactionLiquiditySweep.cs` into an indicator project you already have.
2. Copy `bin\Release\net10.0-windows\FvgReactionLiquiditySweep.dll` into `%APPDATA%\ATAS\Indicators`
   (or `Documents\ATAS\Indicators`). ATAS builds that still run on .NET 8 need the DLL from
   `bin\Release\net8.0-windows` instead.
3. Restart ATAS and add **FVG Reaction + Liquidity Sweep** from the *My Indicators* group.

The absorption heatmap and the absorption / delta confirmations read footprint data, so they
need a feed with tick data (e.g. Rithmic).

The project targets classic ATAS. For ATAS X, reference the DLLs from the ATAS X folder and
target `net10.0` without WPF, as in the official
[AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators) project.

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
| | Only trade with the trend / Require delta / Require absorption | off | |
| | Cooldown between signals (bars) | 3 | per direction |
| | One trade at a time | on | new signals while a trade is open are hidden but still learned from |
| | Min TP probability to show (%) | 0 | hide weaker signals; they are still learned from |
| Take Profit / Stop Loss | Take profit (ticks) / Stop loss (ticks) | 80 / 80 | |
| | Max bars in trade (0 = no limit) | 0 | expired trades are left out of the probabilities |
| | Close trades at session end | off | also takes no new signal on a session's last bar |
| | TP and SL inside one bar | Stop loss first | or decide by candle direction (O-L-H-C / O-H-L-C) |
| | Probability smoothing (virtual trades) | 10 | higher = steadier numbers that need more history to move |
| Display | labels, TP / SL levels, panel and its corner, label offset, font, TP / SL line style | | |
| Alerts | Alert on new signal / Alert when TP / SL is hit / sound file | off / off / alert1 | only in real time, never while history loads |

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
